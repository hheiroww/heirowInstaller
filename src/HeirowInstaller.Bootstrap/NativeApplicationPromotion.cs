using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using HeirowInstaller.BootstrapContract;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.Bootstrap;

internal static class NativeApplicationPromotion
{
    private const string Mode = "--bootstrap-application";

    // The original native process remains the pipe server and stays alive until
    // its exact elevated child exits. No argv-supplied profile or cache is used.
    internal static async Task<int> RequestAsync(BootstrapDescriptor descriptor, string[] arguments, BootstrapWindow window, CancellationToken token)
    {
        if (descriptor.HostKind != "application") throw new InvalidDataException("Application approval requires a verified application binding.");
        ValidateArguments(arguments);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidDataException("The original application user is unavailable.");
        string name = "socketjack-application-" + Guid.NewGuid().ToString("N"), nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var pipe = CreatePipe(name, user);
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory };
        foreach (string value in new[] { Mode, name, parent.Id.ToString(CultureInfo.InvariantCulture), parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), nonce }) start.ArgumentList.Add(value);
        window.Report(new("Requesting administrator access", "Approve Windows' prompt to open " + descriptor.Name + "."));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the approved application host.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await pipe.WaitForConnectionAsync(deadline.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != child.Id) throw new CryptographicException("The application approval peer changed.");
        byte[] proof = new byte[32]; await pipe.ReadExactlyAsync(proof, deadline.Token);
        if (!CryptographicOperations.FixedTimeEquals(proof, Convert.FromHexString(nonce))) throw new CryptographicException("The application approval nonce changed.");
        await WriteHandoffAsync(pipe, nonce, DescriptorDigest(), arguments, deadline.Token);
        var accepted = new byte[1]; await pipe.ReadExactlyAsync(accepted, deadline.Token);
        if (accepted[0] != 1) throw new CryptographicException("The approved application host rejected its parent.");
        window.Hide(); await child.WaitForExitAsync(CancellationToken.None); return child.ExitCode;
    }

    internal static int Run(string[] args, BootstrapDescriptor descriptor)
    {
        if (args.Length != 5 || descriptor.HostKind != "application" || !Program.IsAdministrator() ||
            args[1].Length is < 1 or > 160 || args[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-') ||
            !int.TryParse(args[2], out var pid) || pid <= 0 || pid == Environment.ProcessId || !long.TryParse(args[3], out var ticks) || ticks <= 0 || !BootstrapEngine.HashText(args[4]))
            throw new InvalidDataException("Invalid administrator application handoff.");
        using var window = new BootstrapWindow(descriptor.Name + " · Administrator access");
        return window.Run(async token =>
        {
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != pid) throw new CryptographicException("The original native application coordinator changed.");
            // Authenticate the live server before opening any other user's token.
            using var original = OriginalSource.Open(pid, ticks, Environment.ProcessPath!);
            await pipe.WriteAsync(Convert.FromHexString(args[4]), deadline.Token); await pipe.FlushAsync(deadline.Token);
            string[] arguments = await ReadHandoffAsync(pipe, args[4], DescriptorDigest(), deadline.Token);
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token); await pipe.FlushAsync(deadline.Token);
            return await PrepareAndRunAsync(descriptor, original, arguments, window, token);
        });
    }

    // A caller already running as administrator still uses protected, verified
    // files, and its actual current token is the profile authority.
    internal static async Task<int> RunCurrentAsync(BootstrapDescriptor descriptor, string[] arguments, BootstrapWindow window, CancellationToken token)
    {
        using var self = Process.GetCurrentProcess();
        using var original = OriginalSource.Open(self.Id, self.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        return await PrepareAndRunAsync(descriptor, original, arguments, window, token);
    }

    private static async Task<int> PrepareAndRunAsync(BootstrapDescriptor descriptor, OriginalSource original, string[] arguments, BootstrapWindow window, CancellationToken token)
    {
        ValidateArguments(arguments);
        var machine = BootstrapEngine.SharedRoot("machine");
        using var http = new HttpClient(new OfflineHandler());
        var engine = new BootstrapEngine(descriptor, http, window);
        var sourceCandidates = await ApplicationPromotionPayloads.SourceCacheCandidatesAsync(descriptor, original.ApplicationRoot,
            new[] { Path.Combine(original.Local, "SocketJack", "Shared"), machine }, engine.PackageRoot, original.OpenRead, token);
        BootstrapMachineStore.Prepare(machine);
        string stagingParent = Path.Combine(machine, "staging"); BootstrapMachineStore.ValidateNamespace(machine, stagingParent); Directory.CreateDirectory(stagingParent);
        string staging = Path.Combine(stagingParent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var protectedPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in descriptor.Packages)
        {
            var destination = engine.PackageRoot(machine, package);
            BootstrapMachineStore.ValidateNamespace(machine, destination); Directory.CreateDirectory(destination);
            using var lease = await BootstrapEngine.AcquirePackageAsync(Path.Combine(destination, "provision.lock"), token);
            string archive = Path.Combine(destination, "package.hinst"), files = Path.Combine(destination, "files");
            if (File.Exists(archive) || Directory.Exists(files) || File.Exists(Path.Combine(destination, "complete.json")))
            {
                BootstrapMachineStore.ValidateTree(destination, requireProtectedDacl: false);
                if (!File.Exists(Path.Combine(destination, "complete.json")) || !await BootstrapEngine.FileMatchesAsync(archive, package.Size, package.Sha256, token))
                    throw new CryptographicException("A protected application runtime is incomplete or changed. Repair is required.");
                await engine.VerifyPackageAsync(archive, files, package, extract: false, token);
            }
            else
            {
                string work = Path.Combine(staging, "packages", package.Id); Directory.CreateDirectory(work);
                string copied = Path.Combine(work, "package.hinst");
                FileStream? sourceArchive = null; string? sourcePath = null;
                foreach (string candidate in sourceCandidates)
                {
                    var path = Path.Combine(engine.PackageRoot(candidate, package), "package.hinst"); FileStream? opened = null;
                    try
                    {
                        opened = original.OpenRead(path);
                        if (opened.Length == package.Size && Convert.ToHexString(await SHA256.HashDataAsync(opened, token)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                        { opened.Position = 0; sourceArchive = opened; sourcePath = path; opened = null; break; }
                    }
                    catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
                    finally { opened?.Dispose(); }
                }
                if (sourceArchive is null) throw new CryptographicException("The application's signed offline runtime archive is missing or changed. Run setup to repair it.");
                await using (sourceArchive)
                    await ApplicationPromotionPayloads.CopyVerifiedAsync(_ => sourceArchive, sourcePath!, copied, package.Size, package.Sha256, window, token);
                string extracted = Path.Combine(work, "files"); await engine.VerifyPackageAsync(copied, extracted, package, extract: true, token);
                BootstrapMachineStore.ValidateTree(work, requireProtectedDacl: false); original.EnsureAlive();
                Directory.Move(extracted, files); File.Move(copied, archive);
                await File.WriteAllTextAsync(Path.Combine(destination, "complete.json"), "{\"formatVersion\":1}", token);
            }
            protectedPackages.Add(package.Id, files);
        }
        string applicationNamespace = BootstrapEngine.UnderRoot(machine, "applications/" + descriptor.PublisherKeySha256[..16].ToLowerInvariant());
        BootstrapMachineStore.ValidateNamespace(machine, applicationNamespace); Directory.CreateDirectory(applicationNamespace);
        string application = Path.Combine(applicationNamespace, DescriptorDigest());
        using var applicationLease = await BootstrapEngine.AcquirePackageAsync(application + ".lock", token);
        BootstrapMachineStore.ValidateNamespace(machine, application);
        if (!Directory.Exists(application))
        {
            string prepared = Path.Combine(staging, "application");
            await ApplicationPromotionPayloads.StageApplicationAsync(descriptor, original.ApplicationRoot, prepared, protectedPackages, original.OpenRead, window, token);
            ApplicationPromotionPayloads.ValidateInventory(descriptor, prepared);
            BootstrapMachineStore.ValidateTree(prepared, requireProtectedDacl: false); original.EnsureAlive(); Directory.Move(prepared, application);
        }
        BootstrapMachineStore.ValidateTree(application, requireProtectedDacl: false);
        ApplicationPromotionPayloads.ValidateInventory(descriptor, application);
        // PrepareAsync performs a second verification of all protected packages,
        // local files and the regenerated probing configuration. It cannot fetch.
        var start = await engine.PrepareAsync(machine, staging, token, application);
        string temporary = Path.Combine(staging, "temp"); Directory.CreateDirectory(temporary);
        ApplicationPromotionPayloads.ConfigureEnvironment(start, temporary, original.Profile, original.Roaming, original.Local, original.ApplicationRoot);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.CreateNoWindow = true; original.EnsureAlive(); token.ThrowIfCancellationRequested();
        window.Report(new("Opening application", "Protected application files are ready."));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the protected application.");
        window.Hide(); await child.WaitForExitAsync(CancellationToken.None); return child.ExitCode;
    }

    private static string DescriptorDigest() => Convert.ToHexString(SHA256.HashData(WebBootstrapBindings.Read(Environment.ProcessPath!).DescriptorBytes)).ToLowerInvariant();
    private static void ValidateArguments(IReadOnlyList<string> args)
        => ApplicationPromotionPayloads.ValidateArguments(args);
    private static NamedPipeServerStream CreatePipe(string name, SecurityIdentifier user)
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(user);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }
    internal static Task WriteHandoffAsync(Stream output, string nonce, string digest, string[] arguments, CancellationToken token)
        => ApplicationPromotionPayloads.WriteHandoffAsync(output, nonce, digest, arguments, token);
    internal static Task<string[]> ReadHandoffAsync(Stream input, string nonce, string digest, CancellationToken token)
        => ApplicationPromotionPayloads.ReadHandoffAsync(input, nonce, digest, token);
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidDataException("Application startup cannot download missing files. Run setup to repair the installation.");
    }

    private sealed class OriginalSource : IDisposable
    {
        private readonly SafeProcessHandle process;
        private readonly SafeAccessTokenHandle token;
        internal string ApplicationRoot { get; }
        internal string Profile { get; }
        internal string Local { get; }
        internal string Roaming { get; }
        private OriginalSource(SafeProcessHandle process, SafeAccessTokenHandle token, string image)
        {
            this.process = process; this.token = token; ApplicationRoot = Path.GetDirectoryName(image)!;
            Profile = Folder("5E6C858F-0E22-4760-9AFE-EA3317B67173"); Local = Folder("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"); Roaming = Folder("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
        }
        internal static OriginalSource Open(int pid, long ticks, string expectedImage)
        {
            var process = OpenProcess(0x1000, false, pid); SafeAccessTokenHandle? token = null;
            try
            {
                var image = new StringBuilder(32768); int length = image.Capacity;
                if (process.IsInvalid || !QueryFullProcessImageName(process, 0, image, ref length) || !GetProcessTimes(process, out var created, out _, out _, out _) || DateTime.FromFileTimeUtc(created).Ticks != ticks ||
                    !Path.GetFullPath(image.ToString()).Equals(Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("The original native application identity changed.");
                if (!OpenProcessToken(process, 0x0e, out token))
                {
                    int error = Marshal.GetLastWin32Error(); token.Dispose();
                    if (error != 5) throw new Win32Exception(error);
                    using var privilege = DebugPrivilege.Enable();
                    if (!OpenProcessToken(process, 0x0e, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var result = new OriginalSource(process, token, image.ToString()); result.EnsureAlive();
                var originalBinding = WindowsIdentity.RunImpersonated(token, () => { BootstrapEngine.NoLinks(image.ToString()); return WebBootstrapBindings.Read(image.ToString()); });
                if (!Convert.ToHexString(SHA256.HashData(originalBinding.DescriptorBytes)).Equals(DescriptorDigest(), StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("The original signed application descriptor changed.");
                return result;
            }
            catch { token?.Dispose(); process.Dispose(); throw; }
        }
        internal FileStream OpenRead(string path) => WindowsIdentity.RunImpersonated(token, () =>
        {
            EnsureAlive(); BootstrapEngine.NoLinks(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        });
        internal void EnsureAlive()
        { if (!GetExitCodeProcess(process, out var exit) || exit != 259) throw new InvalidDataException("The original application coordinator exited."); }
        private string Folder(string id)
        {
            var guid = new Guid(id); int result = SHGetKnownFolderPath(in guid, 0x4000, token, out var text);
            try { if (result < 0) Marshal.ThrowExceptionForHR(result); return Path.GetFullPath(Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("The original user profile is unavailable.")); }
            finally { if (text != 0) Marshal.FreeCoTaskMem(text); }
        }
        public void Dispose() { token.Dispose(); process.Dispose(); }
    }
    private sealed class DebugPrivilege : IDisposable
    {
        private readonly SafeAccessTokenHandle token; private readonly TokenPrivileges previous;
        private DebugPrivilege(SafeAccessTokenHandle token, TokenPrivileges previous) { this.token = token; this.previous = previous; }
        internal static DebugPrivilege Enable()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var change = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, in change, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) || Marshal.GetLastWin32Error() == 1300)
                    throw new UnauthorizedAccessException("Windows did not permit reading the original application token.");
                return new(token, previous);
            }
            catch { token.Dispose(); throw; }
        }
        public void Dispose() { _ = AdjustTokenPrivileges(token, false, in previous, Marshal.SizeOf<TokenPrivileges>(), out _, out _); token.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, in TokenPrivileges changes, int length, out TokenPrivileges previous, out int required);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, SafeAccessTokenHandle token, out nint path);
}
