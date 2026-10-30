namespace HeirowInstaller.Core;

/// <summary>A newer release exists, but the publisher has not supplied a signed file transition for this installation.</summary>
public sealed class MissingIncrementalUpdateException : InvalidOperationException
{
    public string ProductId { get; }
    public string CurrentVersion { get; }
    public string OfferedVersion { get; }

    public MissingIncrementalUpdateException(string productId, string currentVersion, string offeredVersion)
        : base($"Version {DisplayVersion(offeredVersion)} is published, but no matching signed file update is available for your installed version {DisplayVersion(currentVersion)}. " +
            "The publisher must provide this update before it can be installed here. Your installed application is unchanged. " +
            "Use the publisher's download page to install the newer release, or retry after the publisher fixes the update.")
    {
        ProductId = productId;
        CurrentVersion = DisplayVersion(currentVersion);
        OfferedVersion = DisplayVersion(offeredVersion);
    }

    private static string DisplayVersion(string value)
    {
        // Build metadata is not part of version ordering and must not echo arbitrary feed text into a diagnostic.
        var version = value.Split('+', 2)[0];
        return version.Length is > 0 and <= 128 && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? version : "(unavailable)";
    }
}
