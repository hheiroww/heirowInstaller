using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>Declarative delivery only. Authentication, keys and assembly activation belong to the private application runtime.</summary>
public sealed class SecurityLoaderInstallerModule : IInstallerModule
{
    public string Id => "SocketJackSecurityLoader";
    public IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context)
    {
        if (configuration.ValueKind != JsonValueKind.Object || !configuration.TryGetProperty("formatVersion", out var format) || format.GetInt32() != 1
            || !configuration.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 1 or > 128
            || !configuration.TryGetProperty("publisherKeySha256", out var publisher) || !Hash(publisher.GetString())) throw new InvalidDataException("Use a sealed SecurityLoader package published through SocketJack.com.");
        var files = context.Plan.Manifest.Payload.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object || component.TryGetProperty("sourcePath", out _)
                || !component.TryGetProperty("id", out var identity) || !Identifier(identity.GetString()) || !ids.Add(identity.GetString()!)
                || !component.TryGetProperty("path", out var file) || file.GetString() is not { } path || !path.StartsWith("SecurityLoader/", StringComparison.Ordinal)
                || !path.EndsWith(".sjsl", StringComparison.Ordinal) || !files.ContainsKey(path)
                || !component.TryGetProperty("componentDigest", out var digest) || !Hash(digest.GetString())) throw new InvalidDataException("A protected component must be declared as an encrypted signed payload.");
            _ = context.ResolveTarget(path);
        }
        foreach (var path in new[] { "SecurityLoader/bootstrap.json", "SecurityLoader/bootstrap.sig", "SecurityLoader/bootstrap.pub" })
            if (!files.ContainsKey(path)) throw new InvalidDataException("The sealed bootstrap trust metadata is incomplete.");
        return ["Install verified encrypted SecurityLoader components; authorization occurs when the application starts."];
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
