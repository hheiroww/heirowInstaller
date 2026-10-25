using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

/// <summary>Publishing command implementation, kept independent from the CLI argument parser.</summary>
internal static class PublishingCommands
{
    internal static async Task<PublishedRelease> PublishAsync(string packagePath, Uri publisherOrigin,
        bool githubOidc, bool waitForActivation, InstallOptions validation, bool allowLoopback,
        string? bearerToken = null, string? sessionCookie = null, string? resumeUploadId = null,
        CancellationToken cancellationToken = default, bool sealedDelivery = false)
    {
        if (githubOidc && (bearerToken is not null || sessionCookie is not null))
            throw new ArgumentException("--github-oidc cannot be combined with stored publisher credentials.");
        if (sealedDelivery && (githubOidc || sessionCookie is not null))
            throw new ArgumentException("--sealed-delivery requires the native administrator bearer session and cannot use GitHub OIDC or browser cookies.");
        if (!githubOidc && resumeUploadId is not null)
            throw new ArgumentException("--resume-upload requires --github-oidc.");
        string productId, version;
        if (Path.GetExtension(packagePath).Equals(".hupd", StringComparison.OrdinalIgnoreCase))
        {
            using var update = IncrementalUpdateReader.Open(packagePath);
            update.VerifySignature(validation.TrustedPublisherKeySha256, requireSignature: true);
            await update.VerifyAsync(cancellationToken: cancellationToken);
            var transition = update.Transition;
            productId = transition.ProductId;
            version = transition.ToVersion;
        }
        else using (var package = PackageReader.Open(packagePath))
        {
            // OIDC identifies the publisher; package signatures still authenticate the delivered code.
            var protectedDraft = githubOidc && package.Manifest.Modules.TryGetValue("SocketJackSecurityLoader", out _);
            var hostedSigningDraft = false;
            if (protectedDraft)
                hostedSigningDraft = SecurityLoaderManifest.Validate(package.Manifest.Modules["SocketJackSecurityLoader"],
                    package.Manifest.Payload.ToDictionary(x => x.Path, x => x.Sha256, StringComparer.OrdinalIgnoreCase)).HostedSigning;
            // An unsigned protected draft is an input to an explicitly registered hosted signer;
            // only its sealed, publisher-signed result is eligible for client installation.
            package.VerifySignature(validation.TrustedPublisherKeySha256, validation.RequireSignature || sealedDelivery || (githubOidc && !hostedSigningDraft));
            await package.VerifyAsync(cancellationToken: cancellationToken);
            productId = package.Manifest.Id;
            version = package.Manifest.Version;
        }
        using var publisher = new PublisherClient(publisherOrigin, bearerToken, sessionCookie, allowLoopback);
        if (githubOidc) return await publisher.PublishFromGitHubActionsAsync(packagePath, productId, version,
            new() { WaitForActivation = waitForActivation, UploadId = resumeUploadId }, cancellationToken);
        var receipt = sealedDelivery ? await publisher.PublishSealedDeliveryAsync(packagePath, productId, version, cancellationToken)
            : await publisher.PublishAsync(packagePath, cancellationToken);
        return waitForActivation ? await publisher.WaitForActivationAsync(receipt, cancellationToken) : receipt;
    }

    internal static string? ReadCredentialFromEnvironment(string? name)
    {
        if (name is null) return null;
        return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : throw new ArgumentException("The configured publisher credential environment variable is empty.");
    }
}
