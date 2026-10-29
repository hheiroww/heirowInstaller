using System.Text.Json;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    internal PackageLegalDocuments LegalDraft { get; private set; } = new() { PrivacyPolicy = new(), TermsAndConditions = new() };
    internal string LegalProductId { get; private set; } = "";
    private string legalManifestPath = "";
    private bool LegalDraftApplies(string path, string id) => !string.IsNullOrWhiteSpace(path) && legalManifestPath.Length != 0
        && legalManifestPath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
        && LegalProductId.Equals(id, StringComparison.OrdinalIgnoreCase);

    internal async Task LoadLegalDraftAsync(bool saved)
    {
        if (Busy) return;
        Begin(saved ? "Loading saved legal documents" : "Opening legal document editor", "Privacy Policy and Terms and Conditions");
        var path = ManifestPath;
        try
        {
            var manifest = JsonSerializer.Deserialize<PackageManifest>(await File.ReadAllTextAsync(path, Cancellation!.Token), InstallerJson.Options)
                ?? throw new InvalidDataException("Choose an application manifest first.");
            var documents = manifest.LegalDocuments;
            if (saved)
            {
                using var publisher = LegalPublisher();
                documents = (await Task.Run(() => publisher.GetLegalDocumentsAsync(manifest.Id, Cancellation.Token), Cancellation.Token)).LegalDocuments;
            }
            var draft = await Task.Run(() => LegalDocumentRules.PrepareDraft(manifest.Id, documents), Cancellation.Token);
            await game.OnUiAsync(() =>
            {
                legalManifestPath = Path.GetFullPath(path); LegalProductId = manifest.Id; LegalDraft = draft;
                End("Legal documents ready", "Type, paste or import each document. The next build includes both texts in its signed manifest.");
            });
        }
        catch (Exception error) { await ErrorAsync("Legal documents could not load", error); }
    }

    internal async Task ImportLegalDocumentAsync(string path, bool privacy)
    {
        if (Busy) return;
        Begin("Importing legal document text", Path.GetFileName(path));
        try
        {
            if (LegalProductId.Length == 0) throw new InvalidOperationException("Choose a manifest and open its legal document editor first.");
            var current = privacy ? LegalDraft.PrivacyPolicy! : LegalDraft.TermsAndConditions!;
            var document = await LegalDocumentRules.ImportFileAsync(path, current.Url, Cancellation!.Token);
            await game.OnUiAsync(() =>
            {
                if (privacy) LegalDraft.PrivacyPolicy = document; else LegalDraft.TermsAndConditions = document;
                End("Legal document imported", "Review the imported text. It is included when you build the installer again.");
            });
        }
        catch (Exception error) { await ErrorAsync("Legal document import failed", error); }
    }

    internal async Task SaveLegalDraftAsync()
    {
        if (Busy) return;
        Begin("Saving legal document draft", LegalProductId);
        try
        {
            if (LegalProductId.Length == 0) throw new InvalidOperationException("Open a manifest's legal document editor first.");
            var productId = LegalProductId; var documents = LegalDocumentRules.PrepareDraft(productId, LegalDraft);
            using var publisher = LegalPublisher();
            await Task.Run(() => publisher.SaveLegalDocumentsAsync(productId, documents, Cancellation!.Token), Cancellation!.Token);
            await game.OnUiAsync(() => End("Legal draft saved", "Rebuild and sign your installer to include these texts. Saving a draft does not change an existing signed package."));
        }
        catch (Exception error) { await ErrorAsync("Legal draft could not save", error); }
    }

    private PublisherClient LegalPublisher()
    {
        var token = PublisherSignedIn ? publisherSession!.AccessToken : Environment.GetEnvironmentVariable("SOCKETJACK_PUBLISHER_TOKEN");
        var cookie = PublisherSignedIn ? null : Environment.GetEnvironmentVariable("SOCKETJACK_PUBLISHER_COOKIE");
        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(cookie)) throw new InvalidOperationException("Sign in to load or save this application's legal documents.");
        return new(new Uri("https://socketjack.com"), bearerToken: token, sessionCookie: cookie);
    }

    private async Task<PackageLegalDocuments> LegalForBuildAsync(string path)
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>(await File.ReadAllTextAsync(path, Cancellation!.Token), InstallerJson.Options)
            ?? throw new InvalidDataException("Application manifest is empty.");
        var edited = LegalDraftApplies(path, manifest.Id);
        var documents = edited ? LegalDraft : manifest.LegalDocuments;
        if (!edited && PublisherSignedIn && (string.IsNullOrWhiteSpace(documents?.PrivacyPolicy?.Content) || string.IsNullOrWhiteSpace(documents?.TermsAndConditions?.Content)))
        {
            using var publisher = LegalPublisher();
            var saved = (await Task.Run(() => publisher.GetLegalDocumentsAsync(manifest.Id, Cancellation.Token), Cancellation.Token)).LegalDocuments;
            documents = new()
            {
                PrivacyPolicy = string.IsNullOrWhiteSpace(documents?.PrivacyPolicy?.Content) ? saved?.PrivacyPolicy : documents.PrivacyPolicy,
                TermsAndConditions = string.IsNullOrWhiteSpace(documents?.TermsAndConditions?.Content) ? saved?.TermsAndConditions : documents.TermsAndConditions
            };
        }
        return await Task.Run(() => LegalDocumentRules.PrepareForCreation(manifest.Id, documents), Cancellation.Token);
    }

    private void RequireMatchingLegalDraft(PackageManifest manifest)
    {
        LegalDocumentRules.RequireForCreation(manifest);
        if (!LegalDraftApplies(ManifestPath, manifest.Id)) return;
        var expected = LegalDocumentRules.PrepareForCreation(manifest.Id, LegalDraft);
        bool Same(PackageLegalDocument left, PackageLegalDocument right) => left.Content == right.Content && left.Url == right.Url
            && left.FileName == right.FileName && left.MediaType == right.MediaType && left.Size == right.Size && left.Sha256 == right.Sha256;
        if (!Same(expected.PrivacyPolicy!, manifest.LegalDocuments!.PrivacyPolicy!) || !Same(expected.TermsAndConditions!, manifest.LegalDocuments.TermsAndConditions!))
            throw new InvalidDataException("The edited legal documents differ from the signed installer. Build and sign the installer again, then publish it.");
    }
}
