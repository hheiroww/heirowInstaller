using System.Diagnostics;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

namespace HeirowUpdater;

// Each visible failure owns one attempt. Coalesced checks and repainting never
// launch additional tabs; a button lets the user retry a failed browser launch.
internal sealed class NativeDownloadRecovery(UpdateCompanionConfiguration profile)
{
    internal Uri? Page { get; } = SocketJackDownloadRecovery.GetDownloadPage(profile);
    internal string Notice { get; private set; } = "";
    internal bool Attempted { get; private set; }
    internal bool Opened { get; private set; }
    private int opening;

    internal async Task OpenAsync()
    {
        if (Page is null || Interlocked.Exchange(ref opening, 1) != 0) return;
        Attempted = true;
        try
        {
            Opened = await Task.Run(() =>
            {
                if (profile.Isolated)
                {
                    // Isolated native acceptance never opens the user's browser.
                    var path = Environment.GetEnvironmentVariable("HEIROWUPDATER_TEST_DOWNLOAD_OPEN");
                    if (path is null) return false;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    File.AppendAllText(path, Page.AbsoluteUri + Environment.NewLine);
                    return Environment.GetEnvironmentVariable("HEIROWUPDATER_TEST_DOWNLOAD_FAIL") != "1";
                }
                using var process = Process.Start(new ProcessStartInfo(Page.AbsoluteUri) { UseShellExecute = true });
                return true; // Shell activation can succeed without returning a process.
            });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Opened = false;
        }
        finally { Volatile.Write(ref opening, 0); }
        Notice = Opened
            ? "We opened this app's SocketJack download page. Download the latest installer to update manually."
            : "We couldn't open your browser. Open the SocketJack download page below to update manually.";
    }
}
