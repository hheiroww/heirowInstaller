using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.App;

/// <summary>The existing wizard owns all UI; this same-image child only runs the verified engine.</summary>
internal static class ElevatedInstallWorker
{
    private const string Switch = "--install-worker";
    private const string FixtureMarker = "heirowInstaller isolated wizard acceptance v1\n";
    internal sealed record Observation(int ProcessId, long StartTicks, bool Isolated, string ImageSha256, bool Completed);
    internal static Observation? LastObservation { get; private set; }

    internal static bool IsWorker(string[] args) => args.FirstOrDefault() == Switch;

    internal static async Task<InstallResult> InstallAsync(string packagePath, InstallPlan plan, InstallOptions options,
        Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>> prompt,
        IProgress<InstallProgress>? progress, string? isolatedFixtureRoot, CancellationToken token, string operation = "install")
    {
        var image = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("The setup image is unavailable."));
        PackageSafety.EnsureNoReparsePoints(image);
        // Hold both inputs unchanged across the human UAC decision and the entire operation.
        using var imageLock = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var packageLock = File.Open(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var imageHash = PackageSafety.HashFile(image);
        var hostBinding = ReadHostBinding(image, imageHash);
        var parent = ProcessIdentity.Read(Environment.ProcessId);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pipeName = "heirowInstaller.install." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
        using var pipe = CreatePipe(pipeName, user);
        var test = isolatedFixtureRoot is not null;
        if (test) ValidateFixture(isolatedFixtureRoot!, plan.Manifest.Id, options, plan.InstallDirectory);
        var start = new ProcessStartInfo(image) { UseShellExecute = !test, WorkingDirectory = Path.GetDirectoryName(image)!, WindowStyle = ProcessWindowStyle.Hidden };
        if (test) start.CreateNoWindow = true;
        else start.Verb = "runas"; // Windows owns the ordinary, human UAC prompt.
        foreach (var value in new[] { Switch, pipeName, parent.Id.ToString(), parent.StartTicks.ToString(), nonce }) start.ArgumentList.Add(value);
        using var child = StartWorker(start, token);
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var connected = pipe.WaitForConnectionAsync(connectionTimeout.Token);
        await Task.WhenAny(connected, child.WaitForExitAsync(CancellationToken.None));
        if (!pipe.IsConnected && child.HasExited)
            throw new InvalidOperationException("Windows could not connect the installation worker. Approve setup with this Windows account, or cancel and contact the application publisher.");
        await connected;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peerId) || peerId != child.Id)
            throw new InvalidDataException("The installation worker identity could not be verified.");
        var identity = ProcessIdentity.Read(child.Id);
        identity.RequireImage(image, imageHash);
        using var channel = new WorkerChannel(pipe);
        var hello = await channel.ReadAsync(connectionTimeout.Token);
        if (hello.Kind != "hello" || hello.Hello is not { } h || h.ProcessId != child.Id || h.StartTicks != identity.StartTicks ||
            h.UserSid != user.Value || !FixedEquals(h.Nonce, nonce) || !PackageSafety.HashEquals(h.ImageSha256, imageHash))
            throw new InvalidDataException("The installation worker handshake was rejected.");
        LastObservation = new(child.Id, identity.StartTicks, test, imageHash, false);
        var request = new WorkerRequest(packagePath, PackageSafety.HashFile(packagePath), plan.Manifest.Id, plan.Manifest.Version,
            plan.InstallDirectory, Path.GetFullPath(options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope)),
            user.Value, nonce, operation, WorkerInstallOptions.From(options), isolatedFixtureRoot);
        await channel.SendAsync(new("install") { Install = request }, connectionTimeout.Token);
        using var cancellation = token.Register(() => channel.TryCancel());
        InstallResult? result = null;
        while (result is null)
        {
            var message = await ReadOperationMessageAsync(channel, token);
            switch (message.Kind)
            {
                case "progress" when message.Progress is { } value:
                    progress?.Report(value); break;
                case "applications" when message.Applications is { } applications && Guid.TryParseExact(message.RequestId, "N", out _):
                    if (applications.ProductId != plan.Manifest.Id || applications.ProductName != plan.Manifest.Name || applications.Operation != operation || applications.Applications.Count > 64)
                        throw new InvalidDataException("The worker requested consent for a different application.");
                    RunningApplicationsChoice choice;
                    try { choice = await prompt(applications, token); }
                    catch (OperationCanceledException) { choice = RunningApplicationsChoice.Cancel; }
                    await SendOperationMessageAsync(channel, new("choice") { RequestId = message.RequestId, Choice = choice }, token);
                    break;
                case "result" when message.Result is { Success: true } completed:
                    if (completed.ProductId != request.ProductId || completed.Version != request.Version ||
                        !SamePath(completed.ReceiptPath, Path.Combine(request.StateRoot, request.ProductId, "receipt.json")))
                        throw new InvalidDataException("The installation result did not match the reviewed application and destination.");
                    result = completed; break;
                case "canceled":
                    await child.WaitForExitAsync(CancellationToken.None);
                    throw new OperationCanceledException("Installation was canceled.", token);
                case "error":
                    await child.WaitForExitAsync(CancellationToken.None);
                    throw new InvalidOperationException(message.Error ?? "The installation worker could not finish.");
                default: throw new InvalidDataException("The installation worker sent an unexpected message.");
            }
        }
        await child.WaitForExitAsync(CancellationToken.None);
        if (child.ExitCode is not (0 or 3010)) throw new InvalidOperationException($"The installation worker returned exit code {child.ExitCode}.");
        ValidateCompletedState(request, plan, result, hostBinding?.PublisherPin);
        LastObservation = LastObservation! with { Completed = true };
        return result;
    }

    private static async Task<WorkerMessage> ReadOperationMessageAsync(WorkerChannel channel, CancellationToken token)
    {
        try { return await channel.ReadAsync(CancellationToken.None); }
        catch (IOException error) when (token.IsCancellationRequested)
        { throw new OperationCanceledException("Installation was canceled.", error, token); }
    }

    private static async Task SendOperationMessageAsync(WorkerChannel channel, WorkerMessage message, CancellationToken token)
    {
        try { await channel.SendAsync(message, CancellationToken.None); }
        catch (IOException error) when (token.IsCancellationRequested)
        { throw new OperationCanceledException("Installation was canceled.", error, token); }
    }

    // This route is dispatched before DPI setup, Game construction or any dialog.
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5 || args[0] != Switch || args[1].Length is < 32 or > 100 ||
            !args[1].StartsWith("heirowInstaller.install.", StringComparison.Ordinal) || args[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.') ||
            !int.TryParse(args[2], out var parentPid) || parentPid <= 0 || parentPid == Environment.ProcessId ||
            !long.TryParse(args[3], out var parentTicks) || parentTicks <= 0 || args[4].Length != 64 || !args[4].All(Uri.IsHexDigit)) return 2;
        try
        {
            var image = Path.GetFullPath(Environment.ProcessPath!);
            PackageSafety.EnsureNoReparsePoints(image);
            using var imageLock = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            var imageHash = PackageSafety.HashFile(image);
            var hostBinding = ReadHostBinding(image, imageHash);
            var parent = ProcessIdentity.Read(parentPid);
            if (parent.StartTicks != parentTicks) throw new InvalidDataException("The original wizard is no longer running.");
            parent.RequireImage(image, imageHash);
            // Anonymous SQOS prevents a medium-integrity server from impersonating this elevated client.
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var peer) || peer != parentPid)
                throw new InvalidDataException("The original wizard connection could not be verified.");
            if (ProcessIdentity.Read(parentPid).StartTicks != parentTicks) throw new InvalidDataException("The original wizard identity changed.");
            using var channel = new WorkerChannel(pipe);
            var userSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidDataException("The Windows user is unavailable.");
            var self = ProcessIdentity.Read(Environment.ProcessId);
            await channel.SendAsync(new("hello") { Hello = new(Environment.ProcessId, self.StartTicks, imageHash, userSid, args[4]) }, timeout.Token);
            var initial = await channel.ReadAsync(timeout.Token);
            if (initial.Kind != "install" || initial.Install is not { } request || request.UserSid != userSid || !FixedEquals(request.Nonce, args[4]))
                throw new InvalidDataException("The installation request was rejected.");
            using var operation = new CancellationTokenSource();
            using var listener = new CancellationTokenSource();
            var choices = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<RunningApplicationsChoice>>();
            var commands = ReadCommandsAsync(channel, choices, operation, listener.Token);
            try
            {
                ValidateRequest(request);
                using var packageLock = File.Open(request.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!PackageSafety.HashEquals(PackageSafety.HashFile(request.PackagePath), request.PackageSha256)) throw new CryptographicException("The reviewed package changed before installation.");
                using var package = PackageReader.Open(request.PackagePath);
                package.VerifySignature(request.Options.TrustedPublisherKeySha256, request.Options.RequireSignature);
                if (package.Manifest.Id != request.ProductId || package.Manifest.Version != request.Version) throw new InvalidDataException("The reviewed application identity changed.");
                ValidateHostBinding(hostBinding, package, request);
                await package.VerifyAsync(cancellationToken: operation.Token);
                var engine = new InstallerEngine();
                var options = request.Options.ToOptions();
                if (hostBinding?.PublisherPin is { } boundPin) { options.TrustedPublisherKeySha256 = boundPin; options.RequireSignature = true; }
                options.InstallerPath = image;
                var plan = engine.Plan(package, options);
                if (!SamePath(plan.InstallDirectory, request.InstallDirectory) || !SamePath(options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope), request.StateRoot))
                    throw new InvalidDataException("The reviewed installation destination changed.");
                if (request.IsolatedFixtureRoot is { } fixture) ValidateFixture(fixture, request.ProductId, options, plan.InstallDirectory);
                else if (!InstallerEngine.IsElevated) throw new InvalidOperationException("Windows did not grant administrator access.");
                options.StateRoot = request.StateRoot;
                if (request.Operation != "install")
                {
                    var installed = InstallerEngine.ListInstalled(request.StateRoot).SingleOrDefault(x => x.Id == request.ProductId && x.Scope == plan.Scope)
                        ?? throw new InvalidDataException("The reviewed application is no longer installed.");
                    if (installed.Version != request.Version || !SamePath(installed.InstallDirectory, request.InstallDirectory) || !SamePath(installed.PackagePath, request.PackagePath))
                        throw new InvalidDataException("The installed application no longer matches the reviewed maintenance operation.");
                }
                options.RunningApplicationsPrompt = async (prompt, cancellation) =>
                {
                    if (prompt.ProductId != request.ProductId || prompt.ProductName != package.Manifest.Name)
                        throw new InvalidDataException("The engine requested consent for another application.");
                    // Native Core repair reuses its installation transaction and exact-file guards.
                    // Keep its closure authority intact while displaying the caller's repair operation.
                    if (request.Operation == "repair" && prompt.Operation == "install" && package.Manifest.LegacyMsi is null && package.Manifest.LegacyExecutable is null)
                        prompt = prompt with { Operation = "repair" };
                    if (prompt.Operation != request.Operation) throw new InvalidDataException("The engine maintenance operation changed.");
                    var id = Guid.NewGuid().ToString("N");
                    var answer = new TaskCompletionSource<RunningApplicationsChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (prompt.Applications.Count > 64 || !choices.TryAdd(id, answer)) throw new InvalidDataException("Too many applications require attention.");
                    using var registration = cancellation.Register(() => answer.TrySetCanceled(cancellation));
                    try
                    {
                        await channel.SendAsync(new("applications") { RequestId = id, Applications = prompt }, cancellation);
                        return await answer.Task;
                    }
                    finally { choices.TryRemove(id, out _); }
                };
                var progress = new WorkerProgress(channel, operation);
                var result = request.Operation switch
                {
                    "install" => await engine.InstallAsync(package, options, progress, operation.Token),
                    "repair" => await engine.RepairAsync(request.ProductId, options, progress, operation.Token),
                    "uninstall" => await engine.UninstallAsync(request.ProductId, options, progress, operation.Token),
                    _ => throw new InvalidDataException("The maintenance operation was rejected.")
                };
                await channel.SendAsync(new("result") { Result = result }, CancellationToken.None);
                return result.RebootRequired ? 3010 : 0;
            }
            catch (OperationCanceledException)
            {
                await channel.SendAsync(new("canceled"), CancellationToken.None); return 1602;
            }
            catch (Exception error)
            {
                await channel.SendAsync(new("error") { Error = error.Message.Length > 2000 ? error.Message[..2000] : error.Message }, CancellationToken.None); return 1;
            }
            finally
            {
                listener.Cancel();
                try { await commands; } catch (OperationCanceledException) { }
            }
        }
        catch { return 1; } // A worker must never open an exception dialog or a second native window.
    }

    private static async Task ReadCommandsAsync(WorkerChannel channel,
        System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<RunningApplicationsChoice>> choices,
        CancellationTokenSource operation, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await channel.ReadAsync(token);
                if (message.Kind == "cancel") operation.Cancel();
                else if (message.Kind == "choice" && message.RequestId is { } id && message.Choice is { } choice && Enum.IsDefined(choice) && choices.TryGetValue(id, out var answer)) answer.TrySetResult(choice);
                else throw new InvalidDataException("The wizard sent an unexpected command.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { operation.Cancel(); }
    }

    internal static async Task<IReadOnlyList<string>> VerifyProtocolRejectionsAsync(string root, string packagePath, InstallPlan plan, InstallOptions options, int alternateParentPid)
    {
        ValidateFixture(root, plan.Manifest.Id, options, plan.InstallDirectory);
        var image = Path.GetFullPath(Environment.ProcessPath!);
        var parent = ProcessIdentity.Read(Environment.ProcessId);
        var user = WindowsIdentity.GetCurrent().User!;
        var checks = new List<string>();
        var owned = Path.Combine(plan.InstallDirectory, "notes.txt");
        var receipt = Path.Combine(options.StateRoot!, plan.Manifest.Id, "receipt.json");
        var beforeFile = PackageSafety.HashFile(owned); var beforeReceipt = PackageSafety.HashFile(receipt);
        foreach (var test in new[] { "stale-parent", "parent-image", "nonce", "digest", "destination", "state-root", "oversize" })
        {
            var pipeName = "heirowInstaller.install." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var pipe = CreatePipe(pipeName, user);
            var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(image)! };
            var selectedParent = test == "parent-image" ? ProcessIdentity.Read(alternateParentPid) : parent;
            foreach (var value in new[] { Switch, pipeName, selectedParent.Id.ToString(), (selectedParent.StartTicks + (test == "stale-parent" ? 1 : 0)).ToString(), nonce }) start.ArgumentList.Add(value);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The owned protocol fixture did not start.");
            if (test is not ("stale-parent" or "parent-image"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await pipe.WaitForConnectionAsync(timeout.Token);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != child.Id) throw new InvalidDataException("The protocol fixture connected a different process.");
                using var channel = new WorkerChannel(pipe);
                if ((await channel.ReadAsync(timeout.Token)).Kind != "hello") throw new InvalidDataException("The protocol fixture did not establish its handshake.");
                if (WizardSmoke.CountTopLevelWindows(child.Id) != 0) throw new InvalidOperationException("The rejected worker opened a native window.");
                if (test == "oversize")
                {
                    var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, 256 * 1024 + 1);
                    await pipe.WriteAsync(header, timeout.Token); await pipe.FlushAsync(timeout.Token);
                }
                else
                {
                    var request = new WorkerRequest(packagePath, test == "digest" ? new string('0', 64) : PackageSafety.HashFile(packagePath),
                        plan.Manifest.Id, plan.Manifest.Version, test == "destination" ? Path.Combine(root, "unexpected-target") : plan.InstallDirectory,
                        test == "state-root" ? Path.Combine(root, "unexpected-state") : Path.GetFullPath(options.StateRoot!), user.Value,
                        test == "nonce" ? new string('0', 64) : nonce, "install", WorkerInstallOptions.From(options), root);
                    await channel.SendAsync(new("install") { Install = request }, timeout.Token);
                }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            else await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (child.ExitCode == 0 || !PackageSafety.HashEquals(PackageSafety.HashFile(owned), beforeFile) ||
                !PackageSafety.HashEquals(PackageSafety.HashFile(receipt), beforeReceipt) || Directory.Exists(Path.Combine(root, "unexpected-target")) || Directory.Exists(Path.Combine(root, "unexpected-state")))
                throw new InvalidOperationException("The rejected protocol fixture changed installation data.");
            checks.Add($"The actual windowless worker rejected {test} input before changing files or its receipt");
        }
        return checks;
    }

    private static NamedPipeServerStream CreatePipe(string name, SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 16 * 1024, 16 * 1024, security, HandleInheritability.None);
    }

    private static Process StartWorker(ProcessStartInfo start, CancellationToken token)
    {
        try { return Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the installation worker."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Administrator approval was canceled. You can try again when you are ready.", error, token); }
    }

    private static void ValidateRequest(WorkerRequest request)
    {
        PackageSafety.ValidateId(request.ProductId);
        _ = PackageVersion.Parse(request.Version);
        if (request.Operation is not ("install" or "repair" or "uninstall")) throw new InvalidDataException("The worker operation was rejected.");
        foreach (var path in new[] { request.PackagePath, request.InstallDirectory, request.StateRoot })
        {
            if (!Path.IsPathFullyQualified(path) || path.Length > 30_000) throw new InvalidDataException("The worker requires exact local installation paths.");
            PackageSafety.EnsureNoReparsePoints(path);
        }
        if (request.PackageSha256.Length != 64 || !request.PackageSha256.All(Uri.IsHexDigit) || request.Options.Features is { Count: > 256 } || request.Options.LegacyArguments.Count > 128 ||
            request.Options.LegacyArguments.Any(x => x.Length > 8192 || x.Contains('\0')))
            throw new InvalidDataException("The installation options were rejected.");
        if (request.Options.Isolated != (request.IsolatedFixtureRoot is not null)) throw new InvalidDataException("Isolated workers require an explicit acceptance fixture.");
    }

    private static void ValidateCompletedState(WorkerRequest request, InstallPlan reviewed, InstallResult result, string? hostPin)
    {
        PackageSafety.EnsureNoReparsePoints(result.InstallDirectory);
        var product = InstallerEngine.ListInstalled(request.StateRoot).SingleOrDefault(x => x.Id == request.ProductId && x.Scope == reviewed.Scope);
        if (request.Operation == "uninstall")
        {
            if (product is not null || File.Exists(result.ReceiptPath) || !SamePath(result.InstallDirectory, request.InstallDirectory))
                throw new InvalidDataException("Uninstallation did not remove the reviewed application receipt.");
            return;
        }
        if (product is null || product.Version != request.Version || !SamePath(product.InstallDirectory, result.InstallDirectory) ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(product.PackagePath), request.PackageSha256) ||
            (hostPin ?? request.Options.TrustedPublisherKeySha256) is { } pin && !PackageSafety.HashEquals(product.PublisherKeySha256 ?? "", pin))
            throw new InvalidDataException("The completed installation did not match the verified application receipt.");
        if (SamePath(result.InstallDirectory, request.InstallDirectory)) return;
        // An authored MSI default can resolve its directory only after Windows Installer completes.
        // Accept that result only through the engine's committed, matching MSI receipt/package.
        if (request.Operation != "install" || !reviewed.UseOriginalInstallDirectory || reviewed.Manifest.LegacyMsi is null ||
            !product.UseOriginalInstallDirectory || !product.IsMsi ||
            reviewed.Manifest.LegacyMsi.ProductCode is { Length: > 0 } code && !code.Equals(product.MsiProductCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The completed installation destination changed from the reviewed plan.");
    }

    private sealed record HostBinding(string ProductId, string? PublisherPin, string ImageSha256);
    private static HostBinding? ReadHostBinding(string image, string imageHash)
    {
        if (!PackageReader.HasEmbeddedPackage(image)) return null;
        using var ownPackage = PackageReader.Open(image);
        ownPackage.VerifySignature();
        return new(ownPackage.Manifest.Id, ownPackage.PublisherKeySha256, imageHash);
    }

    private static void ValidateHostBinding(HostBinding? host, PackageReader package, WorkerRequest request)
    {
        if (host is null) return; // The unbound authoring application can install different products.
        if (package.Manifest.Id != host.ProductId) throw new InvalidDataException("This setup belongs to a different application. Use that application's own installer.");
        if (host.PublisherPin is { } pin)
        {
            if (request.Options.TrustedPublisherKeySha256 is { } requested && !PackageSafety.HashEquals(requested, pin))
                throw new CryptographicException("The requested publisher does not match this setup.");
            package.VerifySignature(pin, requireSignature: true);
        }
        else if (package.PublisherKeySha256 is not null || !PackageSafety.HashEquals(request.PackageSha256, host.ImageSha256))
            throw new CryptographicException("An unsigned bundled setup can only apply its own unchanged package. Use the new application's installer.");
    }

    private static void ValidateFixture(string root, string productId, InstallOptions options, string target)
    {
        root = Path.GetFullPath(root);
        PackageSafety.EnsureNoReparsePoints(root);
        PackageSafety.EnsureNoReparsePoints(Path.Combine(root, ".isolated-wizard-fixture"));
        if (!productId.StartsWith("heirowInstaller.wizard-fixture", StringComparison.Ordinal) || !options.Isolated || options.RegisterProduct ||
            options.StateRoot is null || !SamePath(options.StateRoot, Path.Combine(root, "state")) || !SamePath(target, Path.Combine(root, "installed")) ||
            !File.Exists(Path.Combine(root, ".isolated-wizard-fixture")) || File.ReadAllText(Path.Combine(root, ".isolated-wizard-fixture")) != FixtureMarker)
            throw new InvalidDataException("The windowless worker fixture must use its own marked, isolated installation and state directories.");
    }

    private static bool SamePath(string left, string right) => Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool FixedEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private sealed record WorkerHello(int ProcessId, long StartTicks, string ImageSha256, string UserSid, string Nonce);
    // An explicit allowlist prevents future Core options or credential/context objects entering IPC by accident.
    private sealed record WorkerInstallOptions(string? TargetDirectory, string? StateRoot, string? Scope, List<string>? Features,
        bool RegisterProduct, bool Isolated, string? LogPath, List<string> LegacyArguments, bool RequireSignature, string? TrustedPublisherKeySha256)
    {
        internal static WorkerInstallOptions From(InstallOptions options) => new(options.TargetDirectory, options.StateRoot, options.Scope,
            options.Features?.ToList(), options.RegisterProduct, options.Isolated, options.LogPath, options.LegacyArguments.ToList(), options.RequireSignature, options.TrustedPublisherKeySha256);
        internal InstallOptions ToOptions() => new() { TargetDirectory = TargetDirectory, StateRoot = StateRoot, Scope = Scope, Features = Features?.ToList(),
            RegisterProduct = RegisterProduct, Isolated = Isolated, LogPath = LogPath, LegacyArguments = LegacyArguments.ToList(), RequireSignature = RequireSignature,
            TrustedPublisherKeySha256 = TrustedPublisherKeySha256, Silent = true, Launch = false };
    }
    private sealed record WorkerRequest(string PackagePath, string PackageSha256, string ProductId, string Version, string InstallDirectory, string StateRoot, string UserSid, string Nonce, string Operation, WorkerInstallOptions Options, string? IsolatedFixtureRoot);
    private sealed record WorkerMessage(string Kind)
    {
        public WorkerHello? Hello { get; init; }
        public WorkerRequest? Install { get; init; }
        public InstallProgress? Progress { get; init; }
        public RunningApplicationsRequest? Applications { get; init; }
        public string? RequestId { get; init; }
        public RunningApplicationsChoice? Choice { get; init; }
        public InstallResult? Result { get; init; }
        public string? Error { get; init; }
    }

    private sealed class WorkerProgress(WorkerChannel channel, CancellationTokenSource operation) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value)
        {
            try { channel.SendAsync(new("progress") { Progress = value }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch { operation.Cancel(); }
        }
    }

    private sealed class WorkerChannel(Stream stream) : IDisposable
    {
        private const int Limit = 256 * 1024;
        private readonly SemaphoreSlim writer = new(1);
        private static readonly JsonSerializerOptions Json = new(InstallerJson.Options) { MaxDepth = 24, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        internal async Task SendAsync(WorkerMessage value, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > Limit) throw new InvalidDataException("The installation message exceeds its size limit.");
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await writer.WaitAsync(token);
            try { await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); }
            finally { writer.Release(); }
        }
        internal async Task<WorkerMessage> ReadAsync(CancellationToken token)
        {
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 2 or > Limit) throw new InvalidDataException("The installation message length was rejected.");
            var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
            return JsonSerializer.Deserialize<WorkerMessage>(bytes, Json) ?? throw new InvalidDataException("The installation message was empty.");
        }
        internal async void TryCancel() { try { await SendAsync(new("cancel"), CancellationToken.None); } catch { } }
        public void Dispose() => writer.Dispose();
    }

    private sealed record ProcessIdentity(int Id, long StartTicks, string Image)
    {
        internal static ProcessIdentity Read(int pid)
        {
            using var handle = OpenProcess(0x1000, false, pid);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var text = new StringBuilder(32768); var length = text.Capacity;
            if (!QueryFullProcessImageName(handle, 0, text, ref length) || !GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(pid, DateTime.FromFileTimeUtc(created).Ticks, Path.GetFullPath(text.ToString()));
        }
        internal void RequireImage(string expected, string hash)
        {
            PackageSafety.EnsureNoReparsePoints(Image);
            if (!SamePath(Image, expected) || !PackageSafety.HashEquals(PackageSafety.HashFile(Image), hash)) throw new InvalidDataException("The wizard and worker must use the same unchanged setup image.");
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int pid);
}
