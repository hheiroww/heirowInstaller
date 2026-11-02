using System.Security;
using System.Runtime.Versioning;
using System.Text;

namespace HeirowInstaller.Core;

[UnsupportedOSPlatform("windows")]
internal static class PortableIntegration
{
    internal static Task<List<ModuleSnapshot>> ApplyAsync(PackageManifest manifest, InstallPlan plan, InstallTransaction transaction,
        CancellationToken token, InstalledProduct? product)
    {
        var snapshots = new List<ModuleSnapshot>();
        for (var index = 0; index < manifest.Shortcuts.Count; index++)
        {
            var definition = manifest.Shortcuts[index];
            if (!InstallerEngine.IsSelected(definition.Feature, plan.Features)) continue;
            token.ThrowIfCancellationRequested();
            var target = PackageSafety.UnderRoot(plan.InstallDirectory, definition.Target);
            if (!File.Exists(target)) throw new FileNotFoundException("Shortcut target is missing.");
            var arguments = definition.ArgumentList?.ToList() ?? [];
            if (plan.UpdateCompanionEnabled && definition.Target == manifest.EntryPoint)
            {
                var companion = manifest.UpdateCompanion!;
                target = companion.RuntimeMode == "shared" ? SharedUpdaterRuntime.Resolve(InstallerEngine.ReadBoundCompanionConfiguration(product!, manifest))
                    : PackageSafety.UnderRoot(plan.InstallDirectory, companion.ExecutablePath);
                arguments.InsertRange(0, ["launch", "--config", PackageSafety.UnderRoot(plan.InstallDirectory, companion.ConfigurationPath), "--"]);
            }
            var working = PackageSafety.ResolveWorkingDirectory(plan.InstallDirectory, definition.WorkingDirectory);
            if (OperatingSystem.IsLinux())
            {
                if (target.Contains('=')) throw new InvalidDataException("A desktop launcher executable path cannot contain '='.");
                var exec = string.Join(" ", new[] { target }.Concat(arguments).Select(DesktopArgument));
                var icon = definition.Icon is { Length: > 0 } ? "Icon=" + DesktopValue(PackageSafety.UnderRoot(plan.InstallDirectory, definition.Icon)) + "\n" : "";
                Write("launcher.desktop", "[Desktop Entry]\nType=Application\nName=" + DesktopValue(definition.Name) + "\nExec=" + exec + "\nPath=" + DesktopValue(working) + "\n" + icon + "Terminal=false\n", 493);
            }
            else
            {
                Write("Contents/Info.plist", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>CFBundleExecutable</key><string>launch</string><key>CFBundleName</key><string>" + SecurityElement.Escape(definition.Name) + "</string><key>CFBundleIdentifier</key><string>com.socketjack.installer." + manifest.Id + "." + index + "</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>", 420);
                Write("Contents/MacOS/launch", "#!/bin/sh\ncd " + ShellArgument(working) + " || exit 1\nexec " + string.Join(" ", new[] { target }.Concat(arguments).Select(ShellArgument)) + " \"$@\"\n", 493);
            }

            void Write(string part, string content, int mode)
            {
                var path = Destination(manifest.Id, index, definition.Location, plan.Scope, part);
                var snapshot = new ModuleSnapshot { Kind = "portable-shortcut", Key = path, Values = new()
                {
                    ["index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["location"] = definition.Location, ["part"] = part,
                    ["data"] = File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null,
                    ["mode"] = File.Exists(path) ? ((int)File.GetUnixFileMode(path)).ToString(System.Globalization.CultureInfo.InvariantCulture) : null
                } };
                var temporary = Path.Combine(transaction.Root, Guid.NewGuid().ToString("N"));
                File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.SetUnixFileMode(temporary, (UnixFileMode)mode);
                transaction.ReplaceFile(path, temporary); snapshots.Add(snapshot);
            }
        }
        return Task.FromResult(snapshots);
    }

    internal static Task RestoreAsync(InstalledProduct product, InstallTransaction transaction, CancellationToken token)
    {
        foreach (var snapshot in product.Modules.Where(x => x.Kind == "portable-shortcut").Reverse())
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(snapshot.Values.GetValueOrDefault("index"), out var index) || index is < 0 or > 4096)
                throw new InvalidDataException("Invalid portable shortcut receipt.");
            var path = Destination(product.Id, index, snapshot.Values["location"]!, product.Scope, snapshot.Values["part"]!);
            if (!path.Equals(snapshot.Key, InstallerPlatform.PathComparison)) throw new InvalidDataException("Shortcut receipt escapes its owned destination.");
            if (snapshot.Values["data"] is { } data)
            {
                var temporary = Path.Combine(transaction.Root, Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(temporary, Convert.FromBase64String(data));
                File.SetUnixFileMode(temporary, (UnixFileMode)int.Parse(snapshot.Values["mode"]!, System.Globalization.CultureInfo.InvariantCulture));
                transaction.ReplaceFile(path, temporary);
            }
            else transaction.DeleteFile(path);
        }
        return Task.CompletedTask;
    }

    private static string Destination(string id, int index, string location, string scope, string part)
    {
        PackageSafety.ValidateId(id);
        if (location is not ("startMenu" or "desktop") || scope is not ("user" or "machine")) throw new InvalidDataException("Invalid shortcut destination.");
        if (location == "desktop" && scope == "machine") throw new InvalidDataException("Portable desktop shortcuts require user scope.");
        var root = location == "desktop" ? Path.Combine(InstallerPlatform.UserHome, "Desktop") : OperatingSystem.IsMacOS()
            ? scope == "machine" ? "/Applications" : Path.Combine(InstallerPlatform.UserHome, "Applications")
            : scope == "machine" ? "/usr/local/share/applications" : Path.Combine(InstallerPlatform.UserData, "applications");
        if (OperatingSystem.IsLinux() && part == "launcher.desktop") return PackageSafety.UnderRoot(root, id + "-" + index + ".desktop");
        if (OperatingSystem.IsMacOS() && part is "Contents/Info.plist" or "Contents/MacOS/launch") return PackageSafety.UnderRoot(root, id + "-" + index + ".app/" + part);
        throw new InvalidDataException("Invalid portable shortcut file.");
    }

    private static string DesktopValue(string text) => text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    private static string DesktopArgument(string text) => DesktopValue("\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"");
    private static string ShellArgument(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
}
