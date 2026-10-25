using System.Runtime.CompilerServices;

namespace HeirowInstaller.Core;

/// <summary>Resolves signed, product-scoped component roots such as an application's VST3 directory.</summary>
public static class PayloadDestinations
{
    private static readonly ConditionalWeakTable<InstalledProduct, PackageManifest> installedManifests = new();

    public static void ValidateDefinitions(PackageManifest manifest)
    {
        if (manifest.InstallationRoots?.Count > 8) throw new InvalidDataException("Too many component installation roots.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in manifest.InstallationRoots ?? [])
        {
            PackageSafety.ValidateId(root.Id);
            if (!ids.Add(root.Id)) throw new InvalidDataException("Duplicate component installation root.");
            if (root.KnownFolder is not ("commonFiles64" or "commonFiles32" or "userCommonFiles"))
                throw new InvalidDataException("Unsupported component known folder.");
            root.RelativeDirectory = PackageSafety.NormalizeRelativePath(root.RelativeDirectory);
            if (root.RelativeDirectory.Split('/').Length < 2)
                throw new InvalidDataException("A component root must include a product directory beneath its known folder.");
        }
        foreach (var file in manifest.Payload)
        {
            if (file.TargetRoot is null) continue;
            if (!ids.Contains(file.TargetRoot) || !file.Path.StartsWith(file.TargetRoot + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An external payload must name its signed root and use that archive namespace.");
            _ = PackageSafety.NormalizeRelativePath(file.Path[(file.TargetRoot.Length + 1)..]);
        }
        if (manifest.Payload.Any(x => x.Path.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase) && x.TargetRoot is not null))
            throw new InvalidDataException("The application entry point must remain in its application directory.");
        if (manifest.UpdateCompanion is { } companion && manifest.Payload.Any(x => x.Path.Equals(companion.ExecutablePath, StringComparison.OrdinalIgnoreCase) && x.TargetRoot is not null))
            throw new InvalidDataException("A runtime payload cannot also target a component root.");
    }

    public static void ValidateLayout(PackageManifest manifest, string installDirectory, string scope, bool isolated, string? stateRoot = null, IEnumerable<PayloadFile>? selectedFiles = null)
    {
        ValidateDefinitions(manifest);
        var roots = new List<string>();
        foreach (var definition in manifest.InstallationRoots ?? [])
        {
            if (selectedFiles is not null && !selectedFiles.Any(x => string.Equals(x.TargetRoot, definition.Id, StringComparison.OrdinalIgnoreCase))) continue;
            var root = ResolveRoot(definition, installDirectory, scope, isolated);
            if (!isolated && Related(root, installDirectory)) throw new InvalidDataException("Application and component roots must not overlap.");
            if (stateRoot is not null && Related(root, stateRoot)) throw new InvalidDataException("Component roots must not overlap installer state.");
            if (roots.Any(other => Related(root, other))) throw new InvalidDataException("Component roots must not overlap one another.");
            roots.Add(root);
        }
    }

    public static void ValidateUpgrade(PackageManifest previous, PackageManifest target)
    {
        ValidateDefinitions(previous); ValidateDefinitions(target);
        foreach (var oldRoot in previous.InstallationRoots ?? [])
        {
            var next = target.InstallationRoots?.SingleOrDefault(x => x.Id.Equals(oldRoot.Id, StringComparison.OrdinalIgnoreCase));
            if (next is not null && (next.KnownFolder != oldRoot.KnownFolder || !next.RelativeDirectory.Equals(oldRoot.RelativeDirectory, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Relocating an existing component root requires an explicit migration.");
        }
        foreach (var oldFile in previous.Payload)
        {
            var next = target.Payload.SingleOrDefault(x => x.Path.Equals(oldFile.Path, StringComparison.OrdinalIgnoreCase));
            if (next is not null && !string.Equals(next.TargetRoot, oldFile.TargetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A payload path cannot silently change its installation root.");
        }
    }

    public static string Resolve(PackageManifest manifest, string installDirectory, string scope, bool isolated, PayloadFile file)
    {
        if (file.TargetRoot is null) return PackageSafety.UnderRoot(installDirectory, file.Path);
        var definition = manifest.InstallationRoots?.SingleOrDefault(x => x.Id.Equals(file.TargetRoot, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Missing signed component root.");
        if (!file.Path.StartsWith(file.TargetRoot + "/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid component payload namespace.");
        return PackageSafety.UnderRoot(ResolveRoot(definition, installDirectory, scope, isolated), file.Path[(file.TargetRoot.Length + 1)..]);
    }

    public static string ResolveInstalled(InstalledProduct product, InstalledFile file)
    {
        if (file.TargetRoot is null) return PackageSafety.UnderRoot(product.InstallDirectory, file.Path);
        var manifest = installedManifests.GetValue(product, static value =>
        {
            using var package = PackageReader.Open(value.PackagePath);
            package.VerifySignature(value.PublisherKeySha256, true);
            if (!package.Manifest.Id.Equals(value.Id, StringComparison.OrdinalIgnoreCase) || package.Manifest.Version != value.Version)
                throw new InvalidDataException("Component ownership does not match its signed cached package.");
            ValidateDefinitions(package.Manifest);
            return package.Manifest;
        });
        var declared = manifest.Payload.SingleOrDefault(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
        if (declared is null || !string.Equals(declared.TargetRoot, file.TargetRoot, StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(declared.Sha256, file.Sha256) || declared.Feature is not null && !product.Features.Contains(declared.Feature, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Component file ownership does not match the signed payload.");
        return Resolve(manifest, product.InstallDirectory, product.Scope, product.Isolated, declared);
    }

    public static IReadOnlyList<string> Roots(PackageManifest manifest, string installDirectory, string scope, bool isolated) =>
        (manifest.InstallationRoots ?? []).Select(x => ResolveRoot(x, installDirectory, scope, isolated)).ToArray();

    public static string BoundaryInstalled(InstalledProduct product, InstalledFile file)
    {
        if (file.TargetRoot is null) return product.InstallDirectory;
        _ = ResolveInstalled(product, file);
        return ResolveRoot(installedManifests.GetValue(product, _ => throw new InvalidOperationException()).InstallationRoots!
            .Single(x => x.Id.Equals(file.TargetRoot, StringComparison.OrdinalIgnoreCase)), product.InstallDirectory, product.Scope, product.Isolated);
    }

    private static string ResolveRoot(InstallationRootDefinition root, string installDirectory, string scope, bool isolated)
    {
        string parent;
        if (isolated) parent = PackageSafety.UnderRoot(installDirectory, ".component-roots/" + root.Id);
        else
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Component known folders require Windows.");
            if (root.KnownFolder.StartsWith("commonFiles", StringComparison.Ordinal) && scope != "machine")
                throw new InvalidDataException("Machine component folders require machine installation scope.");
            parent = root.KnownFolder switch
            {
                "commonFiles64" => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                "commonFiles32" => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                "userCommonFiles" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common"),
                _ => throw new InvalidDataException("Unsupported component known folder.")
            };
            if (string.IsNullOrWhiteSpace(parent)) throw new InvalidDataException("Component known folder is unavailable.");
        }
        return PackageSafety.UnderRoot(parent, root.RelativeDirectory);
    }

    private static bool Related(string left, string right)
    {
        left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
