using System.Diagnostics;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed record RunningApplicationRow(int ProcessId, string DisplayName, string ExecutablePath, string AffectedPath, bool CanRequestClose);
internal sealed record RunningApplicationsDialogState(string ProductName, string Operation, IReadOnlyList<RunningApplicationRow> Applications,
    bool IsRetry, string Message, bool ManualOnly = false, bool Waiting = false);

internal sealed partial class AppController
{
    private TaskCompletionSource<RunningApplicationsChoice>? runningApplicationsChoice;
    private bool runningApplicationsCanceled;
    internal RunningApplicationsDialogState? RunningApplicationsDialog { get; private set; }

    private Task<RunningApplicationsChoice> PromptRunningApplicationsAsync(RunningApplicationsRequest request, CancellationToken token) =>
        ShowRunningApplicationsAsync(new(request.ProductName, request.Operation,
            request.Applications.Select(application => new RunningApplicationRow(application.ProcessId, application.DisplayName,
                application.ExecutablePath, application.AffectedPath, application.CanRequestClose)).ToList(), request.IsRetry, request.Message), token);

    private async Task<RunningApplicationsChoice> ShowRunningApplicationsAsync(RunningApplicationsDialogState dialog, CancellationToken token)
    {
        var choice = new TaskCompletionSource<RunningApplicationsChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        await game.OnUiAsync(() =>
        {
            runningApplicationsChoice = choice;
            RunningApplicationsDialog = dialog;
            Status = dialog.IsRetry ? "An application is still open" : "Close apps to continue";
            StatusDetail = "Your permission is needed before installation can continue.";
            Notify();
        });
        using var registration = token.Register(() => choice.TrySetCanceled(token));
        try { return await choice.Task; }
        finally
        {
            await game.OnUiAsync(() =>
            {
                if (ReferenceEquals(runningApplicationsChoice, choice)) runningApplicationsChoice = null;
            });
        }
    }

    internal void CloseRunningApplications()
    {
        if (RunningApplicationsDialog is not { Waiting: false } dialog || runningApplicationsChoice is null) return;
        RunningApplicationsDialog = dialog with { Waiting = true };
        Status = dialog.ManualOnly ? "Checking application status" : "Waiting for apps to close";
        StatusDetail = "Save your work in any application asking for confirmation. You can cancel this installation.";
        Notify();
        runningApplicationsChoice.TrySetResult(RunningApplicationsChoice.CloseAppsAndContinue);
    }

    internal void CancelRunningApplications()
    {
        runningApplicationsCanceled = true;
        runningApplicationsChoice?.TrySetResult(RunningApplicationsChoice.Cancel);
        Cancellation?.Cancel();
    }

    private void ClearRunningApplicationsDialog()
    {
        RunningApplicationsDialog = null;
        runningApplicationsChoice = null;
    }

    // Legacy --wait-pid is a dependency, not authority to close a process. Only
    // Core's verified affected-file discovery may offer a graceful close request.
    private async Task WaitForProductDependencyAsync(InstalledProduct product, string operation, CancellationToken token)
    {
        if (arguments.WaitForPid is null) return;
        RunningApplicationTarget target;
        using (var package = PackageReader.Open(product.PackagePath))
        {
            package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
            if (!package.Manifest.Id.Equals(product.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The maintenance package belongs to a different application.");
            target = RunningApplicationTarget.FromManifest(package.Manifest, product.InstallDirectory, operation, product.Features);
        }
        await WaitForRequestedProcessAsync(target, token);
    }

    private async Task WaitForRequestedProcessAsync(RunningApplicationTarget target, CancellationToken token)
    {
        if (arguments.WaitForPid is not { } pid) return;
        var retry = false;
        while (TryReadDependency(pid, arguments.WaitForStartTicks, out var displayName, out var executable))
        {
            token.ThrowIfCancellationRequested();
            var affected = await RunningApplications.InspectAsync(target, token);
            if (affected.Any(application => application.ProcessId == pid &&
                (arguments.WaitForStartTicks is null || application.StartTimeUtcTicks == arguments.WaitForStartTicks))) return;
            var choice = await ShowRunningApplicationsAsync(new(target.ProductName, target.Operation,
                [new(pid, displayName, executable, "Requested installer dependency", false)], retry,
                "This process was supplied as an installer dependency. Close it yourself, then check again. Its identity does not permit the installer to close it.", ManualOnly: true), token);
            if (choice == RunningApplicationsChoice.Cancel) throw new OperationCanceledException(token);
            await Task.Delay(200, token);
            retry = true;
        }
        await game.OnUiAsync(() => { ClearRunningApplicationsDialog(); Notify(); });
    }

    private static bool TryReadDependency(int pid, long? startTicks, out string name, out string executable)
    {
        name = "Installer dependency"; executable = "Executable identity unavailable";
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || startTicks is not null && process.StartTime.ToUniversalTime().Ticks != startTicks) return false;
            name = process.ProcessName;
            try { executable = process.MainModule?.FileName ?? executable; }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}
