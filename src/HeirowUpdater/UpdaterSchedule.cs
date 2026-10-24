using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using HeirowInstaller.Core;

namespace HeirowUpdater;

internal sealed class ScheduleConfiguration
{
    public int IntervalMinutes { get; set; } = 60;
    public bool Apply { get; set; }
    public bool AllowUnsigned { get; set; }
    public string? StateRoot { get; set; }
    public string? TrustedPublisherKeySha256 { get; set; }
    public string? CookieEnvironmentVariable { get; set; }
    public string? TokenEnvironmentVariable { get; set; }
    public string CredentialOrigin { get; set; } = "https://socketjack.com";
    public string? Executable { get; set; }
}

internal static class UpdaterSchedule
{
    private const string Description = "SocketJack heirowUpdater per-user application update checks.";
    public static string SettingsDirectory => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowUpdater");
    public static string ConfigurationPath => Path.Combine(SettingsDirectory, "schedule.json");
    public static string LastCheckPath => Path.Combine(SettingsDirectory, "last-check.json");
    private static string TaskName
    {
        get
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Background scheduling requires Windows Task Scheduler.");
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current Windows user has no SID.");
            return "heirowUpdater." + sid;
        }
    }
    private static string ResolveTaskName(string? name)
    {
        if (name is null) return TaskName;
        if (!name.StartsWith("heirowUpdater.Test.", StringComparison.Ordinal) || name.Length > 100 || name.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '-' or '_')))
            throw new ArgumentException("Custom scheduler validation task names must begin with heirowUpdater.Test. and contain only letters, digits, dots, hyphens or underscores.");
        return name;
    }
    public static async Task<object> StatusAsync(CancellationToken token, string? taskName = null, string? configurationPath = null)
    {
        if (!OperatingSystem.IsWindows()) return new { enabled = false, available = false, reason = "Windows Task Scheduler is unavailable." };
        var name = ResolveTaskName(taskName);
        var (code, xml) = await RunAsync(["/Query", "/TN", name, "/XML"], token);
        var config = ReadConfiguration(configurationPath);
        var owned = code == 0 && IsOwned(xml);
        var enabled = owned && XDocument.Parse(xml).Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "Settings")?.Elements().FirstOrDefault(x => x.Name.LocalName == "Enabled")?.Value != "false";
        return new { enabled, taskName = name, intervalMinutes = config?.IntervalMinutes ?? 60,
            apply = config?.Apply ?? false, allowUnsigned = config?.AllowUnsigned ?? false, configurationPath = configurationPath ?? ConfigurationPath,
            lastCheckPath = File.Exists(LastCheckPath) ? LastCheckPath : null, conflict = code == 0 && !owned };
    }
    public static ScheduleConfiguration? ReadConfiguration(string? path = null)
    {
        path ??= ConfigurationPath;
        if (!File.Exists(path)) return null;
        PackageSafety.EnsureNoReparsePoints(path);
        var result = JsonSerializer.Deserialize<ScheduleConfiguration>(File.ReadAllText(path), InstallerJson.Options) ?? throw new InvalidDataException("Updater configuration is empty.");
        Validate(result); return result;
    }
    public static async Task EnableAsync(ScheduleConfiguration configuration, CancellationToken token, string? taskName = null, string? configurationPath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Background scheduling requires Windows.");
        Validate(configuration);
        var name = ResolveTaskName(taskName);
        configurationPath = Path.GetFullPath(configurationPath ?? ConfigurationPath);
        var (queryCode, existing) = await RunAsync(["/Query", "/TN", name, "/XML"], token);
        if (queryCode == 0 && !IsOwned(existing)) throw new InvalidOperationException("Another application owns the configured Task Scheduler name.");
        var executable = configuration.Executable ?? FindExecutable();
        if (!File.Exists(executable)) throw new FileNotFoundException("The heirowUpdater executable is missing.");
        configuration.Executable = Path.GetFullPath(executable);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? content = null) => new(ns + name, content);
        var document = new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.4"),
            E("RegistrationInfo", new[] { E("Description", Description), E("Author", "SocketJack heirowUpdater") }),
            E("Triggers", new[]
            {
                E("LogonTrigger", new[] { E("Enabled", "true"), E("UserId", sid), E("Delay", "PT1M") }),
                E("TimeTrigger", new[] { E("Repetition", new[] { E("Interval", "PT" + configuration.IntervalMinutes + "M"), E("StopAtDurationEnd", "false") }), E("StartBoundary", DateTime.Now.AddMinutes(1).ToString("s")), E("Enabled", "true") })
            }),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "UpdaterUser"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("AllowHardTerminate", "false"), E("StartWhenAvailable", "true"), E("RunOnlyIfNetworkAvailable", "true"), E("Enabled", "true"), E("ExecutionTimeLimit", "PT30M") }),
            new XElement(ns + "Actions", new XAttribute("Context", "UpdaterUser"), E("Exec", new[] { E("Command", configuration.Executable), E("Arguments", "check --all --background --config " + Quote(configurationPath) + (taskName is not null ? " --status-file " + Quote(Path.Combine(Path.GetDirectoryName(configurationPath)!, "last-check.json")) : "")), E("WorkingDirectory", Path.GetDirectoryName(configuration.Executable)) }))));
        PackageSafety.EnsureNoReparsePoints(configurationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        var previous = File.Exists(configurationPath) ? await File.ReadAllBytesAsync(configurationPath, token) : null;
        var xmlPath = Path.Combine(Path.GetDirectoryName(configurationPath)!, "schedule-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await WriteJsonAsync(configurationPath, configuration, token);
            await File.WriteAllTextAsync(xmlPath, document.ToString(), token);
            var (code, _) = await RunAsync(["/Create", "/TN", name, "/XML", xmlPath, "/F"], token);
            if (code != 0) throw new InvalidOperationException("Windows Task Scheduler could not register the per-user updater task.");
        }
        catch
        {
            if (previous is not null) await File.WriteAllBytesAsync(configurationPath, previous, CancellationToken.None);
            else if (File.Exists(configurationPath)) File.Delete(configurationPath);
            throw;
        }
        finally { if (File.Exists(xmlPath)) File.Delete(xmlPath); }
    }
    public static async Task DisableAsync(CancellationToken token, string? taskName = null)
    {
        var name = ResolveTaskName(taskName);
        var (code, xml) = await RunAsync(["/Query", "/TN", name, "/XML"], token);
        if (code != 0) return;
        if (!IsOwned(xml)) throw new InvalidOperationException("Another application owns this task; it was not changed.");
        var (deleteCode, _) = await RunAsync(["/Delete", "/TN", name, "/F"], token);
        if (deleteCode != 0) throw new InvalidOperationException("Windows Task Scheduler could not disable the updater task.");
    }
    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, InstallerJson.Options), token); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(ScheduleConfiguration configuration)
    {
        if (configuration.IntervalMinutes is < 15 or > 1440) throw new ArgumentException("Update check interval must be between 15 and 1440 minutes.");
        if (configuration.TrustedPublisherKeySha256 is { } key && (key.Length != 64 || !key.All(Uri.IsHexDigit))) throw new ArgumentException("Trusted publisher key must be a SHA256 fingerprint.");
        foreach (var name in new[] { configuration.CookieEnvironmentVariable, configuration.TokenEnvironmentVariable })
            if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.IndexOfAny(['\0', '\r', '\n']) >= 0)) throw new ArgumentException("Credential settings must be environment variable names.");
    }
    private static bool IsOwned(string xml)
    {
        try { return XDocument.Parse(xml).Descendants().Any(x => x.Name.LocalName == "Description" && x.Value == Description); }
        catch (System.Xml.XmlException) { return false; }
    }
    private static async Task<(int Code, string Output)> RunAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Task Scheduler command could not start.");
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); await error;
        return (process.ExitCode, await output);
    }
    private static string FindExecutable()
    {
        var process = System.Environment.ProcessPath;
        if (process is not null && Path.GetFileNameWithoutExtension(process).Equals("heirowUpdater", StringComparison.OrdinalIgnoreCase)) return process;
        return Path.Combine(AppContext.BaseDirectory, "heirowUpdater.exe");
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
