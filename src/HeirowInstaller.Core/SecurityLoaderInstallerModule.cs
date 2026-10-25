using System.Text.Json;
using System.Security.Cryptography;

namespace HeirowInstaller.Core;

/// <summary>Declarative delivery only. Authentication, keys and assembly activation belong to the private application runtime.</summary>
public sealed class SecurityLoaderInstallerModule : IInstallerModule
{
    public string Id => "SocketJackSecurityLoader";
    public IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context)
    {
        var (publisher, componentVersion) = ValidateHeader(configuration);
        var components = configuration.GetProperty("components");
        var files = context.Plan.Manifest.Payload.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var digests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object || component.TryGetProperty("sourcePath", out _)
                || !Text(component, "id", out var identity) || !Identifier(identity) || !ids.Add(identity)
                || !Text(component, "path", out var path) || !path.StartsWith("SecurityLoader/", StringComparison.Ordinal)
                || !path.EndsWith(".sjsl", StringComparison.Ordinal) || !files.ContainsKey(path) || !paths.Add(path)
                || !Text(component, "componentDigest", out var digest) || !Hash(digest) || !digests.Add(digest)
                || component.TryGetProperty("version", out var version) && (version.ValueKind != JsonValueKind.String || version.GetString() != componentVersion)
                || component.TryGetProperty("publisherKeySha256", out var componentPublisher) && (componentPublisher.ValueKind != JsonValueKind.String || !PackageSafety.HashEquals(componentPublisher.GetString() ?? "", publisher)))
                throw new InvalidDataException("A protected component must have a distinct identity, encrypted signed payload and release binding.");
            _ = context.ResolveTarget(path);
        }
        foreach (var path in new[] { "SecurityLoader/bootstrap.json", "SecurityLoader/bootstrap.sig", "SecurityLoader/bootstrap.pub" })
            if (!files.ContainsKey(path)) throw new InvalidDataException("The sealed bootstrap trust metadata is incomplete.");
        return ["Install verified encrypted SecurityLoader components; authorization occurs when the application starts."];
    }
    // The caller MUST verify the package against external/installed trust before evaluating this declaration.
    internal static void ValidatePublisherTrust(JsonElement configuration, string trustedOuterPublisher)
    {
        var (publisher, _) = ValidateHeader(configuration);
        if (configuration.TryGetProperty("outerPublisherPin", out var delegation))
        {
            if (delegation.ValueKind != JsonValueKind.String || !Hash(delegation.GetString()) || !PackageSafety.HashEquals(delegation.GetString()!, trustedOuterPublisher))
                throw new CryptographicException("The protected key delegation does not match the trusted outer installer publisher.");
        }
        else if (!PackageSafety.HashEquals(publisher, trustedOuterPublisher))
            throw new CryptographicException("A different protected publisher requires explicit delegation from the trusted outer installer publisher.");
    }
    static (string Publisher, string Version) ValidateHeader(JsonElement configuration)
    {
        NoDuplicateFields(configuration);
        if (configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty("preparationOnly", out var preparation))
        {
            if (preparation.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("The preparationOnly flag must be a boolean.");
            if (preparation.ValueKind == JsonValueKind.True) throw new InvalidDataException("Preparation-only packages are sealing artifacts and are not installable applications.");
        }
        if (configuration.ValueKind != JsonValueKind.Object || !configuration.TryGetProperty("formatVersion", out var format) || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var number) || number != 1
            || !configuration.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 1 or > 128
            || !Text(configuration, "publisherKeySha256", out var publisher) || !Hash(publisher)
            || !Text(configuration, "componentVersion", out var version) || version.Length is < 1 or > 128 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+')))
            throw new InvalidDataException("Use a version-bound sealed SecurityLoader package published through SocketJack.com.");
        _ = PackageVersion.Parse(version);
        return (publisher, version);
    }
    static void NoDuplicateFields(JsonElement value, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("Protected metadata exceeds its nesting limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject())
            {
                if (!fields.Add(field.Name)) throw new InvalidDataException("Protected metadata contains duplicate fields.");
                NoDuplicateFields(field.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) NoDuplicateFields(item, depth + 1);
    }
    static bool Text(JsonElement value, string name, out string text)
    {
        text = "";
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString()!; return true;
    }
    public Task<ModuleSnapshot> InstallAsync(JsonElement configuration, InstallerModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _ = Plan(configuration, context);
        return Task.FromResult(new ModuleSnapshot { Kind = "extension:" + Id, Key = Id, Values = new() { ["configuration"] = configuration.GetRawText() } });
    }
    public Task UninstallAsync(ModuleSnapshot originalState, InstallerModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask;
    }
    static bool Hash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
    static bool Identifier(string? text) => text is { Length: > 0 and <= 128 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
