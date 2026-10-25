using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

/// <summary>Publishing command implementation, kept independent from the CLI argument parser.</summary>
internal static class PublishingCommands
{
    internal static async Task<PublishedRelease> PublishAsync(string packagePath, Uri publisherOrigin,
        bool githubOidc, bool waitForActivation, InstallOptions validation, bool allowLoopback,
        string? bearerToken = null, string? sessionCookie = null, string? resumeUploadId = null,
        CancellationToken cancellationToken = default, bool sealedDelivery = false, bool protectedPreparation = false)
    {
        if (githubOidc && (bearerToken is not null || sessionCookie is not null))
            throw new ArgumentException("--github-oidc cannot be combined with stored publisher credentials.");
        if (sealedDelivery && (githubOidc || sessionCookie is not null))
            throw new ArgumentException("--sealed-delivery requires the native administrator bearer session and cannot use GitHub OIDC or browser cookies.");
        if (protectedPreparation && (sealedDelivery || githubOidc || sessionCookie is not null))
            throw new ArgumentException("--protected-preparation is for native administrator bearer publishing; use --github-oidc separately for CI preparation.");
        if (protectedPreparation && !Path.GetExtension(packagePath).Equals(".hinst", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Native protected preparation requires a .hinst source draft.");
        if (!githubOidc && resumeUploadId is not null)
            throw new ArgumentException("--resume-upload requires --github-oidc.");
        string productId, version;
        if (Path.GetExtension(packagePath).Equals(".hupd", StringComparison.OrdinalIgnoreCase))
        {
            if (protectedPreparation || sealedDelivery)
                throw new ArgumentException("Protected preparation and sealed delivery require a full .hinst or standalone .exe; publish signed .hupd transitions through ordinary publishing.");
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
            var protectedDraft = (githubOidc || protectedPreparation) && package.Manifest.Modules.TryGetValue("SocketJackSecurityLoader", out _);
            var hostedSigningDraft = false;
            if (protectedDraft)
                hostedSigningDraft = SecurityLoaderManifest.Validate(package.Manifest.Modules["SocketJackSecurityLoader"],
                    package.Manifest.Payload.ToDictionary(x => x.Path, x => x.Sha256, StringComparer.OrdinalIgnoreCase)).HostedSigning;
            if (protectedPreparation && !hostedSigningDraft) throw new ArgumentException("Native preparation requires a protected hosted-signing source draft.");
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
            : protectedPreparation ? await publisher.PublishProtectedPreparationAsync(packagePath, cancellationToken)
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
