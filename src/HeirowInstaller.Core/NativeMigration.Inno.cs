using System.Text.RegularExpressions;

namespace HeirowInstaller.Core;

/// <summary>Source preparation only: check proposed original Inno bindings without executing a generated log.</summary>
internal static class InnoMigrationPolicy
{
    internal const string SignedPayloadProvenance = "original-signed-generated-media-v1";

    internal static void ValidateBaseline(NativeMigrationBaselineDefinition baseline)
    {
        if (baseline.MsiProductCode is not null || baseline.BurnRegistration is not null || baseline.BurnMsiProductCodes is not null || baseline.OwnedFootprint is not null || baseline.Inno is not { } inno)
            throw new InvalidDataException("Inno migration requires its own explicit identity, without MSI or Burn authority.");
        ValidateIdentity(inno.AppId, inno.Registration, baseline.Scope);
    }

    internal static void ValidateOriginalDeclaration(PackageManifest manifest, bool requireHashes)
    {
        if (manifest.LegacyExecutable?.InnoUninstallMedia is not { } media) return;
        var executable = manifest.LegacyExecutable;
        if (media.Provenance != SignedPayloadProvenance)
            throw new InvalidDataException("Inno uninstall media requires an explicit original signed generated-media declaration.");
        ValidateIdentity(media.AppId, executable.MaintenanceRegistration, manifest.Scope);
        _ = CanonicalRoot(media.InstallDirectory);
        if (string.IsNullOrEmpty(media.SetupVersion) || media.SetupVersion.Length > 32 || !Regex.IsMatch(media.SetupVersion, "^[0-9]+(?:\\.[0-9]+){1,3}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Inno uninstall media requires its exact setup version.");
        var paths = MediaPaths(media);
        var directory = paths[0][..(paths[0].LastIndexOf('/') + 1)];
        if (manifest.Payload.Any(file => file.Path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
            !file.Path[directory.Length..].Contains('/') && Regex.IsMatch(file.Path[directory.Length..], "^unins[0-9]{3}\\.", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) &&
            !paths.Contains(file.Path, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("The original package contains an undeclared generated uninstall sidecar or another uninstall set.");
        var stem = Path.GetFileNameWithoutExtension(paths[0]);
        if (!Regex.IsMatch(stem, "^unins[0-9]{3}$", RegexOptions.CultureInvariant) ||
            Path.GetExtension(paths[0]) != ".exe" || Path.GetExtension(paths[1]) != ".dat" ||
            !paths[1].Equals(Path.ChangeExtension(paths[0], ".dat"), StringComparison.Ordinal) ||
            media.MessagesPath is not null && !paths[2].Equals(Path.ChangeExtension(paths[0], ".msg"), StringComparison.Ordinal) ||
            paths.Contains(PackageSafety.NormalizeRelativePath(executable.PayloadPath), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Inno generated media must identify one distinct exact EXE/DAT and optional MSG set.");
        var installedExecutable = "{InstallDir}/" + Path.GetFileName(paths[0]);
        if (executable.UninstallExecutable?.Replace('\\', '/') != installedExecutable ||
            !executable.UninstallArguments.Contains("/NORESTART", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The original Inno uninstall definition must bind its exact app-root executable and forbid automatic reboot.");
        foreach (var path in paths)
        {
            var matches = manifest.Payload.Where(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Feature is not null || matches[0].TargetRoot is not null ||
                requireHashes && (matches[0].Size is <= 0 or > 64L * 1024 * 1024 || !PackageSafety.HashPattern().IsMatch(matches[0].Sha256)))
                throw new InvalidDataException("Every Inno executable/data/message byte must be a required authenticated entry of the original package.");
        }
    }

    internal static IReadOnlyList<PayloadFile> RequireOriginalMedia(PackageManifest original, InstalledProduct installed, NativeMigrationBaselineDefinition authorization)
    {
        ValidateBaseline(authorization);
        ValidateOriginalDeclaration(original, requireHashes: true);
        var media = original.LegacyExecutable?.InnoUninstallMedia
            ?? throw new InvalidDataException("The original signed wrapper does not attest its generated Inno uninstall log; hashing live files or running repair is not provenance.");
        var inno = authorization.Inno!;
        if (!installed.IsExecutable || installed.IsMsi || original.Id != installed.Id || original.Version != installed.Version || original.Scope != installed.Scope ||
            authorization.Scope != installed.Scope || authorization.FromVersion != installed.Version || inno.AppId != media.AppId ||
            !SameRegistration(inno.Registration, original.LegacyExecutable!.MaintenanceRegistration!) ||
            CanonicalRoot(media.InstallDirectory) != CanonicalRoot(installed.InstallDirectory))
            throw new InvalidDataException("The original signed Inno media differs from this exact recorded product, version, scope, root or registration.");
        foreach (var snapshot in installed.BackendRegistrations)
        {
            BackendMaintenance.ValidateSnapshot(snapshot, installed.Scope);
            if (snapshot.Values.GetValueOrDefault("hive") != inno.Registration.Hive || snapshot.Values.GetValueOrDefault("view") != inno.Registration.View ||
                !string.Equals(snapshot.Values.GetValueOrDefault("path"), inno.Registration.UninstallKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recorded backend registration does not belong to the declared Inno identity.");
        }
        if (installed.BackendRegistrations.Count > 1)
            throw new InvalidDataException("Inno migration cannot adopt additional recorded backend identities.");
        return MediaPaths(media).Select(path => original.Payload.Single(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // This first checkpoint deliberately has no process/registry mutation implementation.
    // In particular, no boolean, current-file hash or repaired .dat can enable execution.
    internal static void RequireExecutionImplementation()
        => throw new NotSupportedException("Inno retirement execution is not implemented in this source checkpoint. No generated log was executed and no backend registration was changed.");

    private static string[] MediaPaths(InnoUninstallMediaDefinition media)
    {
        var declared = new List<string> { media.ExecutablePath, media.DataPath };
        if (media.MessagesPath is not null) declared.Add(media.MessagesPath);
        var paths = declared.Select(PackageSafety.NormalizeRelativePath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Inno uninstall media paths must be distinct.");
        return paths;
    }

    internal static void ValidateIdentity(string appId, BackendRegistrationDefinition? registration, string scope)
    {
        if (string.IsNullOrEmpty(appId) || appId.Length > 127 || appId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '{' or '}')) || registration is null)
            throw new InvalidDataException("Inno migration requires an exact literal AppId and registration.");
        BackendMaintenance.ValidateDefinition(registration, scope);
        if (registration.BundleUpgradeCode is not null || !string.Equals(registration.UninstallKey, BackendMaintenance.UninstallRoot + appId + "_is1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Inno identity requires its exact AppId_is1 key; Burn discovery is not Inno provenance.");
    }

    private static bool SameRegistration(BackendRegistrationDefinition left, BackendRegistrationDefinition right) =>
        left.Hive == right.Hive && left.View == right.View && string.Equals(left.UninstallKey, right.UninstallKey, StringComparison.OrdinalIgnoreCase) &&
        left.BundleUpgradeCode is null && right.BundleUpgradeCode is null;

    private static string CanonicalRoot(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/') || path[2..].Contains(':') ||
            path.Any(char.IsControl) || path[3..].Split('\\').Any(part => part.Length == 0 || part is "." or ".." || part != part.TrimEnd(' ', '.')))
            throw new InvalidDataException("Inno generated media requires one explicit canonical local Windows installation root.");
        _ = PackageSafety.NormalizeRelativePath(path[3..]);
        return path.ToUpperInvariant(); // Windows identity comparison, without filesystem access during policy validation.
    }
}
