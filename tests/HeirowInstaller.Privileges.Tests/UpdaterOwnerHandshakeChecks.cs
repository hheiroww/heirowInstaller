using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using HeirowInstaller.Core;

internal static class UpdaterOwnerHandshakeChecks
{
    private static UpdaterOwnerHandshake.Binding Binding(string root) => new("fixture.owner", Path.Combine(root, "application"),
        Path.Combine(root, "state"), "user", new string('a', 64), Path.Combine(root, "prepared"));

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var mode in new[] { "valid", "wrong-server", "stale-start", "nonce", "target", "missing" })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(UpdaterOwnerHandshakeChecks).Assembly.Location);
            start.ArgumentList.Add("--owner-handshake-child"); start.ArgumentList.Add(root); start.ArgumentList.Add(mode);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // A separate arbitrary wait dependency is deliberately unable to select the captured user token.
            UpdaterOwnerHandshake.Attach(start, 1, 1, Binding(root), lifetime.Token);
            using var self = Process.GetCurrentProcess();
            check(int.Parse(start.ArgumentList[start.ArgumentList.IndexOf("--owner-pid") + 1]) == self.Id,
                "owner proof derives the initiating PID independently of the supplied wait-only dependency");
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var text = await output; var diagnostic = await error;
            check(child.ExitCode == 0 && text.Trim() == (mode == "valid" ? "ACCEPT" : "REJECT"), "real owner-pipe child " + mode + " obeyed the kernel and exact target binding" + (child.ExitCode != 0 ? ": " + diagnostic : ""));
            if (mode == "valid")
            {
                await UpdaterOwnerHandshake.WaitForAcceptanceAsync(start, lifetime.Token);
                check(true, "coordinator retained its actual token until the authenticated child acknowledged acceptance");
            }
            else
            {
                lifetime.Cancel();
                try { await UpdaterOwnerHandshake.WaitForAcceptanceAsync(start); throw new InvalidOperationException("Rejected child was accepted by its owner."); }
                catch (Exception rejected) when (rejected is IOException or OperationCanceledException or CryptographicException) { check(true, "rejected owner proof never acknowledged consent"); }
            }
        }
    }

    internal static async Task<int> ChildAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 9;
        var root = args[1]; var mode = args[2];
        string Value(string name) => args[Array.IndexOf(args, name) + 1];
        var pid = int.Parse(Value("--owner-pid"), CultureInfo.InvariantCulture); var ticks = long.Parse(Value("--owner-start"), CultureInfo.InvariantCulture);
        var nonce = Value("--owner-nonce"); var name = Value("--owner-pipe"); var binding = Binding(root);
        if (mode == "wrong-server") pid = 1;
        if (mode == "stale-start") ticks++;
        if (mode == "nonce") nonce = new string('0', 64);
        if (mode == "target") binding = binding with { TargetManifestSha256 = new string('b', 64) };
        try
        {
            using var accepted = await UpdaterOwnerHandshake.AuthenticateAsync(mode == "missing" ? null : name, nonce, pid, ticks, binding);
            using var current = WindowsIdentity.GetCurrent();
            if (mode != "valid" || accepted is null || accepted.User.ProcessId != pid || accepted.User.UserSid != current.User?.Value) return 3;
            await accepted.AcceptAsync(); Console.WriteLine("ACCEPT"); return 0;
        }
        catch (Exception rejected) when (mode != "valid" && rejected is InvalidDataException or CryptographicException or IOException)
        { Console.WriteLine("REJECT"); return 0; }
    }
}
