using System.Text.Json;

if (args.Length != 2 || args[0] != "--run-directory" || !Path.IsPathFullyQualified(args[1])) return 2;
var root = Path.GetFullPath(args[1]);
if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !root.Contains("heirowInstaller-tests-", StringComparison.Ordinal)) return 3;
for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return 4;
if (File.ReadAllText(Path.Combine(root, ".manual-check-probe")) != "heirowInstaller manual feedback acceptance\n") return 5;
var ready = Path.Combine(root, "ready.json");
File.WriteAllText(ready + ".tmp", JsonSerializer.Serialize(new { processId = Environment.ProcessId }));
File.Move(ready + ".tmp", ready);
var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
while (!File.Exists(Path.Combine(root, "exit.flag")) && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
return 0;
