using System.Net.Http.Headers;
using System.Text.Json;

namespace HeirowInstaller.Publishing;

/// <summary>Requests a short-lived GitHub identity; no stored publishing secret is required.</summary>
public static class GitHubActionsOidcClient
{
    public const string Audience = "https://socketjack.com/installer-publisher";

    public static async Task<string> RequestTokenAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var requestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.Host.EndsWith(".actions.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(requestToken))
            throw new InvalidOperationException("GitHub Actions OIDC requires a GitHub-hosted token endpoint and id-token: write permission.");
        var builder = new UriBuilder(uri) { Query = uri.Query.TrimStart('?') + (uri.Query.Length > 1 ? "&" : "") + "audience=" + Uri.EscapeDataString(Audience) };
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub OIDC token request failed. HTTP {(int)response.StatusCode}.");
            var bytes = await PublisherClient.ReadBoundedAsync(response, cancellationToken);
            using var json = JsonDocument.Parse(bytes);
            var token = json.RootElement.GetProperty("value").GetString();
            if (!PublisherClient.IsCredential(token)) throw new InvalidDataException("GitHub returned an invalid OIDC credential.");
            return token!;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("GitHub OIDC token request could not reach its configured endpoint."); }
        catch (JsonException) { throw new InvalidDataException("GitHub returned an invalid OIDC response."); }
    }
}
