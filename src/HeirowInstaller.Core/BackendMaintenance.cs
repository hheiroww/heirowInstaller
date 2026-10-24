using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HeirowInstaller.Core;

internal static class BackendMaintenance
{
    internal const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    public static void ValidateDefinition(BackendRegistrationDefinition definition, string? scope = null)
    {
        if (definition.Hive is not ("user" or "machine") || definition.View is not ("registry64" or "registry32") ||
            scope is not null && definition.Hive != scope)
            throw new InvalidDataException("Backend maintenance registry hive must match installation scope and use an explicit registry32 or registry64 view.");
        if ((definition.UninstallKey is null) == (definition.BundleUpgradeCode is null))
            throw new InvalidDataException("Author exactly one backend uninstallKey or Burn bundleUpgradeCode.");
        if (definition.UninstallKey is { } path) ValidateKey(path);
        if (definition.BundleUpgradeCode is { } upgrade && !Guid.TryParse(upgrade, out _))
            throw new InvalidDataException("A Burn bundleUpgradeCode must be a GUID.");
    }

    private static void ValidateKey(string path)
    {
        if (!path.StartsWith(UninstallRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backend maintenance requires an exact Windows uninstall registration key.");
        var leaf = path[UninstallRoot.Length..];
        if (leaf.Length is < 1 or > 255 || leaf is "." or ".." || leaf.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '{' or '}')))
            throw new InvalidDataException("Backend uninstallKey must identify one exact uninstall registration.");
    }

    public static void ValidateSnapshot(ModuleSnapshot snapshot, string scope)
    {
        if (snapshot.Kind != "registry" || snapshot.Key != "SystemComponent" || snapshot.Values.GetValueOrDefault("hive") != scope ||
            snapshot.Values.GetValueOrDefault("view") is not ("registry64" or "registry32"))
            throw new InvalidDataException("Invalid backend maintenance snapshot.");
        ValidateKey(snapshot.Values.GetValueOrDefault("path") ?? "");
    }

    [SupportedOSPlatform("windows")]
    public static List<ModuleSnapshot> Configure(InstalledProduct product, InstalledProduct? previous, InstallPlan plan, InstallTransaction transaction)
    {
        var prior = previous?.BackendRegistrations ?? [];
        if (!plan.UpdateCompanionEnabled)
        {
            Restore(prior, product.Scope, transaction);
            return [];
        }
        (string Hive, string View, string Path) identity;
        if (product.IsMsi)
        {
            if (!Guid.TryParse(product.MsiProductCode, out var code)) throw new InvalidDataException("MSI maintenance requires its exact ProductCode.");
            var path = UninstallRoot + code.ToString("B").ToUpperInvariant();
            string? foundView = null;
            foreach (var view in new[] { "registry64", "registry32" })
            {
                using var hive = WindowsIntegration.Hive(product.Scope, view); using var key = hive.OpenSubKey(path);
                if (key is not null) { foundView = view; break; }
            }
            if (foundView is null)
            {
                var otherScope = product.Scope == "user" ? "machine" : "user";
                foreach (var view in new[] { "registry64", "registry32" })
                {
                    using var hive = WindowsIntegration.Hive(otherScope, view); using var key = hive.OpenSubKey(path);
                    if (key is not null) throw new InvalidDataException("This MSI's uninstall registration requires " + otherScope + " maintenance scope. Select --scope " + otherScope + " and an appropriately protected state directory before selecting application updates.");
                }
                throw new InvalidDataException("The exact installed MSI registration is missing; companion maintenance cannot be registered.");
            }
            if (!WindowsIntegration.IsMsiInstalled(product.MsiProductCode)) throw new InvalidDataException("The exact MSI product is no longer installed.");
            identity = (product.Scope, foundView, path);
        }
        else
        {
            var definition = plan.Manifest.LegacyExecutable?.MaintenanceRegistration ?? throw new InvalidDataException("An executable backend needs explicit maintenanceRegistration metadata before selecting application updates.");
            ValidateDefinition(definition, product.Scope);
            identity = (definition.Hive, definition.View, ResolveExecutable(definition));
        }
        var matching = prior.FirstOrDefault(x => SameIdentity(x, identity));
        Restore(prior.Where(x => !SameIdentity(x, identity)), product.Scope, transaction);
        using (var hive = WindowsIntegration.Hive(identity.Hive, identity.View))
        using (var key = hive.OpenSubKey(identity.Path, writable: true))
        {
            if (key is null) throw new InvalidDataException("The authored executable uninstall registration was not created by its installer.");
            var current = WindowsIntegration.CaptureRegistry(identity.Hive, identity.Path, "SystemComponent", identity.View);
            transaction.AddRollback(() => WindowsIntegration.RestoreRegistry(current));
            key.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
            return [matching ?? current];
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ResolveExecutable(BackendRegistrationDefinition definition)
    {
        using var hive = WindowsIntegration.Hive(definition.Hive, definition.View);
        if (definition.UninstallKey is { } authored) return authored;
        using var root = hive.OpenSubKey(UninstallRoot.TrimEnd('\\'));
        var found = new List<string>();
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            using var key = root!.OpenSubKey(name);
            var value = key?.GetValue("BundleUpgradeCode");
            var values = value is string[] array ? array : value is string single ? new[] { single } : [];
            if (values.Any(x => Guid.TryParse(x, out var actual) && actual == Guid.Parse(definition.BundleUpgradeCode!)))
                found.Add(UninstallRoot + name);
        }
        if (found.Count != 1) throw new InvalidDataException("The authored Burn BundleUpgradeCode must identify exactly one installed bundle in its specified registry hive and view.");
        ValidateKey(found[0]);
        return found[0];
    }

    private static bool SameIdentity(ModuleSnapshot snapshot, (string Hive, string View, string Path) identity) =>
        snapshot.Values.GetValueOrDefault("hive") == identity.Hive && snapshot.Values.GetValueOrDefault("view") == identity.View &&
        string.Equals(snapshot.Values.GetValueOrDefault("path"), identity.Path, StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    public static void Restore(IEnumerable<ModuleSnapshot> snapshots, string scope, InstallTransaction transaction)
    {
        foreach (var snapshot in snapshots)
        {
            ValidateSnapshot(snapshot, scope);
            using var hive = WindowsIntegration.Hive(scope, snapshot.Values["view"]);
            using var key = hive.OpenSubKey(snapshot.Values["path"]!);
            // Never recreate a backend registration removed by its actual uninstaller.
            if (key is null) continue;
            var current = WindowsIntegration.CaptureRegistry(scope, snapshot.Values["path"]!, "SystemComponent", snapshot.Values["view"]);
            transaction.AddRollback(() => WindowsIntegration.RestoreRegistry(current));
            WindowsIntegration.RestoreRegistry(snapshot);
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool HasRegistration(InstalledProduct product)
    {
        foreach (var snapshot in product.BackendRegistrations)
        {
            ValidateSnapshot(snapshot, product.Scope);
            using var hive = WindowsIntegration.Hive(product.Scope, snapshot.Values["view"]);
            using var key = hive.OpenSubKey(snapshot.Values["path"]!);
            if (key is not null) return true;
        }
        return false;
    }
}
