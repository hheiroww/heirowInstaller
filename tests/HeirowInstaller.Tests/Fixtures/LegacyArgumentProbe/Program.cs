using System.Text.Json;

var report = args.Single(x => x.StartsWith("--report=", StringComparison.Ordinal))["--report=".Length..];
if (!Path.IsPathFullyQualified(report)) return 2;
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
File.WriteAllText(report, JsonSerializer.Serialize(args));
if (args.FirstOrDefault(x => x.StartsWith("--fixture-arp-key=", StringComparison.Ordinal)) is { } registration)
{
    if (!OperatingSystem.IsWindows()) return 3;
    const string prefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\heirowInstaller.Tests.";
    var path = registration["--fixture-arp-key=".Length..];
    var target = args.Single(x => x.StartsWith("--fixture-target=", StringComparison.Ordinal))["--fixture-target=".Length..];
    if (!path.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(path[prefix.Length..], out var identity) ||
        !Path.IsPathFullyQualified(target) || !Path.GetFullPath(target).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !target.Contains("heirowInstaller-tests-", StringComparison.Ordinal)) return 4;
    using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
    using (var existing = hive.OpenSubKey(path))
        if (existing is not null && existing.GetValue("HeirowInstallerAcceptanceFixture") as string != identity.ToString()) return 5;
    if (args.Contains("--fixture-uninstall"))
    {
        hive.DeleteSubKeyTree(path, false);
        File.Delete(Path.Combine(target, "application.exe"));
    }
    else
    {
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "application.exe"), "non-executed acceptance application");
        using var key = hive.CreateSubKey(path);
        key.SetValue("HeirowInstallerAcceptanceFixture", identity.ToString());
        key.SetValue("DisplayName", "heirowInstaller acceptance " + identity);
        key.SetValue("SystemComponent", 0, Microsoft.Win32.RegistryValueKind.DWord);
        if (args.FirstOrDefault(x => x.StartsWith("--fixture-burn-upgrade=", StringComparison.Ordinal)) is { } burn)
            key.SetValue("BundleUpgradeCode", new[] { burn["--fixture-burn-upgrade=".Length..] }, Microsoft.Win32.RegistryValueKind.MultiString);
    }
}
return 0;
