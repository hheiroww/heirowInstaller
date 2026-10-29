using System.Diagnostics;
using HeirowInstaller.Core;
using Microsoft.Xna.Framework;

namespace HeirowInstaller.App;

internal sealed partial class InstallerScene
{
    private void BuildLegalDocuments()
    {
        Box(0, 0, Width, Height, new Color(0, 0, 0, 205), 0, Color.Transparent).isMouseBypassEnabled = false;
        int width = Math.Min(850, Width - 48), height = Math.Min(700, Height - 64);
        int x = (Width - width) / 2, y = (Height - height) / 2;
        Box(x, y, width, height, Theme.Surface, 14);
        Text(showLegalEditor ? "Application legal documents" : "Provided by " + Controller.Manifest!.Publisher,
            x + 24, y + 19, width - 48, 34, 19, bold: true);
        Text(Controller.Busy ? Controller.Status + "…" : showLegalEditor ? "Type, paste or import both texts. Rebuild to include your edits in the signed installer."
            : "These texts are part of this package. A privacy notice is not general consent to data processing.",
            x + 25, y + 64, width - 50, 39, 10, Theme.Muted, wrap: true);
        if (Controller.Busy)
        {
            legalBusyX = x + 24; legalBusyY = y + 103; legalBusyWidth = width - 48;
            legalBusyIndicator = Box(legalBusyX, legalBusyY, legalBusyWidth * .18f, 3, Theme.Lime, 0, Color.Transparent);
        }
        Action("legal.privacy", "Privacy Policy", x + 24, y + 111, 185, () => { legalPrivacy = true; legalTextPage = 0; Controller.Notify(); }, primary: legalPrivacy);
        Action("legal.terms", "Terms and Conditions", x + 219, y + 111, 217, () => { legalPrivacy = false; legalTextPage = 0; Controller.Notify(); }, primary: !legalPrivacy);
        var pair = showLegalEditor ? Controller.LegalDraft : Controller.Manifest?.LegalDocuments;
        var document = (legalPrivacy ? pair?.PrivacyPolicy : pair?.TermsAndConditions) ?? new PackageLegalDocument();
        Text("PUBLIC DOCUMENT URL", x + 25, y + 166, width - 50, 19, 8.5f, Theme.Muted, true);
        var url = Field("legal.url", document.Url, x + 24, y + 190, width - 196, text => document.Url = text);
        if (!showLegalEditor) ReadOnlyField(url);
        Action("legal.open-url", "Open URL ↗", x + width - 158, y + 190, 134, () =>
        {
            if (Uri.TryCreate(document.Url, UriKind.Absolute, out var target) && target.Scheme == "https" && target.UserInfo.Length == 0)
                Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }, enabled: !Controller.Busy && Uri.TryCreate(document.Url, UriKind.Absolute, out _));
        string content = document.Content;
        const int pageLength = 4096;
        if (!showLegalEditor)
        {
            var starts = new List<int> { 0 };
            for (int next = pageLength; next < content.Length;)
            {
                if (char.IsHighSurrogate(content[next - 1])) next--;
                starts.Add(next); next += pageLength;
            }
            int pages = starts.Count;
            legalTextPage = Math.Clamp(legalTextPage, 0, pages - 1);
            int start = starts[legalTextPage], count = (legalTextPage + 1 < pages ? starts[legalTextPage + 1] : content.Length) - start;
            content = content.Substring(start, count);
            Text($"Page {legalTextPage + 1}/{pages} · {document.Size:N0} signed UTF-8 bytes", x + 25, y + 242, width - 50, 23, 9, Theme.Muted);
            Action("legal.previous", "Previous", x + width - 247, y + height - 64, 105, () => { legalTextPage--; Controller.Notify(); }, enabled: legalTextPage > 0);
            Action("legal.next", "Next", x + width - 130, y + height - 64, 106, () => { legalTextPage++; Controller.Notify(); }, enabled: legalTextPage + 1 < pages);
        }
        else Text("DOCUMENT TEXT · maximum 65,536 UTF-8 bytes", x + 25, y + 242, width - 50, 23, 9, Theme.Muted);
        var field = Field("legal.content", content, x + 24, y + 274, width - 48, text => document.Content = text, true, Math.Max(100, height - 357));
        if (!showLegalEditor) ReadOnlyField(field);
        Action("legal.close", showLegalEditor ? "Use for next build" : "Close", x + 24, y + height - 64, showLegalEditor ? 166 : 108, () =>
        { showLegalEditor = showPackageLegal = false; Controller.Notify(); }, primary: true);
        if (showLegalEditor)
        {
            Action("legal.import", "Import file", x + 200, y + height - 64, 117, () =>
            {
                using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Import legal document as text", Filter = "UTF-8 text or Markdown|*.txt;*.md;*.markdown", CheckFileExists = true };
                if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) _ = Controller.ImportLegalDocumentAsync(picker.FileName, legalPrivacy);
            }, enabled: !Controller.Busy);
            Action("legal.load-saved", "Load saved", x + 327, y + height - 64, 125, () => _ = Controller.LoadLegalDraftAsync(true), enabled: !Controller.Busy);
            Action("legal.save-draft", "Save draft", x + width - 156, y + height - 64, 132, () => _ = Controller.SaveLegalDraftAsync(), enabled: !Controller.Busy);
        }
    }
}
