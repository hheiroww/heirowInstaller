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
        await VerifyGuardLifecycleAsync(game, application, manual);
        await VerifyCoreGuardCompletionAsync(game, application);
        VerifyWindowPlacement();
        await VerifyCanceledTransportAsync();
        Console.WriteLine($"Passed {assertions} headless close-apps controller checks. No windows, input, or process actions were used.");
    }

    private static async Task VerifyGuardLifecycleAsync(InstallerGame game, RunningApplication application, RunningApplication manual)
    {
        using var stop = new CancellationTokenSource();
        var controller = new AppController(game, stop);
        var oldId = Guid.NewGuid().ToString("N"); var newId = Guid.NewGuid().ToString("N");
        RunningApplicationsRequest Request(string id) => new("fixture.app", "Fixture app", "install", [application, manual], false, "Fixture guard") { GuardId = id };
        var request = Request(oldId);
        var prompt = controller.PromptAsync(request, stop.Token);
        await game.OnUiAsync(() =>
        {
            Check(request.NotifyCompletion, "stateful installer UI opts into guard completion");
            controller.DismissRunningApplications();
            Check(controller.RunningApplicationsDialog is not null && !prompt.IsCompleted, "dismiss cannot answer or hide an active consent request");
            controller.CloseRunningApplications();
        });
        Check(await prompt.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.CloseAppsAndContinue, "guard retains the explicit close choice");
        await game.OnUiAsync(() =>
        {
            Check(controller.CanDismissRunningApplications, "an answered waiting panel has a safe Close action");
            controller.DismissRunningApplications();
            Check(controller.RunningApplicationsDialog is null && !stop.IsCancellationRequested && controller.Busy, "Close dismisses only presentation while the operation remains busy");
            controller.ForceCloseRunningApplications(); controller.CheckRunningApplications();
        });
        var retry = controller.PromptAsync(Request(oldId), stop.Token);
        await game.OnUiAsync(() =>
        {
            Check(!retry.IsCompleted, "dismissing an answered panel never supplies consent to a retry");
            controller.CheckRunningApplications();
        });
        _ = await retry.WaitAsync(TimeSpan.FromSeconds(2));
        var terminalChoice = await controller.PromptAsync(Request(oldId) with { Applications = [], Completed = true }, CancellationToken.None);
        await game.OnUiAsync(() => Check(controller.RunningApplicationsDialog is null && controller.Busy,
            "guard success clears the panel immediately while installation is still busy"));
        Check(terminalChoice == RunningApplicationsChoice.CheckAgain, "terminal cleanup never returns force or close consent");

        var next = controller.PromptAsync(Request(newId), stop.Token);
        _ = await controller.PromptAsync(Request(oldId) with { Applications = [], Completed = true }, CancellationToken.None);
        await game.OnUiAsync(() =>
        {
            Check(controller.RunningApplicationsDialog?.GuardId == newId && !next.IsCompleted, "stale completion cannot clear a newer guard or resolve its choice");
            controller.CheckRunningApplications();
            controller.ConfirmForceCloseRunningApplications(); controller.ForceCloseRunningApplications();
        });
        _ = await next.WaitAsync(TimeSpan.FromSeconds(2));
        var unrelated = controller.PromptAsync(Request(Guid.NewGuid().ToString("N")), stop.Token);
        await game.OnUiAsync(() =>
        {
            Check(!unrelated.IsCompleted && !controller.RunningApplicationsDialog!.ConfirmForceClose, "identical processes in a new guard cannot inherit queued force consent");
            controller.CancelRunningApplications();
        });
        Check(await unrelated.WaitAsync(TimeSpan.FromSeconds(2)) == RunningApplicationsChoice.Cancel, "cancel still cancels an active new guard");
        var canceledId = controller.RunningApplicationsDialog!.GuardId;
        _ = await controller.PromptAsync(Request(canceledId) with { Applications = [], Completed = true }, CancellationToken.None);
        await game.OnUiAsync(() => Check(controller.RunningApplicationsDialog is null, "terminal notification clears canceled or failed guard presentation"));
        await VerifyCompletionTransportAsync(controller, Request(oldId) with { Applications = [], Completed = true });
    }

    private static void VerifyWindowPlacement()
    {
        var center = typeof(DesktopWindowPlacement).GetMethod("CenterPosition", BindingFlags.NonPublic | BindingFlags.Static)!;
        (int X, int Y) Position(int x, int y, int width, int height, int windowWidth, int windowHeight) =>
            ((int X, int Y))center.Invoke(null, [x, y, width, height, windowWidth, windowHeight])!;
        Check(Position(0, 0, 1920, 1040, 1060, 800) == (430, 120), "placement uses the work area excluding the taskbar");
        Check(Position(-1920, -200, 1920, 1040, 1060, 800) == (-1490, -80), "placement preserves negative monitor coordinates");
        Check(Position(0, 40, 1280, 680, 1500, 1000) == (0, 40), "oversized windows keep title-bar access within a small work area");
        foreach (var scale in new[] { 1, 1.5, 2 })
        {
            var windowWidth = (int)(780 * scale); var windowHeight = (int)(510 * scale);
            var result = Position(2560, 0, 3840, 2080, windowWidth, windowHeight);
            Check(Math.Abs((result.X - 2560) * 2 + windowWidth - 3840) <= 1 && Math.Abs(result.Y * 2 + windowHeight - 2080) <= 1,
                "DPI-adjusted window bounds center within the selected monitor");
        }
        Check(!DesktopWindowPlacement.Center(0), "placement safely ignores missing native windows");
    }

    private static async Task VerifyCoreGuardCompletionAsync(InstallerGame game, RunningApplication application)
    {
        var inspect = typeof(RunningApplications).GetMethod("OverrideInspectionForTests", BindingFlags.NonPublic | BindingFlags.Static)!;
        var target = RunningApplicationTarget.FromManifest(new() { Id = "fixture.app", Name = "Fixture app", EntryPoint = "App.exe" }, @"C:\Fixture");
        var observed = new RunningApplication(application.ProcessId, application.StartTimeUtcTicks, application.ExecutablePath,
            application.DisplayName, application.AffectedPath, "application", false);
        var open = 1;
        Func<RunningApplicationTarget, IReadOnlyList<RunningApplication>> probe = _ => Volatile.Read(ref open) == 1 ? [observed] : [];
        using (var scope = (IDisposable)inspect.Invoke(null, [probe])!)
        using (var stop = new CancellationTokenSource())
        {
            var controller = new AppController(game, stop);
            var guard = RunningApplications.EnsureClosedAsync(target, controller.PromptAsync, stop.Token);
            await UntilAsync(async () => { var visible = false; await game.OnUiAsync(() => visible = controller.RunningApplicationsDialog is not null); return visible; });
            Volatile.Write(ref open, 0);
            await game.OnUiAsync(controller.CheckRunningApplications);
            await guard.WaitAsync(TimeSpan.FromSeconds(5));
            await game.OnUiAsync(() => Check(controller.Busy && controller.RunningApplicationsDialog is null,
                "actual Core guard clears its UI after quiet reinspection while installation remains busy"));
        }
        Volatile.Write(ref open, 1);
        using (var scope = (IDisposable)inspect.Invoke(null, [probe])!)
        {
            foreach (var error in new[] { false, true })
            {
                var completed = false;
                try
                {
                    await RunningApplications.EnsureClosedAsync(target, (request, _) =>
                    {
                        if (request.Completed) { completed = true; throw new ObjectDisposedException("Fixture UI"); }
                        request.NotifyCompletion = true;
                        if (error) throw new InvalidDataException("Original guard failure");
                        return Task.FromResult(RunningApplicationsChoice.Cancel);
                    });
                    throw new Exception("Guard unexpectedly succeeded.");
                }
                catch (InvalidDataException failure) when (error && failure.Message == "Original guard failure") { assertions++; }
                catch (OperationCanceledException) when (!error) { assertions++; }
                Check(completed, "Core emits terminal cleanup on cancellation and errors without masking them");
            }
            var callbacks = 0;
            try { await RunningApplications.EnsureClosedAsync(target, (_, _) => { callbacks++; return Task.FromResult(RunningApplicationsChoice.Cancel); }); }
            catch (OperationCanceledException) { }
            Check(callbacks == 1, "legacy callbacks retain their original prompt count without completion opt-in");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await RunningApplications.EnsureClosedAsync(target, (request, _) =>
                {
                    if (request.Completed) return new TaskCompletionSource<RunningApplicationsChoice>().Task;
                    request.NotifyCompletion = true; return Task.FromResult(RunningApplicationsChoice.Cancel);
                });
                throw new Exception("Guard unexpectedly succeeded.");
            }
            catch (OperationCanceledException) { assertions++; }
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "an unresponsive UI cannot hold terminal guard cleanup open indefinitely");
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Fixture guard UI did not appear.");
            await Task.Delay(10);
        }
    }

    private static async Task VerifyCompletionTransportAsync(AppController controller, RunningApplicationsRequest terminal)
    {
        var worker = Assembly.Load("heirowInstaller").GetType("HeirowInstaller.App.ElevatedInstallWorker", throwOnError: true)!;
        var channelType = worker.GetNestedType("WorkerChannel", BindingFlags.NonPublic)!;
        var messageType = worker.GetNestedType("WorkerMessage", BindingFlags.NonPublic)!;
        using var stream = new MemoryStream();
        using var channel = (IDisposable)Activator.CreateInstance(channelType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [stream], null)!;
        var message = Activator.CreateInstance(messageType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, ["applications"], null)!;
        messageType.GetProperty("Applications")!.SetValue(message, terminal);
        messageType.GetProperty("RequestId")!.SetValue(message, Guid.NewGuid().ToString("N"));
        messageType.GetProperty("NotifyCompletion")!.SetValue(message, true);
        var send = (Task)channelType.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(channel, [message, CancellationToken.None])!;
        await send;
        stream.Position = 0;
        var receive = (Task)channelType.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(channel, [CancellationToken.None])!;
        await receive;
        var received = receive.GetType().GetProperty("Result")!.GetValue(receive)!;
        var request = (RunningApplicationsRequest)messageType.GetProperty("Applications")!.GetValue(received)!;
        Check(request.Completed && request.GuardId == terminal.GuardId && (bool)messageType.GetProperty("NotifyCompletion")!.GetValue(received)!,
            "real elevated-worker framing preserves guard completion and opt-in fields");
        Check(await controller.PromptAsync(request, CancellationToken.None) == RunningApplicationsChoice.CheckAgain,
            "relayed terminal notification creates no pending consent task");
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
    internal bool Busy { get; set; } = true;
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
