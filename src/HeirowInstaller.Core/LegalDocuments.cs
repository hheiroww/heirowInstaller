using System.Security.Cryptography;
using System.Text;

namespace HeirowInstaller.Core;

/// <summary>Actual publisher-supplied documents covered by the package manifest signature.</summary>
public sealed class PackageLegalDocuments
{
    public PackageLegalDocument? PrivacyPolicy { get; set; }
    public PackageLegalDocument? TermsAndConditions { get; set; }
}

public sealed class PackageLegalDocument
{
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MediaType { get; set; } = "text/plain";
    public string Content { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public static class LegalDocumentRules
{
    public const int MaximumDocumentBytes = 65536;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static PackageLegalDocuments CreateDraft(string productId, Uri? publisher = null)
    {
        PackageSafety.ValidateId(productId);
        publisher ??= new("https://socketjack.com");
        if (!publisher.IsAbsoluteUri || publisher.Scheme != "https" || publisher.UserInfo.Length != 0)
            throw new ArgumentException("The legal document publisher must use HTTPS without credentials.", nameof(publisher));
        var origin = publisher.GetLeftPart(UriPartial.Authority);
        var id = Uri.EscapeDataString(productId);
        return new()
        {
            PrivacyPolicy = new() { Url = origin + "/heirowInstaller/PrivacyPolicy/" + id, FileName = "privacy-policy.txt" },
            TermsAndConditions = new() { Url = origin + "/heirowInstaller/TermsAndConditions/" + id, FileName = "terms-and-conditions.txt" }
        };
    }

    /// <summary>Copies typed, pasted or imported authoring text and computes its exact UTF-8 integrity metadata.</summary>
    public static PackageLegalDocuments PrepareForCreation(string productId, PackageLegalDocuments? documents)
    {
        var defaults = CreateDraft(productId);
        return new()
        {
            PrivacyPolicy = Prepare(documents?.PrivacyPolicy, defaults.PrivacyPolicy!, "Privacy Policy"),
            TermsAndConditions = Prepare(documents?.TermsAndConditions, defaults.TermsAndConditions!, "Terms and Conditions")
        };
    }

    public static PackageLegalDocuments PrepareDraft(string productId, PackageLegalDocuments? documents)
    {
        var defaults = CreateDraft(productId);
        return new()
        {
            PrivacyPolicy = Prepare(documents?.PrivacyPolicy ?? defaults.PrivacyPolicy, defaults.PrivacyPolicy!, "Privacy Policy", allowBlank: true),
            TermsAndConditions = Prepare(documents?.TermsAndConditions ?? defaults.TermsAndConditions, defaults.TermsAndConditions!, "Terms and Conditions", allowBlank: true)
        };
    }

    public static void ValidateDraft(PackageLegalDocuments? documents)
    {
        if (documents?.PrivacyPolicy is { } privacy) ValidateDocument(privacy, "Privacy Policy", allowBlank: true);
        if (documents?.TermsAndConditions is { } terms) ValidateDocument(terms, "Terms and Conditions", allowBlank: true);
    }

    public static void RequireForCreation(PackageManifest manifest)
    {
        if (manifest.LegalDocuments is null)
            throw new InvalidDataException("New installers and publications require both a Privacy Policy and Terms and Conditions. Type or paste both documents, or import .txt/.md files, then rebuild and sign the package.");
        Validate(manifest.LegalDocuments);
    }

    /// <summary>Legacy manifests may omit the pair. A present pair must be complete and match its signed content.</summary>
    public static void Validate(PackageLegalDocuments? documents)
    {
        if (documents is null) return;
        ValidateDocument(documents.PrivacyPolicy, "Privacy Policy");
        ValidateDocument(documents.TermsAndConditions, "Terms and Conditions");
    }

    public static async Task<PackageLegalDocument> ImportFileAsync(string path, string url, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        PackageSafety.EnsureNoReparsePoints(path);
        var fileName = Path.GetFileName(path);
        var mediaType = MediaType(fileName);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > MaximumDocumentBytes) throw new InvalidDataException("Legal document files must contain between 1 and 65,536 UTF-8 bytes.");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        string content;
        try { content = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Legal documents must be UTF-8 plain text or Markdown."); }
        return Prepare(new() { Url = url, FileName = fileName, MediaType = mediaType, Content = content }, new(), "Legal document");
    }

    private static PackageLegalDocument Prepare(PackageLegalDocument? document, PackageLegalDocument defaults, string name, bool allowBlank = false)
    {
        if (document is null) throw new InvalidDataException(name + " is required. Type or paste its text, or import a UTF-8 .txt/.md file.");
        var copy = new PackageLegalDocument
        {
            Url = string.IsNullOrWhiteSpace(document.Url) ? defaults.Url : document.Url,
            FileName = string.IsNullOrWhiteSpace(document.FileName) ? defaults.FileName : document.FileName,
            MediaType = string.IsNullOrWhiteSpace(document.MediaType) ? "text/plain" : document.MediaType,
            Content = document.Content
        };
        var bytes = ContentBytes(copy.Content, name, allowBlank);
        copy.Size = bytes.Length;
        copy.Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        ValidateDocument(copy, name, allowBlank);
        return copy;
    }

    private static void ValidateDocument(PackageLegalDocument? document, string name, bool allowBlank = false)
    {
        if (document is null) throw new InvalidDataException(name + " is required. Type or paste its text, or import a UTF-8 .txt/.md file.");
        var bytes = ContentBytes(document.Content, name, allowBlank);
        if (document.Url is not { Length: > 0 and <= 2048 } || document.Url.Any(char.IsControl) || document.Url != document.Url.Trim()
            || !Uri.TryCreate(document.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0)
            throw new InvalidDataException(name + " must have a public HTTPS URL without credentials.");
        if (document.FileName is not { Length: > 0 and <= 128 } || document.FileName.IndexOfAny(['/', '\\']) >= 0
            || PackageSafety.NormalizeRelativePath(document.FileName) != document.FileName || MediaType(document.FileName) != document.MediaType)
            throw new InvalidDataException(name + " requires a safe .txt or .md file name and its matching text media type.");
        if (document.Size != bytes.Length || document.Sha256 is not { Length: 64 }
            || document.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(document.Sha256), SHA256.HashData(bytes)))
            throw new InvalidDataException(name + " size or SHA256 does not match its exact UTF-8 content.");
    }

    private static byte[] ContentBytes(string? content, string name, bool allowBlank = false)
    {
        if (content is null || content.Length > MaximumDocumentBytes || (!allowBlank && !content.Any(c => !char.IsWhiteSpace(c) && c != '\uFEFF')))
            throw new InvalidDataException(name + " must contain nonblank text, at most 65,536 UTF-8 bytes.");
        if (content.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException(name + " contains unsupported control characters.");
        try
        {
            var bytes = Utf8.GetBytes(content);
            if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException(name + " exceeds 65,536 UTF-8 bytes.");
            return bytes;
        }
        catch (EncoderFallbackException) { throw new InvalidDataException(name + " must be valid UTF-8 text."); }
    }

    private static string MediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".md" or ".markdown" => "text/markdown",
        _ => throw new InvalidDataException("Import a plain text (.txt) or Markdown (.md/.markdown) legal document.")
    };
}
