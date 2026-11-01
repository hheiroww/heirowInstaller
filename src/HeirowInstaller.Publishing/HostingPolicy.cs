using System.Net;
using System.Text.Json;

namespace HeirowInstaller.Publishing;

public sealed class InstallerHostingPolicy
{
    public bool WindowsAuthenticodeRequired { get; set; }
    public bool CodeSigningProvided { get; set; }
}

public sealed partial class PublisherClient
{
    /// <summary>Hosting requirements only; this never grants access to a code-signing identity.</summary>
    public async Task<InstallerHostingPolicy> GetHostingPolicyAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(new Uri(origin, "api/installer/hosting-policy"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        // Older hosting servers had no Windows-signature gate. The server remains authoritative.
        if (response.StatusCode == HttpStatusCode.NotFound) return new();
        await EnsureSuccessAsync(response, deadline.Token);
        return JsonSerializer.Deserialize<InstallerHostingPolicy>(await ReadBoundedAsync(response, deadline.Token), Json)
            ?? throw new InvalidDataException("The hosting policy is missing.");
    }
}
