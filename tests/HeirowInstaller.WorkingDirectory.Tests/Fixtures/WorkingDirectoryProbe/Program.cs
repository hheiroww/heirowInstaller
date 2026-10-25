using System.Diagnostics;
using System.Text.Json;

var root = Path.GetDirectoryName(Environment.ProcessPath!)!;
if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
    !root.Contains("heirowInstaller-working-directory-", StringComparison.Ordinal) ||
    !File.Exists(Path.Combine(root, ".working-directory-probe")) ||
    File.ReadAllText(Path.Combine(root, ".working-directory-probe")) != "heirowInstaller working directory acceptance\n") return 2;
for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
    if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return 3;
using var process = Process.GetCurrentProcess();
var temporary = Path.Combine(root, "opened.json.tmp");
File.WriteAllText(temporary, JsonSerializer.Serialize(new { processId = process.Id, startTicks = process.StartTime.ToUniversalTime().Ticks,
    executable = Environment.ProcessPath, workingDirectory = Environment.CurrentDirectory }));
File.Move(temporary, Path.Combine(root, "opened.json"));
var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
while (!File.Exists(Path.Combine(root, "allow-exit")) && DateTimeOffset.UtcNow < deadline) await Task.Delay(40);
return 0;
