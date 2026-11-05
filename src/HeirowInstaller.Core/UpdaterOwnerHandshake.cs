using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>One-shot kernel-bound proof that the handoff's original process is its actual initiating coordinator.</summary>
public static class UpdaterOwnerHandshake
{
    public sealed record Binding(string ProductId, string InstallDirectory, string StateRoot, string Scope, string TargetManifestSha256, string StageIdentity);
    private sealed record Hello(string Nonce, Binding Binding);
    public sealed class AuthenticatedOwner : IDisposable
    {
        private readonly NamedPipeClientStream pipe;
        private readonly Hello hello;
        public InstallerUserContext User { get; }
        private AuthenticatedOwner(NamedPipeClientStream pipe, Hello hello, InstallerUserContext user) { this.pipe = pipe; this.hello = hello; User = user; }
        public async Task AcceptAsync(CancellationToken token = default) { await WriteAsync(pipe, hello, token); pipe.Dispose(); }
        public void Dispose() { pipe.Dispose(); if (OperatingSystem.IsWindows()) User.Dispose(); }
        internal static AuthenticatedOwner Create(NamedPipeClientStream pipe, string nonce, Binding binding, InstallerUserContext user) => new(pipe, new(nonce, binding), user);
    }
    private static readonly ConcurrentDictionary<string, Task> pending = new(StringComparer.Ordinal);

    public static void Attach(ProcessStartInfo start, int ownerPid, long ownerStart, Binding binding, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var current = Process.GetCurrentProcess();
        if (ownerPid <= 0 || ownerStart <= 0) throw new InvalidDataException("Invalid wait-only update dependency.");
        Validate(binding);
        if (pending.Count >= 64) throw new InvalidOperationException("Too many update owner sessions are pending.");
        var name = "heirowUpdater.owner." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidDataException("The handoff owner has no Windows user.");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        start.ArgumentList.Add("--owner-pipe"); start.ArgumentList.Add(name);
        start.ArgumentList.Add("--owner-nonce"); start.ArgumentList.Add(nonce);
        // A caller may wait for a separate application. Only this actual server process supplies user authority.
        start.ArgumentList.Add("--owner-pid"); start.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--owner-start"); start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var task = ServeAsync(pipe, new(nonce, binding), token);
        if (!pending.TryAdd(name, task)) { pipe.Dispose(); throw new InvalidOperationException("Duplicate handoff owner session."); }
        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = ExpireAsync(name, task);
    }

    /// <summary>Keep the original coordinator alive until its child has captured the verified user token.</summary>
    public static async Task WaitForAcceptanceAsync(ProcessStartInfo start, CancellationToken token = default)
    {
        var index = start.ArgumentList.IndexOf("--owner-pipe");
        if (index < 0) return;
        var name = start.ArgumentList[index + 1];
        if (!pending.TryGetValue(name, out var task)) throw new InvalidDataException("This handoff has no active owner session.");
        try { await task.WaitAsync(token); }
        finally { if (task.IsCompleted) pending.TryRemove(name, out _); }
    }

    /// <summary>Authenticates the actual pipe server, then captures its kernel user token before the ready acknowledgement.</summary>
    public static async Task<AuthenticatedOwner?> AuthenticateAsync(string? name, string? nonce, int ownerPid, long ownerStart,
        Binding expected, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        Validate(expected);
        if (name is null || !name.StartsWith("heirowUpdater.owner.", StringComparison.Ordinal) || name.Length != 68 ||
            !name[20..].All(Uri.IsHexDigit) || nonce is null || nonce.Length != 64 || !nonce.All(Uri.IsHexDigit) || ownerPid <= 0 || ownerPid == Environment.ProcessId || ownerStart <= 0)
            throw new InvalidDataException("File-update helpers require their initiating process's one-shot owner proof.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        InstallerUserContext? user = null;
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != ownerPid)
                throw new InvalidDataException("The update owner proof was served by a different process.");
            var hello = await ReadAsync<Hello>(pipe, timeout.Token);
            if (!FixedEquals(hello.Nonce, nonce) || hello.Binding != expected) throw new CryptographicException("The update owner approved a different target or stage.");
            user = InstallerUserContext.OpenForProcess(ownerPid, ownerStart);
            return AuthenticatedOwner.Create(pipe, nonce, expected, user);
        }
        catch { pipe.Dispose(); if (OperatingSystem.IsWindows()) user?.Dispose(); throw; }
    }

    private static async Task ExpireAsync(string name, Task task)
    { try { await task; } catch { } await Task.Delay(TimeSpan.FromMinutes(2)); pending.TryRemove(name, out _); }

    private static async Task ServeAsync(NamedPipeServerStream pipe, Hello expected, CancellationToken token)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            await pipe.WaitForConnectionAsync(timeout.Token);
            await WriteAsync(pipe, expected, timeout.Token);
            var accepted = await ReadAsync<Hello>(pipe, timeout.Token);
            if (!FixedEquals(accepted.Nonce, expected.Nonce) || accepted.Binding != expected.Binding)
                throw new CryptographicException("The update helper accepted a different owner binding.");
        }
    }
    private static void Validate(Binding value)
    {
        PackageSafety.ValidateId(value.ProductId);
        if (value.Scope is not ("user" or "machine") || !Path.IsPathFullyQualified(value.InstallDirectory) || !Path.IsPathFullyQualified(value.StateRoot) ||
            value.TargetManifestSha256.Length != 64 || !value.TargetManifestSha256.All(Uri.IsHexDigit) || value.StageIdentity.Length is < 1 or > 32768)
            throw new InvalidDataException("Invalid update owner binding.");
    }
    private static bool FixedEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, InstallerJson.Options);
        if (bytes.Length is < 1 or > 64 * 1024) throw new InvalidDataException("Update owner proof exceeds its bound.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var length = BitConverter.ToInt32(header);
        if (length is < 1 or > 64 * 1024) throw new InvalidDataException("Update owner proof exceeds its bound.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, InstallerJson.Options) ?? throw new InvalidDataException("Empty update owner proof.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int pid);
}
