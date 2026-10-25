using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeirowInstaller.Publishing;

/// <summary>Declarative input to a private protected-package builder. This public contract contains no keys or decryption implementation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SecurityLoaderManifest
{
    public int FormatVersion { get; set; } = 1;
    public bool HostedSigning { get; set; }
    public string? ApplicationId { get; set; }
    public string PublisherOrigin { get; set; } = "https://socketjack.com";
    public List<SecurityLoaderComponent> Components { get; set; } = [];
    public List<SecurityLoaderOperation> Operations { get; set; } = [];
    public List<string> PublicPayload { get; set; } = [];
    public string? BootstrapPath { get; set; }
    public string? BootstrapSha256 { get; set; }

    public static SecurityLoaderManifest Validate(JsonElement descriptor, IReadOnlyDictionary<string, string> payloadHashes)
    {
        var result = descriptor.Deserialize<SecurityLoaderManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The security loader descriptor is empty.");
        if (result.FormatVersion != 1 || result.Components is not { Count: > 0 and <= 128 } || result.Operations is null || result.Operations.Count > 4096 || result.PublicPayload is null)
            throw new InvalidDataException("The security loader descriptor format or component limit is invalid.");
        if (result.ApplicationId != null && !Identifier(result.ApplicationId) || !Uri.TryCreate(result.PublisherOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new InvalidDataException("The application identity and publisher origin must use a fixed secure service.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in result.Components)
        {
            if (!Identifier(component.Id) || !ids.Add(component.Id) || component.ExecutionMode is not ("remote" or "local") ||
                string.IsNullOrWhiteSpace(component.EntryType) || component.EntryType.Length > 512 || component.EntryType.Any(char.IsControl) ||
                component.Runtime is not ("net10.0" or "net8.0") || component.Dependencies is null || component.Dependencies.Count > 256)
                throw new InvalidDataException("A protected component has an invalid identity or execution contract.");
            RequirePayload(component.SourcePath);
            if (!protectedPaths.Add(component.SourcePath)) throw new InvalidDataException("A protected source appears more than once.");
            foreach (var dependency in component.Dependencies)
            {
                if (!Identifier(dependency) || dependency == component.Id) throw new InvalidDataException("Protected dependencies must name distinct component or assembly identifiers.");
            }
        }
        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in result.Operations)
        {
            if (!Identifier(operation.Id) || !operations.Add(operation.Id) || !ids.Contains(operation.ComponentId))
                throw new InvalidDataException("A protected operation has an invalid or duplicate identity or component.");
            var remote = result.Components.Single(c => c.Id == operation.ComponentId).ExecutionMode == "remote";
            if (remote && (operation.ArgumentsSchema is null || operation.ResultSchema is null))
                throw new InvalidDataException("Remote operations must declare argument and result schemas.");
            foreach (var schema in new[] { operation.ArgumentsSchema, operation.ResultSchema })
                if (schema is { } value && (value.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > 8192))
                    throw new InvalidDataException("Operation schemas must be bounded JSON objects.");
        }
        var publicPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in result.PublicPayload)
        {
            RequirePayload(path);
            if (!publicPaths.Add(path) || protectedPaths.Contains(path)) throw new InvalidDataException("A source cannot be both protected and ordinary public payload.");
        }
        if (result.BootstrapPath is not null || result.BootstrapSha256 is not null)
        {
            if (result.BootstrapPath is null || result.BootstrapSha256 is null) throw new InvalidDataException("Bootstrap path and SHA256 must be supplied together.");
            RequirePayload(result.BootstrapPath);
            if (!string.Equals(payloadHashes[result.BootstrapPath], result.BootstrapSha256, StringComparison.OrdinalIgnoreCase) || protectedPaths.Contains(result.BootstrapPath))
                throw new InvalidDataException("The private bootstrap must match its declared signed payload digest.");
        }
        return result;

        void RequirePayload(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') ||
                path.Split('/').Any(x => x is "" or "." or ".." || x.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')) ||
                !payloadHashes.TryGetValue(path, out var hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("A security loader path must name an exact hashed package payload.");
        }
    }
    private static bool Identifier(string value) => value is { Length: > 0 and <= 100 } && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SecurityLoaderComponent
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ExecutionMode { get; set; } = "remote";
    public string EntryType { get; set; } = "";
    public string Runtime { get; set; } = "net10.0";
    public List<string> Dependencies { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SecurityLoaderOperation
{
    public string Id { get; set; } = "";
    public string ComponentId { get; set; } = "";
    public JsonElement? ArgumentsSchema { get; set; }
    public JsonElement? ResultSchema { get; set; }
}
