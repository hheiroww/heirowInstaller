#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace SocketJack;

/// <summary>Optional app-root updater integration. Call StartAfterExit once during app startup.</summary>
public static class HeirowUpdaterBootstrap
{
    private static long sequence;
    public static HeirowUpdaterSession CaptureSession(string? bearerToken = null, string? sessionCookie = null) => new(bearerToken, sessionCookie, Interlocked.Increment(ref sequence));
    public static bool StartAfterExit(string? bearerToken = null, string? sessionCookie = null, string? appRoot = null, bool replaceCredentials = false) => Start("after-exit", replaceCredentials || bearerToken is not null || sessionCookie is not null ? CaptureSession(bearerToken, sessionCookie) : null, appRoot);
    public static bool CheckForUpdates(string? bearerToken = null, string? sessionCookie = null, string? appRoot = null, bool replaceCredentials = false) => Start("check-for-updates", replaceCredentials || bearerToken is not null || sessionCookie is not null ? CaptureSession(bearerToken, sessionCookie) : null, appRoot);
    public static bool StartAfterExit(HeirowUpdaterSession session, string? appRoot = null) => Start("after-exit", session, appRoot);
    public static bool CheckForUpdates(HeirowUpdaterSession session, string? appRoot = null) => Start("check-for-updates", session, appRoot);

    private static bool Start(string command, HeirowUpdaterSession? session, string? appRoot)
    {
        try
        {
            var host = Environment.ProcessPath;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appRoot ?? (host is not null && !Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(host)! : AppContext.BaseDirectory)));
            var configuration = Path.Combine(root, "heirowUpdater.json");
            if (!File.Exists(configuration)) return false;
            var updater = Resolve(root, configuration);
            if (!File.Exists(updater)) return false;
            using var application = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            start.ArgumentList.Add(command);
            start.ArgumentList.Add("--config"); start.ArgumentList.Add(configuration);
            start.ArgumentList.Add("--application-pid"); start.ArgumentList.Add(application.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--application-start"); start.ArgumentList.Add(application.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            if (host is not null && !Path.GetFullPath(host).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { start.ArgumentList.Add("--application-host"); start.ArgumentList.Add(Path.GetFullPath(host)); }
            foreach (var name in new[] { "HEIROWUPDATER_TOKEN", "HEIROWUPDATER_COOKIE", "HEIROWUPDATER_SESSION_SUPPLIED", "HEIROWUPDATER_SESSION_CAPTURED_AT", "HEIROWUPDATER_SESSION_ID", "HEIROWUPDATER_SESSION_SOURCE_PID", "HEIROWUPDATER_SESSION_SOURCE_START", "HEIROWUPDATER_SESSION_SEQUENCE" }) start.Environment.Remove(name);
            session?.Apply(start);
            using var companion = Process.Start(start);
            return companion is not null;
        }
        catch { return false; } // Optional updating must never stop the application from opening.
    }

    private static string Resolve(string root, string configuration)
    {
        NoLinks(configuration);
        if (new FileInfo(configuration).Length > 65536) throw new InvalidDataException("Updater profile is too large.");
        using var document = JsonDocument.Parse(File.ReadAllText(configuration)); var profile = document.RootElement;
        if (!Path.GetFullPath(profile.GetProperty("installDirectory").GetString()!).Equals(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Updater root mismatch.");
        if (!profile.TryGetProperty("sharedRuntime", out var runtime) || runtime.ValueKind == JsonValueKind.Null) return Path.Combine(root, "heirowUpdater.exe");
        var version = runtime.GetProperty("version").GetString()!; var hash = runtime.GetProperty("sha256").GetString()!; var size = runtime.GetProperty("size").GetInt64();
        if (!Version.TryParse(version, out _) || version.Length > 64 || version.Any(c => !char.IsAsciiDigit(c) && c != '.') || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || size <= 0 || size > 536870912) throw new InvalidDataException("Invalid updater runtime binding.");
        var scope = profile.GetProperty("scope").GetString();
        if (scope is not ("user" or "machine")) throw new InvalidDataException("Invalid updater scope.");
        var isolated = profile.TryGetProperty("isolated", out var isolatedValue) && isolatedValue.GetBoolean();
        var parent = isolated ? Path.GetDirectoryName(Path.GetFullPath(profile.GetProperty("stateRoot").GetString()!))! : scope == "machine"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "heirowInstaller", "state")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "state");
        var executable = Path.Combine(parent, ".updater-runtime", version + "-" + hash.ToLowerInvariant(), "heirowUpdater.exe");
        NoLinks(executable);
        using var input = File.OpenRead(executable);
        if (input.Length != size || !Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase) || ReadFileVersion(executable) != version) throw new CryptographicException("Updater runtime integrity failed.");
        return executable;
    }
    private static string? ReadFileVersion(string path)
    {
        // Version metadata uses a native API that requires an extended path for long Windows filenames.
        var absolute = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && !absolute.StartsWith(@"\\?\", StringComparison.Ordinal))
            absolute = absolute.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + absolute[2..] : @"\\?\" + absolute;
        return FileVersionInfo.GetVersionInfo(absolute).FileVersion;
    }
    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Updater paths cannot follow links.");
    }
}

/// <summary>Capture before queueing background process startup. An empty snapshot explicitly signs the updater out.</summary>
public sealed class HeirowUpdaterSession
{
    private readonly string? bearer, cookie;
    private readonly long captured = DateTime.UtcNow.Ticks, sourceStart, sequence;
    private readonly int sourcePid = Environment.ProcessId;
    private readonly string id = Guid.NewGuid().ToString("N");
    internal HeirowUpdaterSession(string? bearer, string? cookie, long sequence)
    {
        foreach (var value in new[] { bearer, cookie }) if (value is not null && (value.Length > 6144 || value.Any(c => c < 32 || c == 127))) throw new ArgumentException("Invalid session header data.");
        this.bearer = bearer; this.cookie = cookie; this.sequence = sequence;
        using var application = Process.GetCurrentProcess(); sourceStart = application.StartTime.ToUniversalTime().Ticks;
    }
    internal void Apply(ProcessStartInfo start)
    {
        start.Environment["HEIROWUPDATER_SESSION_SUPPLIED"] = "1";
        start.Environment["HEIROWUPDATER_SESSION_CAPTURED_AT"] = captured.ToString(CultureInfo.InvariantCulture);
        start.Environment["HEIROWUPDATER_SESSION_ID"] = id;
        start.Environment["HEIROWUPDATER_SESSION_SOURCE_PID"] = sourcePid.ToString(CultureInfo.InvariantCulture);
        start.Environment["HEIROWUPDATER_SESSION_SOURCE_START"] = sourceStart.ToString(CultureInfo.InvariantCulture);
        start.Environment["HEIROWUPDATER_SESSION_SEQUENCE"] = sequence.ToString(CultureInfo.InvariantCulture);
        if (bearer is not null) start.Environment["HEIROWUPDATER_TOKEN"] = bearer;
        if (cookie is not null) start.Environment["HEIROWUPDATER_COOKIE"] = cookie;
    }
    public override string ToString() => "Application session snapshot (redacted)";
}
