using System.Net.Http.Headers;
using System.Text.Json;

namespace HeirowInstaller.Publishing;

public static partial class PublisherSignInClient
{
    /// <summary>Renews the installer account's server-enforced remembered session when the desktop opens.</summary>
    public static async Task<PublisherSession> RenewAsync(Uri origin, PublisherSession session,
        CancellationToken cancellationToken = default, bool allowLoopback = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = deadline.Token;
        using var client = SessionClient(origin, session, allowLoopback, out var authority);
        using var request = SessionRequest(authority, "renew");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RequireSuccess(response, "renewal");
        if (response.Content.Headers.ContentLength is > 65536) throw new InvalidDataException("SocketJack account response exceeds the limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > 65536) throw new InvalidDataException("SocketJack account response exceeds the limit.");
            body.Write(buffer, 0, count);
        }
        Renewal result;
        try { result = JsonSerializer.Deserialize<Renewal>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("SocketJack returned an invalid account response."); }
        if (!result.Authenticated || string.IsNullOrWhiteSpace(result.Username) || result.Username.Length > 256 ||
            !ValidToken(result.AccessToken) || result.ExpiresUtc <= DateTimeOffset.UtcNow ||
            !string.Equals(result.Username, session.Username, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SocketJack returned an invalid account renewal.");
        return new(result.Username, result.AccessToken, result.IsAdministrator, result.ExpiresUtc);
    }

    /// <summary>Revokes this account session without accessing or signing out the user's browser.</summary>
    public static async Task RevokeAsync(Uri origin, PublisherSession session,
        CancellationToken cancellationToken = default, bool allowLoopback = false)
    {
        using var client = SessionClient(origin, session, allowLoopback, out var authority);
        using var request = SessionRequest(authority, "signout");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RequireSuccess(response, "sign-out");
    }

    private static HttpClient SessionClient(Uri origin, PublisherSession session, bool allowLoopback, out Uri authority)
    {
        ArgumentNullException.ThrowIfNull(origin); ArgumentNullException.ThrowIfNull(session);
        if (!origin.IsAbsoluteUri || origin.UserInfo.Length > 0 ||
            (origin.Scheme != "https" && !(allowLoopback && origin.IsLoopback && origin.Scheme == "http")))
            throw new ArgumentException("SocketJack account sessions require HTTPS without credentials in the URL.", nameof(origin));
        if (!ValidToken(session.AccessToken)) throw new ArgumentException("A usable SocketJack account session is required.", nameof(session));
        authority = new(origin.GetLeftPart(UriPartial.Authority) + "/");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client;
    }

    private static HttpRequestMessage SessionRequest(Uri origin, string action) => new(HttpMethod.Post, new Uri(origin, "api/app-signin/" + action))
    {
        Content = new StringContent("{\"application\":\"heirowinstaller\"}", System.Text.Encoding.UTF8, "application/json")
    };

    private static bool ValidToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 8192 && value.All(character => character >= 33 && character <= 126);
    private static void RequireSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"SocketJack account {operation} was not accepted. HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private sealed class Renewal
    {
        public bool Authenticated { get; set; }
        public string Username { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public bool IsAdministrator { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }
}
