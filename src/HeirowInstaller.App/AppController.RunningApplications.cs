using System.Diagnostics;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed record RunningApplicationRow(int ProcessId, string DisplayName, string ExecutablePath, string AffectedPath, bool CanRequestClose)
{
    internal long StartTimeUtcTicks { get; init; }
    internal bool CanForceClose { get; init; }
}
internal sealed record RunningApplicationsDialogState(string ProductName, string Operation, IReadOnlyList<RunningApplicationRow> Applications,
    bool IsRetry, string Message, bool ManualOnly = false, bool Waiting = false)
{
    internal string ProductId { get; init; } = "";
    internal bool InspectionTimedOut { get; init; }
    internal bool ConfirmForceClose { get; init; }
}

internal sealed partial class AppController
{
    private TaskCompletionSource<RunningApplicationsChoice>? runningApplicationsChoice;
    private (RunningApplicationsChoice Choice, RunningApplicationsDialogState Dialog)? queuedRunningApplicationsChoice;
    private bool runningApplicationsCanceled;
    internal RunningApplicationsDialogState? RunningApplicationsDialog { get; private set; }

    private Task<RunningApplicationsChoice> PromptRunningApplicationsAsync(RunningApplicationsRequest request, CancellationToken token) =>
        ShowRunningApplicationsAsync(new(request.ProductName, request.Operation,
            request.Applications.Select(application => new RunningApplicationRow(application.ProcessId, application.DisplayName,
                application.ExecutablePath, application.AffectedPath, application.CanRequestClose)
                { StartTimeUtcTicks = application.StartTimeUtcTicks, CanForceClose = application.CanForceClose }).ToList(), request.IsRetry, request.Message)
                { ProductId = request.ProductId, InspectionTimedOut = request.InspectionTimedOut }, token);

    private async Task<RunningApplicationsChoice> ShowRunningApplicationsAsync(RunningApplicationsDialogState dialog, CancellationToken token)
    {
        var choice = new TaskCompletionSource<RunningApplicationsChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        await game.OnUiAsync(() =>
        {
            runningApplicationsChoice = choice;
            if (runningApplicationsCanceled || token.IsCancellationRequested) { choice.TrySetCanceled(token); return; }
            var sameApplications = RunningApplicationsDialog is { } previous && SameRunningApplications(previous, dialog);
            RunningApplicationsDialog = dialog with { ConfirmForceClose = sameApplications && RunningApplicationsDialog!.ConfirmForceClose };
            Status = dialog.InspectionTimedOut ? "Application check took too long" : dialog.IsRetry ? "An application is still open" : "Close apps to continue";
            StatusDetail = dialog.InspectionTimedOut ? "Check again, cancel, or explicitly close the listed apps." : "Your permission is needed before installation can continue.";
            if (queuedRunningApplicationsChoice is { } queued)
            {
                queuedRunningApplicationsChoice = null;
                // A click made while Core was waiting may only answer the same
                // process snapshot. In particular, force consent cannot widen.
                if (SameRunningApplications(queued.Dialog, dialog)) CompleteRunningApplicationsChoice(queued.Choice);
            }
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
        if (RunningApplicationsDialog is not { } dialog) return;
        CompleteRunningApplicationsChoice(dialog.ManualOnly || !dialog.Applications.Any(application => application.CanRequestClose)
            ? RunningApplicationsChoice.CheckAgain : RunningApplicationsChoice.CloseAppsAndContinue);
    }

    internal void CheckRunningApplications() => CompleteRunningApplicationsChoice(RunningApplicationsChoice.CheckAgain);

    internal void ConfirmForceCloseRunningApplications()
    {
        if (RunningApplicationsDialog is not { ManualOnly: false } dialog || !dialog.Applications.Any(application => application.CanForceClose)) return;
        RunningApplicationsDialog = dialog with { ConfirmForceClose = true };
        Notify();
    }

    internal void BackFromForceCloseRunningApplications()
    {
        if (RunningApplicationsDialog is not { } dialog) return;
        RunningApplicationsDialog = dialog with { ConfirmForceClose = false };
        Notify();
    }

    internal void ForceCloseRunningApplications()
    {
        if (RunningApplicationsDialog is not { ManualOnly: false, ConfirmForceClose: true } dialog || !dialog.Applications.Any(application => application.CanForceClose)) return;
        CompleteRunningApplicationsChoice(RunningApplicationsChoice.ForceCloseListedApps);
    }

    private void CompleteRunningApplicationsChoice(RunningApplicationsChoice choice)
    {
        if (runningApplicationsCanceled || RunningApplicationsDialog is not { } dialog) return;
        if (runningApplicationsChoice is null || !runningApplicationsChoice.TrySetResult(choice))
            queuedRunningApplicationsChoice = (choice, dialog);
        else queuedRunningApplicationsChoice = null;
        RunningApplicationsDialog = dialog with { Waiting = true, ConfirmForceClose = false };
        Status = choice == RunningApplicationsChoice.ForceCloseListedApps ? "Closing the approved apps" : choice == RunningApplicationsChoice.CheckAgain ? "Checking application status" : "Waiting for apps to close";
        StatusDetail = dialog.ManualOnly || !dialog.Applications.Any(application => application.CanForceClose)
            ? "This attempt is time limited. Check again or cancel if an app stays open."
            : "This attempt is time limited. Check again, force close eligible listed apps, or cancel if an app stays open.";
        Notify();
    }

    private static bool SameRunningApplications(RunningApplicationsDialogState first, RunningApplicationsDialogState second) =>
        first.ProductId == second.ProductId && first.ProductName == second.ProductName && first.Operation == second.Operation && first.ManualOnly == second.ManualOnly &&
        first.Applications.Count == second.Applications.Count && first.Applications.All(application => second.Applications.Any(other =>
            application.ProcessId == other.ProcessId && application.StartTimeUtcTicks == other.StartTimeUtcTicks &&
            application.ExecutablePath.Equals(other.ExecutablePath, StringComparison.OrdinalIgnoreCase) && application.CanForceClose == other.CanForceClose));

    internal void CancelRunningApplications()
    {
        runningApplicationsCanceled = true;
        queuedRunningApplicationsChoice = null;
        runningApplicationsChoice?.TrySetResult(RunningApplicationsChoice.Cancel);
        Cancellation?.Cancel();
    }

    private void ClearRunningApplicationsDialog()
    {
        RunningApplicationsDialog = null;
        runningApplicationsChoice = null;
        queuedRunningApplicationsChoice = null;
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
            target = RunningApplicationTarget.FromManifest(package.Manifest, product.InstallDirectory, operation, product.Features, product.Scope, product.Isolated);
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
                "This process was supplied as an installer dependency. Close it yourself, then check again. Its identity does not permit the installer to close it.", ManualOnly: true)
                { ProductId = target.ProductId }, token);
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
