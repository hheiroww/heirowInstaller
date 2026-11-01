using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowInstaller.Publishing;

/// <summary>Protects the current Windows user's installer account and serializes token renewal across processes.</summary>
public sealed class RememberedPublisherAccount
{
    private const int MaximumProtectedBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Uri origin;
    private readonly string directory;
    private readonly string accountPath;
    private readonly string lockPath;
    private readonly byte[] binding;
    private readonly TimeProvider clock;
    private readonly bool allowLoopback;

    public RememberedPublisherAccount(Uri origin, string? directory = null, TimeProvider? clock = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            (origin.Scheme != Uri.UriSchemeHttps && !(allowLoopback && origin.IsLoopback && origin.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("Remembered sign-in requires an HTTPS publisher origin.", nameof(origin));
        this.origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
        this.directory = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "Account"));
        this.clock = clock ?? TimeProvider.System;
        this.allowLoopback = allowLoopback;
        binding = SHA256.HashData(Encoding.UTF8.GetBytes("heirowinstaller\n" + this.origin.AbsoluteUri));
        var name = "account-" + Convert.ToHexString(binding).ToLowerInvariant();
        accountPath = Path.Combine(this.directory, name + ".bin");
        lockPath = Path.Combine(this.directory, name + ".lock");
    }

    /// <summary>Only a foreground installer opening should renew; update checks read the existing lifetime.</summary>
    public async Task<PublisherSession?> RestoreAsync(bool renew = false, CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (state.Account is null) return null;
        if (state.Account.ExpiresUtc <= clock.GetUtcNow())
        {
            await WriteAsync(Empty(), cancellationToken).ConfigureAwait(false);
            return null;
        }
        var session = Session(state.Account);
        if (!renew) return session;
        PublisherSession renewed;
        try
        {
            renewed = await PublisherSignInClient.RenewAsync(origin, session, cancellationToken, allowLoopback).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            session.Dispose();
            await WriteAsync(Empty(), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // An offline opening cannot extend the server's expiry or destroy a still-valid cached account.
            if (session.ExpiresUtc > clock.GetUtcNow()) return session;
            session.Dispose();
            await WriteAsync(Empty(), cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch { session.Dispose(); throw; }
        try
        {
            Validate(renewed);
            var replacement = State(renewed);
            // Rotation has succeeded on the server. Finish its local commit even if the opening window closes now.
            await WriteAsync(replacement, CancellationToken.None).ConfigureAwait(false);
            return Session(replacement.Account!);
        }
        finally { renewed.Dispose(); session.Dispose(); }
    }

    /// <summary>Capture before browser approval and supply to RememberAsync to reject approval completed after sign-out.</summary>
    public async Task<string> GetRevisionAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(cancellationToken).ConfigureAwait(false)).Revision;
    }

    public async Task<bool> RememberAsync(PublisherSession session, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        Validate(session);
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        Validate(session);
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRevision is not null && expectedRevision != current.Revision) return false;
        await WriteAsync(State(session), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        // Keep a token-free revision so an older browser completion cannot recreate the signed-out account.
        await WriteAsync(Empty(), CancellationToken.None).ConfigureAwait(false);
        if (state.Account is null) return;
        using var session = Session(state.Account);
        try { await PublisherSignInClient.RevokeAsync(origin, session, cancellationToken, allowLoopback).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidDataException)
        { /* Local sign-out remains complete when the publisher cannot be reached. */ }
    }

    /// <summary>Discard only the failed credential; a later login must survive an older update request's rejection.</summary>
    public async Task InvalidateAsync(string expectedAccessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedAccessToken);
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (state.Account is not null && SameToken(state.Account.AccessToken, expectedAccessToken))
            await WriteAsync(Empty(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileStream> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Remembered sign-in requires Windows current-user protection.");
        PackageSafety.EnsureNoReparsePoints(directory); Directory.CreateDirectory(directory);
        PackageSafety.EnsureNoReparsePoints(lockPath);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
        }
    }

    private async Task<AccountState> ReadAsync(CancellationToken cancellationToken)
    {
        PackageSafety.EnsureNoReparsePoints(accountPath);
        if (File.Exists(accountPath))
        {
            try
            {
                await using var file = new FileStream(accountPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                if (file.Length is < 1 or > MaximumProtectedBytes) throw new InvalidDataException("Remembered account exceeds its protected size limit.");
                var encrypted = new byte[(int)file.Length];
                await file.ReadExactlyAsync(encrypted, cancellationToken).ConfigureAwait(false);
                var plain = RememberedAccountProtection.Transform(encrypted, binding, protect: false);
                try
                {
                    var state = JsonSerializer.Deserialize<AccountState>(plain, Json) ?? throw new InvalidDataException("Remembered account is empty.");
                    if (state.FormatVersion != 1 || state.Origin != origin.AbsoluteUri || state.Application != "heirowinstaller" || !Guid.TryParseExact(state.Revision, "N", out _))
                        throw new InvalidDataException("Remembered account identity is invalid.");
                    if (state.Account is not null)
                    {
                        using var session = Session(state.Account);
                        if (!ValidAccount(session)) throw new InvalidDataException("Remembered account contains invalid data.");
                    }
                    return state;
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception error) when (error is CryptographicException or JsonException or InvalidDataException)
            { /* Invalid protected state becomes a new token-free revision. */ }
        }
        var empty = Empty();
        await WriteAsync(empty, cancellationToken).ConfigureAwait(false);
        return empty;
    }

    private async Task WriteAsync(AccountState state, CancellationToken cancellationToken)
    {
        PackageSafety.EnsureNoReparsePoints(accountPath);
        var plain = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        byte[] encrypted;
        try { encrypted = RememberedAccountProtection.Transform(plain, binding, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = accountPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            PackageSafety.EnsureNoReparsePoints(accountPath);
            RememberedAccountProtection.Commit(temporary, accountPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Validate(PublisherSession session)
    {
        if (!ValidAccount(session) || session.ExpiresUtc <= clock.GetUtcNow())
            throw new InvalidDataException("The remembered publisher session is invalid or expired.");
    }
    private static bool ValidAccount(PublisherSession session) => !string.IsNullOrWhiteSpace(session.Username) && session.Username.Length <= 256 &&
        !string.IsNullOrWhiteSpace(session.AccessToken) && session.AccessToken.Length <= 8192 && session.AccessToken.All(character => character is >= (char)33 and <= (char)126);
    private AccountState Empty() => new() { Origin = origin.AbsoluteUri };
    private AccountState State(PublisherSession session) => new() { Origin = origin.AbsoluteUri, Account = new() { Username = session.Username, AccessToken = session.AccessToken,
        IsAdministrator = session.IsAdministrator, ExpiresUtc = session.ExpiresUtc < clock.GetUtcNow().AddDays(30) ? session.ExpiresUtc : clock.GetUtcNow().AddDays(30) } };
    private static PublisherSession Session(Account account) => new(account.Username, account.AccessToken, account.IsAdministrator, account.ExpiresUtc);
    private static bool SameToken(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left); var b = Encoding.UTF8.GetBytes(right);
        try { return CryptographicOperations.FixedTimeEquals(a, b); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }
    private sealed class AccountState
    {
        public int FormatVersion { get; set; } = 1;
        public string Origin { get; set; } = "";
        public string Application { get; set; } = "heirowinstaller";
        public string Revision { get; set; } = Guid.NewGuid().ToString("N");
        public Account? Account { get; set; }
    }
    private sealed class Account
    {
        public string Username { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public bool IsAdministrator { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }
}
