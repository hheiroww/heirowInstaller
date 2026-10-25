using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HeirowInstaller.Core;

/// <summary>Publisher-signed, exact legacy staging inventory. No wildcard, age, arbitrary root or delete action is accepted.</summary>
public sealed class LegacyStagingCleanupDefinition
{
    public int FormatVersion { get; set; } = 1;
    public string? Feature { get; set; }
    public List<LegacyStagingTree> Trees { get; set; } = [];
}

public sealed class LegacyStagingTree
{
    public string Leaf { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? CompletedReceiptId { get; set; }
    public string? CompletedReceiptSha256 { get; set; }
    public string? SourceManifestSha256 { get; set; }
    public List<LegacyStagingFile> Files { get; set; } = [];
}

public sealed class LegacyStagingFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal static class LegacyStagingDefinition
{
    private static readonly JsonSerializerOptions StrictJson = new(InstallerJson.Options)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static LegacyStagingCleanupDefinition Read(JsonElement configuration)
    {
        var bytes = Encoding.UTF8.GetBytes(configuration.GetRawText());
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Legacy cleanup inventory is too large.");
        IncrementalPackageIO.ValidateJson(bytes);
        LegacyStagingCleanupDefinition definition;
        try { definition = JsonSerializer.Deserialize<LegacyStagingCleanupDefinition>(bytes, StrictJson) ?? throw new InvalidDataException("Missing cleanup inventory."); }
        catch (JsonException error) { throw new InvalidDataException("Invalid legacy cleanup inventory.", error); }
        if (definition.FormatVersion != 1 || definition.Trees is null || definition.Trees.Count is < 1 or > 16)
            throw new InvalidDataException("Legacy cleanup requires between one and sixteen exact trees.");
        if (definition.Feature is not null) PackageSafety.ValidateId(definition.Feature);
        var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var count = 0;
        foreach (var tree in definition.Trees)
        {
            if (tree is null || tree.Leaf is null || !Regex.IsMatch(tree.Leaf, @"^App\.(backup|incoming)-[0-9]{8}-[0-9]{6}-[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant) ||
                !leaves.Add(tree.Leaf) || tree.Files is null || tree.Files.Count == 0 || (count += tree.Files.Count) > 4096)
                throw new InvalidDataException("Legacy cleanup accepts only unique, exact App staging leaves and bounded nonempty inventories.");
            if (tree.Kind == "completed-backup")
            {
                if (!tree.Leaf.StartsWith("App.backup-", StringComparison.Ordinal) || tree.CompletedReceiptId != "installed-" + tree.Leaf[11..] ||
                    !Hash(tree.CompletedReceiptSha256) || !Hash(tree.SourceManifestSha256))
                    throw new InvalidDataException("A legacy backup requires matching completed-receipt and source-manifest attestations.");
                tree.CompletedReceiptSha256 = tree.CompletedReceiptSha256!.ToLowerInvariant(); tree.SourceManifestSha256 = tree.SourceManifestSha256!.ToLowerInvariant();
            }
            else if (tree.Kind != "unproven-incoming" || !tree.Leaf.StartsWith("App.incoming-", StringComparison.Ordinal) ||
                tree.CompletedReceiptId is not null || tree.CompletedReceiptSha256 is not null || tree.SourceManifestSha256 is not null)
                throw new InvalidDataException("Unproven incoming staging can only be quarantined, without completed-ownership claims.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in tree.Files)
            {
                if (file is null || file.Size < 0 || !Hash(file.Sha256)) throw new InvalidDataException("Invalid legacy file inventory metadata.");
                file.Path = PackageSafety.NormalizeRelativePath(file.Path);
                _ = PackageSafety.NormalizeRelativePath(tree.Leaf + "/" + file.Path);
                if (!paths.Add(file.Path)) throw new InvalidDataException("Duplicate legacy inventory path.");
                file.Sha256 = file.Sha256.ToLowerInvariant();
            }
            tree.Files = tree.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
            var directories = Directories(tree);
            if (tree.Files.Any(file => directories.Contains(file.Path))) throw new InvalidDataException("A legacy file overlaps a declared directory.");
        }
        definition.Trees = definition.Trees.OrderBy(tree => tree.Leaf, StringComparer.Ordinal).ToList();
        return definition;
    }
    private static bool Hash(string? value) => value is not null && PackageSafety.HashPattern().IsMatch(value);
    internal static string Digest(LegacyStagingTree tree) => IncrementalPackageIO.Hash(JsonSerializer.SerializeToUtf8Bytes(tree, InstallerJson.Options));
    internal static HashSet<string> Directories(LegacyStagingTree tree)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var file in tree.Files)
        {
            var path = file.Path;
            while (path.LastIndexOf('/') is var index && index >= 0) { path = path[..index]; directories.Add(path); }
        }
        return directories;
    }
    internal static string PersistentRoot(string stateRoot, string productId) => PackageSafety.UnderRoot(stateRoot, ".legacy-staging/" + productId);
    internal static string LedgerRelative(LegacyStagingTree tree) => "imports/" + IncrementalPackageIO.Hash(Encoding.UTF8.GetBytes(tree.Leaf.ToLowerInvariant())) + ".json";
    internal static string QuarantineRoot(string persistentRoot, LegacyStagingTree tree) => PackageSafety.UnderRoot(persistentRoot, "quarantine/" + Digest(tree) + "/" + tree.Leaf);
    internal static byte[] Ledger(LegacyStagingTree tree, string productId, string publisherPin) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        formatVersion = 1, productId, publisherKeySha256 = publisherPin.ToLowerInvariant(), inventorySha256 = Digest(tree),
        disposition = tree.Kind == "completed-backup" ? "removed-completed-backup" : "quarantined-unproven-incoming", inventory = tree
    }, InstallerJson.Options);
}
