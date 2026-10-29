using HeirowInstaller.Core;

internal static class LegalFixture
{
    internal static PackageLegalDocuments For(string productId)
    {
        var documents = LegalDocumentRules.CreateDraft(productId);
        documents.PrivacyPolicy!.Content = "This document applies only to an isolated installer test fixture. The test uses temporary local files and synthetic data. It is not a privacy policy for a distributed application.";
        documents.TermsAndConditions!.Content = "This package is an isolated acceptance fixture. Use it only for installer testing. It does not grant terms for another application or service.";
        return LegalDocumentRules.PrepareForCreation(productId, documents);
    }
}
