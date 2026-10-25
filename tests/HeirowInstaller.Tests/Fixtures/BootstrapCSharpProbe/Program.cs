using System.Text.Json;
using SocketJack;

if (args.Length == 2 && args[0] == "--first-session-root")
{
    var root = Guard(args[1]);
    if (!HeirowUpdaterBootstrap.StartAfterExit(appRoot: root)) return 6;
    var deadline = DateTime.UtcNow.AddSeconds(40);
    while (!File.Exists(Path.Combine(root, "capture.flag")) && !File.Exists(Path.Combine(root, "exit.flag")) && DateTime.UtcNow < deadline) await Task.Delay(20);
    if (File.Exists(Path.Combine(root, "exit.flag"))) return 0;
    if (!File.Exists(Path.Combine(root, "capture.flag"))) return 7;
    // This fresh process has made no earlier capture. Exercise the actual BCL
    // helper once, after its credential-free watcher is already waiting.
    var first = HeirowUpdaterBootstrap.CaptureSession(bearerToken: "fixture-first-bootstrap-native");
    var metadata = new System.Diagnostics.ProcessStartInfo("fixture-never-executed"); first.Apply(metadata);
    var receipt = Path.Combine(root, "capture.json");
    File.WriteAllText(receipt + ".tmp", JsonSerializer.Serialize(new
    {
        sequence = long.Parse(metadata.Environment["HEIROWUPDATER_SESSION_SEQUENCE"]!),
        sourcePid = int.Parse(metadata.Environment["HEIROWUPDATER_SESSION_SOURCE_PID"]!),
        sourceStart = long.Parse(metadata.Environment["HEIROWUPDATER_SESSION_SOURCE_START"]!),
        capturedAt = long.Parse(metadata.Environment["HEIROWUPDATER_SESSION_CAPTURED_AT"]!),
        snapshotId = metadata.Environment["HEIROWUPDATER_SESSION_ID"],
        fixtureBearerApplied = metadata.Environment["HEIROWUPDATER_TOKEN"] == "fixture-first-bootstrap-native"
    }));
    File.Move(receipt + ".tmp", receipt);
    while (!File.Exists(Path.Combine(root, "exit.flag")) && DateTime.UtcNow < deadline) await Task.Delay(20);
    return File.Exists(Path.Combine(root, "exit.flag")) ? 0 : 9;
}
if (args.Length == 2 && args[0] == "--capture-root")
{
    var root = Guard(args[1]);
    Environment.SetEnvironmentVariable("HEIROWUPDATER_TOKEN", "fixture-inherited-browser");
    var snapshot = HeirowUpdaterBootstrap.CaptureSession(sessionCookie: "wshare_hash=fixture-bootstrap-native");
    await Task.Delay(150); // A queued app startup must retain the earlier capture metadata.
    var queuedAt = DateTime.UtcNow.Ticks;
    if (!HeirowUpdaterBootstrap.CheckForUpdates(snapshot, root)) return 2;
    var deadline = DateTime.UtcNow.AddSeconds(15);
    var receipt = Path.Combine(root, "child.json");
    while (!File.Exists(receipt) && DateTime.UtcNow < deadline) await Task.Delay(20);
    if (!File.Exists(receipt)) return 3;
    using var result = JsonDocument.Parse(File.ReadAllText(receipt));
    var data = result.RootElement;
    return data.GetProperty("cookieCorrect").GetBoolean() && data.GetProperty("noBearer").GetBoolean() &&
        data.GetProperty("capturedAt").GetInt64() < queuedAt && data.GetProperty("sourcePid").GetInt32() == Environment.ProcessId &&
        data.GetProperty("sequence").GetInt64() > 0 ? 0 : 4;
}
var configIndex = Array.IndexOf(args, "--config");
if (args.FirstOrDefault() != "check-for-updates" || configIndex < 0 || configIndex + 1 >= args.Length) return 5;
var childRoot = Guard(Path.GetDirectoryName(Path.GetFullPath(args[configIndex + 1]))!);
var temporary = Path.Combine(childRoot, "child.json.tmp");
File.WriteAllText(temporary, JsonSerializer.Serialize(new
{
    cookieCorrect = Environment.GetEnvironmentVariable("HEIROWUPDATER_COOKIE") == "wshare_hash=fixture-bootstrap-native",
    noBearer = Environment.GetEnvironmentVariable("HEIROWUPDATER_TOKEN") is null,
    capturedAt = long.Parse(Environment.GetEnvironmentVariable("HEIROWUPDATER_SESSION_CAPTURED_AT")!),
    sourcePid = int.Parse(Environment.GetEnvironmentVariable("HEIROWUPDATER_SESSION_SOURCE_PID")!),
    sequence = long.Parse(Environment.GetEnvironmentVariable("HEIROWUPDATER_SESSION_SEQUENCE")!)
}));
File.Move(temporary, Path.Combine(childRoot, "child.json"));
return 0;

static string Guard(string path)
{
    var root = Path.GetFullPath(path);
    if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !root.Contains("heirowInstaller-tests-", StringComparison.Ordinal) ||
        File.ReadAllText(Path.Combine(root, ".bootstrap-probe")) != "heirowInstaller bootstrap acceptance\n") throw new InvalidDataException("Invalid fixture root.");
    for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid fixture root.");
    return root;
}
