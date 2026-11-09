using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HeirowInstaller.Core;

// Elevated CI must exercise the same signed-image owner check as production.
// The coordinator is installed as signed fixture payload and exits after the
// helper acknowledges its live kernel-bound proof, before replacement starts.
internal static class SignedHandoffOwner
{
    private sealed record Request(string Executable, string[] Arguments, string Directory, UpdaterOwnerHandshake.Binding Binding);
    private sealed record Child(int Pid, long Start);

    internal static void CopyPayload(string destination)
    {
        var host = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        if (!File.Exists(host)) throw new FileNotFoundException("The fixture needs its own Windows apphost.", host);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path =>
            Path.GetExtension(path) is ".dll" or ".json" || path.Equals(host, StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    internal static async Task<(int ExitCode, string Diagnostic)> ExecuteAsync(string installedDirectory, string area,
        ProcessStartInfo helper, UpdaterOwnerHandshake.Binding binding)
    {
        var arguments = helper.ArgumentList.ToList();
        foreach (var name in new[] { "--owner-pipe", "--owner-nonce", "--owner-pid", "--owner-start" })
        { var index = arguments.IndexOf(name); if (index >= 0) arguments.RemoveRange(index, 2); }
        var request = Path.Combine(area, "owner-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(request, JsonSerializer.Serialize(new Request(helper.FileName, arguments.ToArray(), helper.WorkingDirectory, binding)));
        var executable = Path.Combine(installedDirectory, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--owned-handoff"); start.ArgumentList.Add(request);
        using var owner = Process.Start(start) ?? throw new InvalidOperationException("Could not start signed fixture coordinator.");
        var output = owner.StandardOutput.ReadToEndAsync(); var error = owner.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var receipt = request + ".child";
        while (!File.Exists(receipt)) { if (owner.HasExited) throw new Exception(await error); await Task.Delay(10, timeout.Token); }
        Child? child = null;
        while (child is null) { try { child = JsonSerializer.Deserialize<Child>(File.ReadAllText(receipt)); } catch (JsonException) { await Task.Delay(10, timeout.Token); } }
        using var process = Process.GetProcessById(child.Pid);
        if (process.StartTime.ToUniversalTime().Ticks != child.Start) throw new InvalidDataException("Fixture child identity changed.");
        _ = process.Handle; // Retain this exact process even if it exits before the acknowledgement.
        File.WriteAllText(request + ".ready", "captured");
        await owner.WaitForExitAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
        _ = await output.WaitAsync(timeout.Token);
        return (process.ExitCode, await error.WaitAsync(timeout.Token));
    }

    internal static async Task<int> RunAsync(string path)
    {
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing fixture request.");
        var start = new ProcessStartInfo(request.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = request.Directory };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var current = Process.GetCurrentProcess();
        foreach (var (name, value) in new[] { ("--wait-pid", current.Id.ToString()), ("--wait-start", current.StartTime.ToUniversalTime().Ticks.ToString()) })
            start.ArgumentList[start.ArgumentList.IndexOf(name) + 1] = value;
        UpdaterOwnerHandshake.Attach(start, current.Id, current.StartTime.ToUniversalTime().Ticks, request.Binding);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned helper.");
        File.WriteAllText(path + ".child", JsonSerializer.Serialize(new Child(process.Id, process.StartTime.ToUniversalTime().Ticks)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!File.Exists(path + ".ready")) await Task.Delay(10, timeout.Token);
        var accepted = UpdaterOwnerHandshake.WaitForAcceptanceAsync(start, timeout.Token);
        var exited = process.WaitForExitAsync(timeout.Token);
        if (await Task.WhenAny(accepted, exited) == accepted) { await accepted; return 0; }
        await exited; return process.ExitCode;
    }
}
