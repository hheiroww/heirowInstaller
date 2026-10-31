using System.Collections.Concurrent;
using System.Reflection;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static class Program
{
    private static int assertions;

    private static async Task Main()
    {
        using var game = new InstallerGame();
        using var cancellation = new CancellationTokenSource();
        var controller = new AppController(game, cancellation);
        var application = new RunningApplication(101, 123456, @"C:\Fixture\App.exe", "Fixture app", @"C:\Fixture\App.exe", "application", true) { CanForceClose = true };
        var manual = new RunningApplication(102, 654321, @"C:\Fixture\Host.exe", "Manual host", @"C:\Fixture\Plugin.dll", "plugin-host", true);
        RunningApplicationsRequest Request(params RunningApplication[] apps) => new("fixture.app", "Fixture app", "install", apps, false, "Fixture request");

        var first = controller.PromptAsync(Request(application, manual), cancellation.Token);
        await game.OnUiAsync(() =>
        {
            Check(controller.RunningApplicationsDialog!.Applications[0].StartTimeUtcTicks == application.StartTimeUtcTicks, "controller retains the shown process start identity");
            Check(controller.RunningApplicationsDialog.Applications[0].CanForceClose && !controller.RunningApplicationsDialog.Applications[1].CanForceClose, "only eligible app rows can be force closed");
            controller.ConfirmForceCloseRunningApplications();
            Check(controller.RunningApplicationsDialog.ConfirmForceClose && !first.IsCompleted, "first force click only opens confirmation");
            controller.BackFromForceCloseRunningApplications();
            Check(!controller.RunningApplicationsDialog.ConfirmForceClose && !first.IsCompleted, "back leaves the operation paused");
            controller.CloseRunningApplications();
            controller.CheckRunningApplications();
            controller.CheckRunningApplications();
            Check(controller.RunningApplicationsDialog.Waiting, "queued check leaves visible pending status");
        });
        Check(await first.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.CloseAppsAndContinue, "first prompt retains the original graceful-close choice");
        var checkedAgain = controller.PromptAsync(Request(manual, application), cancellation.Token);
        Check(await checkedAgain.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.CheckAgain, "check again clicked during pending work answers the next matching snapshot");
        var afterCoalescing = controller.PromptAsync(Request(application, manual), cancellation.Token);
        await game.OnUiAsync(() =>
        {
            Check(!afterCoalescing.IsCompleted, "repeated check-again clicks coalesce into one next-prompt action");
            controller.CheckRunningApplications();
        });
        _ = await afterCoalescing.WaitAsync(TimeSpan.FromSeconds(2));

        await game.OnUiAsync(() => { controller.ConfirmForceCloseRunningApplications(); controller.ForceCloseRunningApplications(); });
        var forced = controller.PromptAsync(Request(application, manual), cancellation.Token);
        Check(await forced.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.ForceCloseListedApps, "explicit force confirmation during pending work is retained for the same complete snapshot");

        var changedRequests = new[]
        {
            Request(application with { StartTimeUtcTicks = application.StartTimeUtcTicks + 1 }, manual),
            Request(application with { ProcessId = application.ProcessId + 10 }, manual),
            Request(application with { ExecutablePath = @"C:\Other\App.exe" }, manual),
            Request(application with { CanForceClose = false }, manual),
            Request(application),
            Request(application, manual, application with { ProcessId = 103 }),
            Request(application, manual) with { ProductId = "other.app" }
        };
        foreach (var changed in changedRequests)
        {
            await game.OnUiAsync(controller.Reset);
            var original = controller.PromptAsync(Request(application, manual), cancellation.Token);
            await game.OnUiAsync(() =>
            {
                controller.CloseRunningApplications();
                controller.ConfirmForceCloseRunningApplications();
                controller.ForceCloseRunningApplications();
            });
            _ = await original.WaitAsync(TimeSpan.FromSeconds(2));
            var replacement = controller.PromptAsync(changed, cancellation.Token);
            await game.OnUiAsync(() =>
            {
                Check(!replacement.IsCompleted, "queued force never applies to a changed identity set, eligibility or product");
                Check(!controller.RunningApplicationsDialog!.ConfirmForceClose, "a changed snapshot clears force confirmation");
                controller.CheckRunningApplications();
            });
            Check(await replacement.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.CheckAgain, "changed snapshot waits for a fresh action");
        }

        await game.OnUiAsync(controller.Reset);
        var confirming = controller.PromptAsync(Request(application, manual), cancellation.Token);
        await game.OnUiAsync(() => { controller.CloseRunningApplications(); controller.ConfirmForceCloseRunningApplications(); });
        _ = await confirming.WaitAsync(TimeSpan.FromSeconds(2));
        var changedWhileConfirming = controller.PromptAsync(Request(application with { StartTimeUtcTicks = 987654 }, manual), cancellation.Token);
        await game.OnUiAsync(() =>
        {
            Check(!controller.RunningApplicationsDialog!.ConfirmForceClose, "a prompt replacement also clears an unconfirmed force dialog");
            controller.ForceCloseRunningApplications();
            Check(!changedWhileConfirming.IsCompleted, "second force action is ignored after confirmation invalidation");
            controller.CheckRunningApplications();
        });
        _ = await changedWhileConfirming.WaitAsync(TimeSpan.FromSeconds(2));

        await game.OnUiAsync(controller.Reset);
        var timeout = controller.PromptAsync(Request() with { InspectionTimedOut = true }, cancellation.Token);
        await game.OnUiAsync(() =>
        {
            Check(controller.RunningApplicationsDialog is { InspectionTimedOut: true, Applications.Count: 0 }, "an inspection timeout can present an empty retryable prompt");
            controller.ConfirmForceCloseRunningApplications();
            Check(!controller.RunningApplicationsDialog!.ConfirmForceClose && !timeout.IsCompleted, "empty timeout prompt cannot offer force consent");
            controller.CheckRunningApplications();
        });
        Check(await timeout.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.CheckAgain, "empty inspection timeout supports check again");

        await game.OnUiAsync(controller.Reset);
        var dependency = controller.PromptManualAsync(application, cancellation.Token);
        await game.OnUiAsync(() =>
        {
            controller.ConfirmForceCloseRunningApplications(); controller.ForceCloseRunningApplications();
            Check(!controller.RunningApplicationsDialog!.ConfirmForceClose && !dependency.IsCompleted, "manual dependencies cannot acquire force authority");
            controller.CloseRunningApplications();
        });
        Check(await dependency.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.CheckAgain, "manual dependency close action only checks again");

        await game.OnUiAsync(controller.Reset);
        using (var oldCancellation = new CancellationTokenSource())
        {
            var oldPrompt = controller.PromptAsync(Request(application), oldCancellation.Token);
            Task<RunningApplicationsChoice>? nextPrompt = null;
            await game.OnUiAsync(() =>
            {
                oldCancellation.Cancel();
                nextPrompt = controller.PromptAsync(Request(application, manual), cancellation.Token);
            });
            try { _ = await oldPrompt.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Canceled old prompt unexpectedly returned."); }
            catch (OperationCanceledException) { assertions++; }
            await game.OnUiAsync(controller.CheckRunningApplications);
            Check(await nextPrompt!.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.CheckAgain, "old canceled prompt cleanup cannot clear the next prompt's choice");
        }

        using (var confirmCancellation = new CancellationTokenSource())
        {
            var confirmingController = new AppController(game, confirmCancellation);
            var confirmPrompt = confirmingController.PromptAsync(Request(application), confirmCancellation.Token);
            await game.OnUiAsync(() => { confirmingController.ConfirmForceCloseRunningApplications(); confirmingController.CancelRunningApplications(); });
            Check(await confirmPrompt.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.Cancel, "force confirmation can cancel the whole operation without forcing any choice");
            Check(confirmCancellation.IsCancellationRequested, "force-confirmation cancel interrupts the operation token");
        }

        await game.OnUiAsync(controller.Reset);
        var cancelPrompt = controller.PromptAsync(Request(application), cancellation.Token);
        await game.OnUiAsync(() => { controller.CloseRunningApplications(); controller.CancelRunningApplications(); });
        _ = await cancelPrompt.WaitAsync(TimeSpan.FromSeconds(2));
        Check(cancellation.IsCancellationRequested, "cancel interrupts pending Core work even after its prompt choice was consumed");
        var canceledPrompt = controller.PromptAsync(Request(application), cancellation.Token);
        try { _ = await canceledPrompt.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Canceled prompt unexpectedly returned."); }
        catch (OperationCanceledException) { assertions++; }
        Check(controller.Notifications > 0 && game.UiThreadId != Environment.CurrentManagedThreadId, "production adapter state notifications ran on the dedicated UI dispatcher");
        await VerifyCanceledTransportAsync();
        Console.WriteLine($"Passed {assertions} headless close-apps controller checks. No windows, input, or process actions were used.");
    }

    private static async Task VerifyCanceledTransportAsync()
    {
        // Exercise the actual parent worker transport adapter, without starting
        // any worker, graphics, pipe peer or application process.
        var worker = Assembly.Load("heirowInstaller").GetType("HeirowInstaller.App.ElevatedInstallWorker", throwOnError: true)!;
        var channelType = worker.GetNestedType("WorkerChannel", BindingFlags.NonPublic)!;
        var messageType = worker.GetNestedType("WorkerMessage", BindingFlags.NonPublic)!;
        var message = Activator.CreateInstance(messageType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, ["choice"], null)!;
        foreach (var canceled in new[] { false, true })
        foreach (var sending in new[] { false, true })
        {
            using var stop = new CancellationTokenSource();
            if (canceled) stop.Cancel();
            using var stream = new ClosedPipeStream();
            using var channel = (IDisposable)Activator.CreateInstance(channelType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [stream], null)!;
            var method = worker.GetMethod(sending ? "SendOperationMessageAsync" : "ReadOperationMessageAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var operation = (Task)method.Invoke(null, sending ? [channel, message, stop.Token] : [channel, stop.Token])!;
            try { await operation; throw new InvalidOperationException("The fake closed pipe unexpectedly succeeded."); }
            catch (OperationCanceledException error) when (canceled)
            {
                Check(error.CancellationToken == stop.Token && error.InnerException is IOException, "closed-pipe transport preserves the requested cancellation identity");
            }
            catch (IOException) when (!canceled) { assertions++; }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
}

// Link the production partial adapter to a deterministic dispatcher. This
// exercises actual prompt/choice state without constructing a renderer or app.
internal sealed partial class AppController
{
    private readonly InstallerGame game;
    private readonly AppArguments arguments = new();
    internal CancellationTokenSource Cancellation { get; }
    internal string Status { get; private set; } = "";
    internal string StatusDetail { get; private set; } = "";
    internal int Notifications { get; private set; }
    internal AppController(InstallerGame game, CancellationTokenSource cancellation) { this.game = game; Cancellation = cancellation; }
    internal Task<RunningApplicationsChoice> PromptAsync(RunningApplicationsRequest request, CancellationToken token) => PromptRunningApplicationsAsync(request, token);
    internal Task<RunningApplicationsChoice> PromptManualAsync(RunningApplication app, CancellationToken token) => ShowRunningApplicationsAsync(new("Fixture app", "install",
        [new(app.ProcessId, app.DisplayName, app.ExecutablePath, app.AffectedPath, app.CanRequestClose) { StartTimeUtcTicks = app.StartTimeUtcTicks, CanForceClose = app.CanForceClose }], false, "Manual dependency", ManualOnly: true), token);
    internal void Reset() { ClearRunningApplicationsDialog(); runningApplicationsCanceled = false; }
    internal void Notify()
    {
        if (Environment.CurrentManagedThreadId != game.UiThreadId) throw new InvalidOperationException("Controller changed outside the UI thread.");
        Notifications++;
    }
}

internal sealed class AppArguments
{
    internal int? WaitForPid { get; init; }
    internal long? WaitForStartTicks { get; init; }
}

internal sealed class InstallerGame : IDisposable
{
    private readonly BlockingCollection<Action> actions = new();
    private readonly Thread thread;
    internal int UiThreadId => thread.ManagedThreadId;
    internal InstallerGame()
    {
        thread = new Thread(() => { foreach (var action in actions.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "Headless fixture UI dispatcher" };
        thread.Start();
    }
    internal Task OnUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.Add(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        return completion.Task;
    }
    public void Dispose() { actions.CompleteAdding(); thread.Join(); actions.Dispose(); }
}

internal sealed class ClosedPipeStream : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new IOException("Fixture pipe is closed.");
    public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Fixture pipe is closed.");
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("Fixture pipe is closed."));
    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Fixture pipe is closed.");
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("Fixture pipe is closed."));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
