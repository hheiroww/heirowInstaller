using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HeirowInstaller.Core;

internal static class InnoOwnedPolicy
{
    internal const string Backend = "inno-owned-footprint";
    internal const string Authority = "same-publisher-complete-ownership-v1";
    internal const string Coverage = "complete-published-appid-lineage-v1";
    internal const string WrapperPolicy = "verified-app-local-wrapper-sdk-identity-v1";
    internal const int MaximumFiles = 4096, MaximumCatalogBytes = 2 * 1024 * 1024;

    internal static void Validate(PackageManifest target, NativeMigrationBaselineDefinition baseline)
    {
        var c = baseline.OwnedFootprint ?? throw new InvalidDataException("Owned-footprint migration needs its complete inline catalog.");
        if (baseline.Scope != "user" || target.Scope != "user" || baseline.MsiProductCode is not null || baseline.BurnRegistration is not null ||
            baseline.BurnMsiProductCodes is not null || baseline.Inno is not null || c.FormatVersion != 1 ||
            c.Authority != Authority || c.Coverage != Coverage || c.WrapperPolicy != WrapperPolicy ||
            c.ProductId != target.Id || c.Version != baseline.FromVersion || !Hash(c.PublisherKeySha256) ||
            !PackageSafety.HashEquals(c.OriginalManifestSha256, baseline.FromManifestSha256) || !Hash(c.ReviewSha256) ||
            c.SetupSize is <= 0 or > 2L * 1024 * 1024 * 1024 || !Hash(c.SetupSha256) ||
            c.UnsupportedBehaviors is null || c.UnsupportedBehaviors.Count != 0 ||
            JsonSerializer.SerializeToUtf8Bytes(c, InstallerJson.Options).Length > MaximumCatalogBytes)
            throw new InvalidDataException("Owned-footprint creation requires an enrolled complete same-publisher ownership attestation and user-only scope.");
        InnoMigrationPolicy.ValidateIdentity(c.AppId, c.Registration, "user");
        Canonical(c.SetupPayloadPath); Canonical(c.RootRelativeToLocalAppData);
        if (!c.RootRelativeToLocalAppData.StartsWith("Programs/", StringComparison.Ordinal) || c.RootRelativeToLocalAppData.Split('/').Length < 2)
            throw new InvalidDataException("The owned root must be one explicit current-user LocalAppData/Programs child.");
        if (c.Lineage is null || c.Lineage.Count is < 1 or > 128 ||
            c.Lineage.Any(x => x is null || !Hash(x.SetupSha256) || !Hash(x.RecipeSha256)) ||
            c.Lineage.Select(x => x.SetupSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Lineage.Count ||
            !c.Lineage.Any(x => x.Version == c.Version && PackageSafety.HashEquals(x.SetupSha256, c.SetupSha256)))
            throw new InvalidDataException("The publisher must explicitly review and attest the complete supported AppId lineage.");
        foreach (var item in c.Lineage) _ = PackageVersion.Parse(item.Version);
        if (c.Files is null || c.Files.Count is < 1 or > MaximumFiles ||
            c.Files.Any(x => x is null || x.Size is < 0 or > 2L * 1024 * 1024 * 1024 || !Hash(x.Sha256)) ||
            c.Files.Sum(x => x.Size) > 8L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The complete program inventory exceeds its bounds or lacks authenticated file bytes.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in c.Files)
        {
            if (!paths.Add(Canonical(file.Path)) || Regex.IsMatch(file.Path, @"(^|/)unins[0-9]{3}\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Program files are distinct and cannot masquerade as generated uninstall metadata.");
            if (file.LineageSetupSha256 is not null && (!Hash(file.LineageSetupSha256) ||
                !c.Lineage.Any(x => PackageSafety.HashEquals(x.SetupSha256, file.LineageSetupSha256))) ||
                !file.Required && (file.LineageSetupSha256 is null || PackageSafety.HashEquals(file.LineageSetupSha256, c.SetupSha256)))
                throw new InvalidDataException("Optional historical program files require an exact reviewed earlier setup binding.");
        }
        if (c.Directories is null || c.Directories.Count > MaximumFiles ||
            c.Directories.Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Directories.Count ||
            c.Directories.Any(x => paths.Contains(x)))
            throw new InvalidDataException("Structural directories must be explicit, distinct and separate from file destinations.");
        if (c.GeneratedMetadata is null || c.GeneratedMetadata.Count is < 2 or > 3 ||
            c.GeneratedMetadata.Any(x => x is null || !Regex.IsMatch(x.FileName, @"^unins[0-9]{3}\.(exe|dat|msg)$", RegexOptions.CultureInvariant)) ||
            c.GeneratedMetadata.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.GeneratedMetadata.Count)
            throw new InvalidDataException("Declare exactly one bounded app-root opaque uninstall metadata set.");
        var exe = c.GeneratedMetadata.SingleOrDefault(x => x.FileName.EndsWith(".exe", StringComparison.Ordinal));
        if (exe is null || !exe.Required || !c.GeneratedMetadata.Any(x => x.FileName == Path.ChangeExtension(exe.FileName, ".dat") && x.Required) ||
            c.GeneratedMetadata.Any(x => Path.GetFileNameWithoutExtension(x.FileName) != Path.GetFileNameWithoutExtension(exe.FileName)))
            throw new InvalidDataException("Opaque metadata needs one exact required EXE/DAT stem and optional MSG.");
        foreach (var item in c.GeneratedMetadata) paths.Add(item.FileName);
        if (c.RegistryValues is null || c.RegistryValues.Count is < 3 or > 64 || c.RegistryValues.Any(x => x is null || x.Name is null) ||
            c.RegistryValues.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.RegistryValues.Count)
            throw new InvalidDataException("Declare the complete distinct typed Inno registry profile.");
        foreach (var rule in c.RegistryValues)
        {
            if (rule is null || rule.Name.Length is < 1 or > 128 || rule.Name.Any(char.IsControl) || rule.Value is null || rule.Value.Length > 4096 ||
                rule.Kind is not ("string" or "dword") || rule.Match is not ("literal" or "date" or "nonnegative" or "wrapper-visibility") ||
                rule.Match == "date" && rule.Kind != "string" || rule.Match is "nonnegative" or "wrapper-visibility" && rule.Kind != "dword" ||
                rule.Kind == "dword" && rule.Match == "literal" && !int.TryParse(rule.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("Unsupported typed registration rule.");
            if (rule.Match == "wrapper-visibility" && rule.Value is not ("0" or "1") ||
                rule.Match is "date" or "nonnegative" && rule.Value != "")
                throw new InvalidDataException("A dynamic registration predicate cannot carry ignored or unsupported data.");
            var withoutRoot = rule.Value.Replace("{InstallDir}", "", StringComparison.Ordinal);
            if (withoutRoot.Contains('{') || withoutRoot.Contains('}') || rule.Value.Any(char.IsControl))
                throw new InvalidDataException("Registration predicates allow only the literal install-root substitution.");
        }
        RequireRule("Inno Setup: App Path", "{InstallDir}");
        RequireRule("DisplayVersion", c.Version);
        RequireRule("UninstallString", "\"{InstallDir}\\" + exe.FileName + "\"");
        if (c.Shortcuts is null || c.Shortcuts.Count > 8 || c.Shortcuts.Any(x => x is null)) throw new InvalidDataException("At most eight exact current/historical user launch links are supported.");
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in c.Shortcuts)
        {
            if (link is null || link.KnownFolder is not ("Programs" or "Desktop") ||
                !links.Add(link.KnownFolder + "/" + Canonical(link.RelativePath)) || !link.RelativePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                !c.Files.Any(x => x.Path == Canonical(link.Target)) || Path.GetExtension(link.Target) != ".exe")
                throw new InvalidDataException("Only independently checked exact user launch shortcuts are supported.");
            if (link.LineageSetupSha256 is not null && (!Hash(link.LineageSetupSha256) ||
                !c.Lineage.Any(x => PackageSafety.HashEquals(x.SetupSha256, link.LineageSetupSha256)) ||
                !PackageSafety.HashEquals(link.LineageSetupSha256, c.SetupSha256) && !link.Optional))
                throw new InvalidDataException("Historical launch links need a reviewed setup binding and optional presence.");
        }
        if (c.PreservedDirectories is null || c.PreservedFiles is null || c.PreservedDirectories.Count + c.PreservedFiles.Count > 64)
            throw new InvalidDataException("Preserved user-data namespaces must be explicit and bounded.");
        var preserved = c.PreservedDirectories.Select(Canonical).Concat(c.PreservedFiles.Select(Canonical)).ToArray();
        if (preserved.Distinct(StringComparer.OrdinalIgnoreCase).Count() != preserved.Length ||
            preserved.Any(x => paths.Any(p => Overlap(x, p))) ||
            c.PreservedDirectories.Any(x => c.PreservedDirectories.Any(y => x != y && Overlap(x, y))) ||
            paths.Any(x => paths.Any(y => x != y && Overlap(x, y))) ||
            target.Payload.Any(x => x.TargetRoot is not null || preserved.Any(p => Overlap(p, x.Path))))
            throw new InvalidDataException("Owned, target and preserved-data namespaces cannot overlap or escape the app root.");
        // These are the only native external metadata actions this mode can recover completely.
        if (target.Registry.Count + target.Shortcuts.Count + target.Environment.Count + target.Services.Count + target.Modules.Count + target.Prerequisites.Count != 0 ||
            target.InstallationRoots?.Count > 0 || target.Payload.Any(x => c.GeneratedMetadata.Any(m => Overlap(m.FileName, x.Path))) ||
            string.IsNullOrEmpty(target.EntryPoint) || !target.Payload.Any(x => x.Path == target.EntryPoint && x.Feature is null) ||
            target.UpdateCompanion is { RuntimeMode: not "shared" } ||
            target.UpdateCompanion is { } companion && preserved.Any(p => Overlap(p, companion.ConfigurationPath)))
            throw new InvalidDataException("Owned-footprint targets initially support app-local native files, optional shared updater and SDK ARP only; other integrations require a reviewed extension.");
        return;

        void RequireRule(string name, string value)
        {
            if (!c.RegistryValues.Any(x => x.Name == name && x.Kind == "string" && x.Match == "literal" && x.Value == value && !x.Optional))
                throw new InvalidDataException("The complete typed profile must bind the Inno root, version and exact uninstall filename.");
        }
    }

    internal static void Bind(PackageManifest original, InstalledProduct installed, NativeMigrationBaselineDefinition authorization)
    {
        var c = authorization.OwnedFootprint!;
        if (original.LegacyExecutable is not { } exe || !installed.IsExecutable || installed.IsMsi || installed.UseOriginalInstallDirectory ||
            installed.Scope != "user" || original.Scope != "user" || installed.Id != c.ProductId || original.Id != c.ProductId ||
            original.Version != c.Version || installed.Version != c.Version || !PackageSafety.HashEquals(installed.PublisherKeySha256 ?? "", c.PublisherKeySha256) ||
            !c.Files.Any(x => x.Path == original.EntryPoint && x.Required) ||
            exe.PayloadPath != c.SetupPayloadPath || exe.UninstallExecutable?.Replace('\\', '/') != "{InstallDir}/" + c.GeneratedMetadata.Single(x => x.FileName.EndsWith(".exe", StringComparison.Ordinal)).FileName ||
            exe.MaintenanceRegistration is not { } registration || registration.Hive != "user" || registration.View != c.Registration.View ||
            !string.Equals(registration.UninstallKey, c.Registration.UninstallKey, StringComparison.OrdinalIgnoreCase) || registration.BundleUpgradeCode is not null ||
            original.InstallDirectory.Replace('\\', '/') != "{LocalAppData}/" + c.RootRelativeToLocalAppData ||
            original.Payload.SingleOrDefault(x => x.Path == c.SetupPayloadPath) is not { Feature: null, TargetRoot: null } setup ||
            setup.Size != c.SetupSize || !PackageSafety.HashEquals(setup.Sha256, c.SetupSha256) ||
            installed.Modules.Count != 0 || installed.BackendRegistrations.Count > 1 ||
            original.Registry.Count + original.Shortcuts.Count + original.Environment.Count + original.Services.Count + original.Modules.Count + original.Prerequisites.Count != 0)
            throw new InvalidDataException("The ownership attestation differs from the exact original signed wrapper and recorded product.");
        foreach (var snapshot in installed.BackendRegistrations)
        {
            BackendMaintenance.ValidateSnapshot(snapshot, "user");
            if (snapshot.Values.GetValueOrDefault("view") != c.Registration.View ||
                !string.Equals(snapshot.Values.GetValueOrDefault("path"), c.Registration.UninstallKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recorded visibility snapshot differs from this Inno identity.");
        }
        var companion = original.UpdateCompanion;
        var enabled = companion is not null && installed.Features.Contains(companion.Feature, StringComparer.OrdinalIgnoreCase);
        if (companion is { RuntimeMode: "shared" } || original.InstallationRoots?.Count > 0 ||
            original.Features.Any(x => x.Id != companion?.Feature) || installed.Features.Any(x => x != companion?.Feature) ||
            original.Payload.Count != (companion is null ? 1 : 2) ||
            original.Payload.Any(x => x.TargetRoot is not null || x.Path != c.SetupPayloadPath &&
                (companion is null || x.Path != companion.ExecutablePath || x.Feature != companion.Feature)) ||
            installed.BackendRegistrations.Count != (enabled ? 1 : 0) ||
            installed.RegisteredProduct != enabled || enabled && installed.UninstallerPath is null)
            throw new InvalidDataException("The original wrapper's updater, visibility and SDK registration state is unsupported.");
        var allowed = enabled ? new[] { companion!.ExecutablePath, companion.ConfigurationPath } : [];
        if (installed.Files.Count != allowed.Length || installed.Files.Any(x => !allowed.Contains(x.Path, StringComparer.OrdinalIgnoreCase) ||
            x.TargetRoot is not null || x.OriginalBackup is not null || !Hash(x.Sha256)) ||
            allowed.Any(x => c.Files.Any(f => Overlap(f.Path, x)) || c.GeneratedMetadata.Any(f => Overlap(f.FileName, x)) ||
                c.PreservedDirectories.Concat(c.PreservedFiles).Any(p => Overlap(p, x))))
            throw new InvalidDataException("The wrapper owns only its exact verified app-local updater/profile files.");
    }

    internal static string Canonical(string path)
    {
        if (path != PackageSafety.NormalizeRelativePath(path) || path.Contains('\\') || path.Any(c => c is '{' or '}'))
            throw new InvalidDataException("Catalog paths must be canonical literal relative paths.");
        return path;
    }
    internal static bool Overlap(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    internal static bool Hash(string? value) => value is not null && PackageSafety.HashPattern().IsMatch(value);
    internal static string Digest(InnoOwnedFootprintCatalog c) => IncrementalPackageIO.Hash(JsonSerializer.SerializeToUtf8Bytes(c, InstallerJson.Options));
    internal static void RequirePackageSignature(PackageManifest manifest, string? publisherKey)
    {
        foreach (var baseline in manifest.NativeMigration?.Baselines.Where(x => x.Backend == Backend) ?? [])
            if (publisherKey is null || !PackageSafety.HashEquals(publisherKey, baseline.OwnedFootprint!.PublisherKeySha256))
                throw new CryptographicException("An owned-footprint target requires the exact publisher signature declared by its ownership attestation.");
    }
}

/// <summary>Creates an incomplete inventory draft from a frozen publish folder, never an enrolled ownership assertion.</summary>
public static class InnoOwnedFootprintDraft
{
    public static Task<InnoOwnedFootprintCatalog> CreateAsync(PackageReader original, string frozenPublishDirectory, string rootRelativeToLocalAppData,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        progress?.Report(new("catalog-draft", "Reading the frozen publish inventory; ownership attestation remains incomplete.", 0, 0));
        return Task.Run(() => CreateCore(original, frozenPublishDirectory, rootRelativeToLocalAppData, progress, cancellationToken), cancellationToken);
    }
    internal static InnoOwnedFootprintCatalog Create(PackageReader original, string frozenPublishDirectory, string rootRelativeToLocalAppData) =>
        CreateCore(original, frozenPublishDirectory, rootRelativeToLocalAppData, null, default);
    private static InnoOwnedFootprintCatalog CreateCore(PackageReader original, string frozenPublishDirectory, string rootRelativeToLocalAppData,
        IProgress<InstallProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        original.VerifySignature(requireSignature: true);
        var manifest = original.Manifest;
        var exe = manifest.LegacyExecutable ?? throw new InvalidDataException("Choose the immutable original executable wrapper.");
        var setup = manifest.Payload.Single(x => x.Path == exe.PayloadPath && x.Feature is null);
        InnoOwnedPolicy.Canonical(rootRelativeToLocalAppData);
        var root = Path.GetFullPath(frozenPublishDirectory); PackageSafety.EnsureNoReparsePoints(root);
        var result = new InnoOwnedFootprintCatalog { ProductId = manifest.Id, Version = manifest.Version,
            PublisherKeySha256 = original.PublisherKeySha256!, OriginalManifestSha256 = NativeMigrationValidation.ManifestDigest(original),
            SetupPayloadPath = setup.Path, SetupSize = setup.Size, SetupSha256 = setup.Sha256, RootRelativeToLocalAppData = rootRelativeToLocalAppData,
            Registration = NativeMigrationValidation.Clone(exe.MaintenanceRegistration ?? new()) };
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                PackageSafety.EnsureNoReparsePoints(path);
                if (Directory.Exists(path))
                {
                    if (result.Directories.Count >= InnoOwnedPolicy.MaximumFiles) throw new InvalidDataException("Draft directory inventory exceeds its bound.");
                    result.Directories.Add(InnoOwnedPolicy.Canonical(Path.GetRelativePath(root, path).Replace('\\', '/')));
                    pending.Push(path); continue;
                }
                if (result.Files.Count >= InnoOwnedPolicy.MaximumFiles) throw new InvalidDataException("Draft inventory exceeds its file bound.");
                var size = new FileInfo(path).Length;
                if (size > 2L * 1024 * 1024 * 1024 || result.Files.Sum(x => x.Size) + size > 8L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Draft inventory exceeds its byte bound.");
                result.Files.Add(new() { Path = InnoOwnedPolicy.Canonical(Path.GetRelativePath(root, path).Replace('\\', '/')),
                    Size = size, Sha256 = PackageSafety.HashFile(path) });
                progress?.Report(new("catalog-draft", "Hashing the frozen inventory without enrolling ownership.", result.Files.Count, 0));
            }
        result.Files = result.Files.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        result.Directories = result.Directories.Order(StringComparer.Ordinal).ToList();
        // Authority, coverage, review, AppId, lineage, integrations and metadata intentionally remain incomplete.
        progress?.Report(new("catalog-draft", "Inventory draft prepared; publisher review and complete attestation are still required.", result.Files.Count, result.Files.Count));
        return result;
    }
}
