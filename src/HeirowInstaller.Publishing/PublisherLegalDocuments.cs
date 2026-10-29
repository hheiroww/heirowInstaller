using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowInstaller.Publishing;

public sealed class PublisherLegalDocuments
{
    public string ProductId { get; set; } = "";
    public PackageLegalDocuments? LegalDocuments { get; set; }
    public string PrivacyPolicyUrl { get; set; } = "";
    public string TermsAndConditionsUrl { get; set; } = "";
    public string? Status { get; set; }
}

public sealed partial class PublisherClient
{
    public Task<PublisherLegalDocuments> GetLegalDocumentsAsync(string productId, CancellationToken cancellationToken = default)
        => LegalRequestAsync(productId, null, cancellationToken);

    /// <summary>Saves authoring text, including incomplete drafts. It never rewrites an already signed package.</summary>
    public Task<PublisherLegalDocuments> SaveLegalDocumentsAsync(string productId, PackageLegalDocuments documents,
        CancellationToken cancellationToken = default)
        => LegalRequestAsync(productId, LegalDocumentRules.PrepareDraft(productId, documents), cancellationToken);

    private async Task<PublisherLegalDocuments> LegalRequestAsync(string productId, PackageLegalDocuments? documents, CancellationToken cancellationToken)
    {
        PackageSafety.ValidateId(productId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(documents is null ? HttpMethod.Get : HttpMethod.Put,
            new Uri(origin, "api/installer/publisher/apps/" + Uri.EscapeDataString(productId) + "/legal"));
        if (documents is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { productId, legalDocuments = documents }, Json);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-HeirowInstaller-SHA256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("The saved legal documents exceed their metadata limit.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > 1024 * 1024) throw new InvalidDataException("The saved legal documents exceed their metadata limit.");
                body.Write(buffer, 0, count);
            }
            var result = JsonSerializer.Deserialize<PublisherLegalDocuments>(body.ToArray(), Json) ?? throw new InvalidDataException("Publisher returned no legal documents.");
            if (!string.Equals(result.ProductId, productId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Saved legal documents belong to another product.");
            LegalDocumentRules.ValidateDraft(result.LegalDocuments);
            foreach (var url in new[] { result.PrivacyPolicyUrl, result.TermsAndConditionsUrl })
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.UserInfo.Length != 0)
                    throw new InvalidDataException("Publisher returned an invalid public legal document URL.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The publisher's legal document request timed out. Try again."); }
    }
}
