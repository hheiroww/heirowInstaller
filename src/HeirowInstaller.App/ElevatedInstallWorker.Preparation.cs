#if WINDOWS
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static partial class ElevatedInstallWorker
{
    private static readonly object preparationGate = new();
    private static Task<WarmWorker>? preparation;
    internal static int? LastPreparedProcessId { get; private set; }

    internal static Task PrepareAdministratorAsync(string? publisherPin, CancellationToken token, string? productId = null)
    {
        if (InstallerEngine.IsElevated) return Task.CompletedTask;
        lock (preparationGate) return preparation ??= Task.Run(() => PrepareWarmWorkerAsync(publisherPin, productId, token), CancellationToken.None);
    }

    internal static void CancelPreparation()
    {
        Task<WarmWorker>? pending;
        lock (preparationGate) { pending = preparation; preparation = null; }
        if (pending is null) return;
        _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<WarmWorker?> TakePrewarmedAsync(string productId, string? publisherPin, CancellationToken token)
    {
        Task<WarmWorker>? pending;
        lock (preparationGate) { pending = preparation; preparation = null; }
        if (pending is null) return null;
        WarmWorker? warmed = null;
        try
        {
            warmed = await pending.WaitAsync(token);
            await warmed.StopKeepAliveAsync();
            if (warmed.ProductId is not null && warmed.ProductId != productId || warmed.PublisherPin is not null &&
                (publisherPin is null || !PackageSafety.HashEquals(warmed.PublisherPin, publisherPin)))
                throw new InvalidDataException("The prepared administrator session belongs to another reviewed product or publisher.");
            if (warmed.Child.HasExited) { warmed.Dispose(); return null; }
            return warmed;
        }
        catch { warmed?.Dispose(); throw; }
    }

    private static async Task<WarmWorker> PrepareWarmWorkerAsync(string? publisherPin, string? productId, CancellationToken token, string? isolatedFixture = null)
    {
        var image = Path.GetFullPath(DirectX11Fallback.CurrentPrimaryHostPath);
        PackageSafety.EnsureNoReparsePoints(image);
        var imageLock = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        NamedPipeServerStream? pipe = null; BootstrapWorkerLaunch? promotion = null; Process? child = null; WorkerChannel? channel = null;
        try
        {
            var imageHash = PackageSafety.HashFile(image);
            if (DirectX11Fallback.CurrentPrimaryHostSha256 is { } pinned && !PackageSafety.HashEquals(imageHash, pinned))
                throw new CryptographicException("The setup host changed before administrator approval.");
            var parent = ProcessIdentity.Read(Environment.ProcessId); var user = WindowsIdentity.GetCurrent().User!;
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pipeName = "heirowInstaller.install." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            pipe = CreatePipe(pipeName, user);
            if (isolatedFixture is null && BootstrapWorkerPromotion.TryStart(pipeName, parent.Id, parent.StartTicks, nonce, publisherPin, out promotion)) child = promotion!.Process;
            else
            {
                if (isolatedFixture is null) RequireSafeLegacyElevationImage(image);
                var start = new ProcessStartInfo(image) { UseShellExecute = isolatedFixture is null, CreateNoWindow = isolatedFixture is not null, WorkingDirectory = Path.GetDirectoryName(image)!, WindowStyle = ProcessWindowStyle.Hidden };
                if (isolatedFixture is null) start.Verb = "runas";
                foreach (var argument in new[] { Switch, pipeName, parent.Id.ToString(), parent.StartTicks.ToString(), nonce }) start.ArgumentList.Add(argument);
                child = StartWorker(start, token);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            await Task.WhenAny(connected, child.WaitForExitAsync(CancellationToken.None));
            if (child.HasExited && !pipe.IsConnected) throw new InvalidOperationException("The administrator worker could not be prepared.");
            await connected;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || promotion is null && peer != child.Id)
                throw new InvalidDataException("A different process connected to the prepared administrator session.");
            promotion?.ValidatePeer(peer);
            var identity = ProcessIdentity.Read(peer); var expected = promotion?.ExpectedWorkerImage ?? image; var hash = promotion?.ExpectedWorkerSha256 ?? imageHash;
            identity.RequireImage(expected, hash); var identified = IdentifyPipeClient(pipe);
            channel = new WorkerChannel(pipe); var hello = await channel.ReadAsync(timeout.Token);
            if (hello.Kind != "hello" || hello.Hello is not { } greeting || greeting.ProcessId != peer || greeting.StartTicks != identity.StartTicks ||
                greeting.UserSid != identified.Sid || isolatedFixture is null && !identified.Elevated || !FixedEquals(greeting.Nonce, nonce) || !PackageSafety.HashEquals(greeting.ImageSha256, hash))
                throw new InvalidDataException("The prepared administrator session did not match its verified worker.");
            var warmed = new WarmWorker(imageLock, pipe, child, promotion, channel, hello, pipeName, nonce, productId, publisherPin, token);
            LastPreparedProcessId = peer;
            imageLock = null!; pipe = null; child = null; promotion = null; channel = null; return warmed;
        }
        finally { channel?.Dispose(); pipe?.Dispose(); promotion?.Dispose(); child?.Dispose(); imageLock?.Dispose(); }
    }

    internal static async Task<IReadOnlyList<string>> VerifyPreparedFixtureAsync(string root, InstallPlan plan, InstallOptions options)
    {
        ValidateFixture(root, plan.Manifest.Id, options, plan.InstallDirectory);
        var before = Directory.EnumerateFiles(plan.InstallDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, PackageSafety.HashFile, StringComparer.OrdinalIgnoreCase);
        var receipt = Path.Combine(options.StateRoot!, plan.Manifest.Id, "receipt.json"); var receiptHash = PackageSafety.HashFile(receipt);
        Task<WarmWorker> task;
        lock (preparationGate)
        {
            if (preparation is not null) throw new InvalidOperationException("Another administrator preparation is active.");
            task = preparation = Task.Run(() => PrepareWarmWorkerAsync(options.TrustedPublisherKeySha256, plan.Manifest.Id, CancellationToken.None, root));
        }
        var warmed = await task;
        await warmed.Channel.SendAsync(new("keepalive"), CancellationToken.None);
        await Task.Delay(100);
        if (warmed.Child.HasExited || WizardSmoke.CountTopLevelWindows(LastPreparedProcessId!.Value) != 0 ||
            before.Any(pair => !PackageSafety.HashEquals(PackageSafety.HashFile(pair.Key), pair.Value)) || !PackageSafety.HashEquals(PackageSafety.HashFile(receipt), receiptHash))
            throw new InvalidOperationException("The prepared isolated worker changed installation data or opened a window before consent.");
        return ["The actual identification-only pipe authenticated a prepared windowless fixture worker without real UAC", "The idle prepared worker accepted keepalive while every application file and receipt remained unchanged before consent"];
    }

    private sealed class WarmWorker : IDisposable
    {
        private readonly FileStream imageLock;
        private readonly CancellationTokenSource keepAlive;
        private readonly Task keeper;
        internal NamedPipeServerStream Pipe { get; }
        internal Process Child { get; }
        internal BootstrapWorkerLaunch? Promotion { get; }
        internal WorkerChannel Channel { get; }
        internal WorkerMessage Hello { get; }
        internal string PipeName { get; }
        internal string Nonce { get; }
        internal string? ProductId { get; }
        internal string? PublisherPin { get; }
        private int disposed;
        internal WarmWorker(FileStream imageLock, NamedPipeServerStream pipe, Process child, BootstrapWorkerLaunch? promotion,
            WorkerChannel channel, WorkerMessage hello, string pipeName, string nonce, string? productId, string? publisherPin, CancellationToken lifetime)
        {
            this.imageLock = imageLock; Pipe = pipe; Child = child; Promotion = promotion; Channel = channel; Hello = hello; PipeName = pipeName; Nonce = nonce;
            ProductId = productId; PublisherPin = publisherPin;
            keepAlive = CancellationTokenSource.CreateLinkedTokenSource(lifetime); keeper = KeepAliveAsync();
        }
        private async Task KeepAliveAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            try { while (await timer.WaitForNextTickAsync(keepAlive.Token)) await Channel.SendAsync(new("keepalive"), keepAlive.Token); }
            catch (OperationCanceledException) when (keepAlive.IsCancellationRequested) { }
            catch (IOException) { }
        }
        internal async Task StopKeepAliveAsync() { keepAlive.Cancel(); await keeper; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            keepAlive.Cancel(); Pipe.Dispose(); imageLock.Dispose(); Child.Dispose(); Promotion?.Dispose();
            _ = keeper.ContinueWith(_ => { Channel.Dispose(); keepAlive.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
#endif
