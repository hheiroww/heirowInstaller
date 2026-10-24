using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HeirowInstaller.Core;

public static partial class PackageSafety
{
    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || System.IO.Path.IsPathRooted(path))
            throw new InvalidDataException("Package paths must be nonempty relative paths of at most 240 characters.");
        path = path.Replace('\\', '/');
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|') ||
                ReservedName().IsMatch(part.Split('.')[0]))
                throw new InvalidDataException($"Unsafe package path: {path}");
        }
        return path;
    }

    public static string UnderRoot(string root, string relative)
    {
        relative = NormalizeRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package path escapes its destination.");
        EnsureNoReparsePoints(result);
        return result;
    }

    public static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Installer paths may not traverse symbolic links or reparse points: {current}");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    public static void ValidateId(string id)
    {
        if (!ProductId().IsMatch(id)) throw new InvalidDataException("Product IDs must contain 1-100 letters, digits, dots, underscores, or hyphens and begin with a letter or digit.");
    }

    public static void ValidateManifest(PackageManifest manifest, bool requireHashes)
    {
        ValidateId(manifest.Id);
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"Unsupported package format: {manifest.FormatVersion}");
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Publisher))
            throw new InvalidDataException("Package name and publisher are required.");
        _ = PackageVersion.Parse(manifest.Version);
        if (manifest.Scope is not ("user" or "machine")) throw new InvalidDataException("Scope must be user or machine.");
        var featureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in manifest.Features)
        {
            ValidateId(feature.Id);
            if (!featureIds.Add(feature.Id)) throw new InvalidDataException($"Duplicate feature: {feature.Id}");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Payload.Count == 0) throw new InvalidDataException("Package has no payload files.");
        foreach (var file in manifest.Payload)
        {
            file.Path = NormalizeRelativePath(file.Path);
            if (!paths.Add(file.Path)) throw new InvalidDataException($"Duplicate payload destination: {file.Path}");
            if (file.Feature is not null && !featureIds.Contains(file.Feature)) throw new InvalidDataException($"Unknown payload feature: {file.Feature}");
            if (file.Size < 0) throw new InvalidDataException("Payload sizes cannot be negative.");
            if (requireHashes && !HashPattern().IsMatch(file.Sha256)) throw new InvalidDataException($"Invalid SHA256 for {file.Path}");
        }
        foreach (var path in paths)
        {
            var ancestor = path;
            while (ancestor.LastIndexOf('/') is var separator && separator >= 0)
            {
                ancestor = ancestor[..separator];
                if (paths.Contains(ancestor)) throw new InvalidDataException("A payload destination cannot also be another file's parent.");
            }
        }
        if (manifest.EntryPoint.Length > 0 && !paths.Contains(NormalizeRelativePath(manifest.EntryPoint)))
            throw new InvalidDataException("Entry point must be included in the payload.");
        if (manifest.LegacyMsi is { } msi)
        {
            if (!paths.Contains(NormalizeRelativePath(msi.PayloadPath)) || !msi.PayloadPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Legacy MSI must refer to a packaged .msi file.");
            if (msi.ProductCode is { Length: > 0 } && !Guid.TryParse(msi.ProductCode, out _))
                throw new InvalidDataException("MSI product code must be a GUID.");
            if (!Regex.IsMatch(msi.InstallDirectoryProperty, "^[A-Z][A-Z0-9_]*$"))
                throw new InvalidDataException("MSI install directory property must be a public uppercase property.");
            if (manifest.Registry.Count + manifest.Environment.Count + manifest.Services.Count + manifest.Shortcuts.Count + manifest.Modules.Count > 0)
                throw new InvalidDataException("MSI packages delegate OS integration to Windows Installer; native integration modules cannot be mixed into an MSI adapter package.");
        }
        if (manifest.LegacyExecutable is { } executable)
        {
            if (manifest.LegacyMsi is not null) throw new InvalidDataException("A package must select only one installation backend.");
            if (!paths.Contains(NormalizeRelativePath(executable.PayloadPath)) || !executable.PayloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Legacy executable must refer to a packaged .exe file.");
            if (manifest.Registry.Count + manifest.Environment.Count + manifest.Services.Count + manifest.Shortcuts.Count + manifest.Modules.Count > 0)
                throw new InvalidDataException("Legacy executable packages delegate OS integration to their original installer.");
        }
        foreach (var feature in manifest.Registry.Select(x => x.Feature).Concat(manifest.Shortcuts.Select(x => x.Feature))
                     .Concat(manifest.Environment.Select(x => x.Feature)).Concat(manifest.Services.Select(x => x.Feature))
                     .Concat(manifest.Prerequisites.Select(x => x.Feature)))
            if (feature is not null && !featureIds.Contains(feature)) throw new InvalidDataException($"Unknown module feature: {feature}");
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool HashEquals(string left, string right) =>
        left.Length == 64 && right.Length == 64 && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left.ToLowerInvariant()), System.Text.Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductId();
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex HashPattern();
}

public sealed class PackageVersion : IComparable<PackageVersion>
{
    private long[] Parts { get; }
    private string[] PreRelease { get; }
    private PackageVersion(long[] parts, string[] preRelease) { Parts = parts; PreRelease = preRelease; }
    public static PackageVersion Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new InvalidDataException("A package version is required.");
        var withoutBuild = value.Split('+')[0];
        var halves = withoutBuild.Split('-', 2);
        var parts = halves[0].Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(x => !long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new InvalidDataException($"Invalid package version: {value}");
        var pre = halves.Length == 2 ? halves[1].Split('.') : [];
        if (pre.Any(x => x.Length == 0 || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new InvalidDataException($"Invalid prerelease version: {value}");
        return new(parts.Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray(), pre);
    }
    public int CompareTo(PackageVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < Math.Max(Parts.Length, other.Parts.Length); i++)
        {
            var c = (i < Parts.Length ? Parts[i] : 0).CompareTo(i < other.Parts.Length ? other.Parts[i] : 0);
            if (c != 0) return c;
        }
        if (PreRelease.Length == 0 || other.PreRelease.Length == 0)
            return (PreRelease.Length == 0).CompareTo(other.PreRelease.Length == 0);
        for (var i = 0; i < Math.Min(PreRelease.Length, other.PreRelease.Length); i++)
        {
            var a = long.TryParse(PreRelease[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var b = long.TryParse(other.PreRelease[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            var c = a && b ? an.CompareTo(bn) : a != b ? (a ? -1 : 1) : StringComparer.Ordinal.Compare(PreRelease[i], other.PreRelease[i]);
            if (c != 0) return c;
        }
        return PreRelease.Length.CompareTo(other.PreRelease.Length);
    }
}
