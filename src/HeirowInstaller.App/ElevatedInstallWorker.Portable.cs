#if !WINDOWS
using HeirowInstaller.Core;
using System.Security.Cryptography;

namespace HeirowInstaller.App;

internal static class ElevatedInstallWorker
{
    internal sealed record Observation(int ProcessId, long StartTicks, bool Isolated, string ImageSha256, bool Completed);
    internal static Observation? LastObservation => null;
    internal static bool IsWorker(string[] args) => args.FirstOrDefault() == "--install-worker";
    internal static Task<int> RunAsync(string[] args) => throw Unavailable();
    internal static Task<InstallResult> InstallAsync(string packagePath, InstallPlan plan, InstallOptions options,
        Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>> prompt,
        IProgress<InstallProgress>? progress, string? isolatedFixtureRoot, CancellationToken token, string operation = "install") => throw Unavailable();
    internal static Task<IReadOnlyList<string>> VerifyProtocolRejectionsAsync(string root, string packagePath, InstallPlan plan, InstallOptions options, int alternateParentPid) => throw Unavailable();
    internal static void VerifyUnsignedHostPackage(string productId, string packageSha256, PackageReader package)
    {
        if (package.Manifest.Id != productId || package.PublisherKeySha256 is not null || !PackageSafety.HashEquals(package.ComputePackageSha256(), packageSha256))
            throw new CryptographicException("An unsigned bundled setup can only apply its own unchanged package.");
    }
    private static Exception Unavailable() => new PlatformNotSupportedException("For machine-scope installation on Linux or macOS, run the CLI from a terminal with sudo.");
}
#endif
