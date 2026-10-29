using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static partial class DesktopSmoke
{
    private static PackageLegalDocuments LegalSmokeDocuments()
    {
        var documents = LegalDocumentRules.CreateDraft("heirowInstaller.desktop-fixture");
        documents.PrivacyPolicy!.Content = "This isolated desktop acceptance fixture uses synthetic data and temporary local files to test installer behavior. It is not a distributed product policy.";
        documents.TermsAndConditions!.Content = "Use this fixture only for isolated installer acceptance. These terms do not apply to another application or service.";
        return LegalDocumentRules.PrepareForCreation("heirowInstaller.desktop-fixture", documents);
    }
}
