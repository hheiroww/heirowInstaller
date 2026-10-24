using System.Text.Json;

var report = args.Single(x => x.StartsWith("--report=", StringComparison.Ordinal))["--report=".Length..];
if (!Path.IsPathFullyQualified(report)) return 2;
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
File.WriteAllText(report, JsonSerializer.Serialize(args));
return 0;
