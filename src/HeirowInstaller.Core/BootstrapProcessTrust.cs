using System.Text.Json;
using HeirowInstaller.BootstrapContract;

namespace HeirowInstaller.Core;

/// <summary>Recognizes the verified native updater bootstrap while its downloaded host runs as a direct child.</summary>
public static class BootstrapProcessTrust
{
    public const string ParentIdVariable = "SOCKETJACK_BOOTSTRAP_PID";
    public const string ParentStartVariable = "SOCKETJACK_BOOTSTRAP_START";

    /// <summary>Reads the presentation name from the signed, live setup coordinator for these exact package arguments.</summary>
    public static string? ReadBoundSetupName(string productId, string version, string packageSha256, string publisherPin,
        string? remoteUrl, long remoteSize, int? primaryDesktopPid = null)
    {
        if (!OperatingSystem.IsWindows() ||
            !PackageSafety.HashPattern().IsMatch(publisherPin ?? "") || !PackageSafety.HashPattern().IsMatch(packageSha256 ?? "") ||
            !int.TryParse(Environment.GetEnvironmentVariable(ParentIdVariable), out var parentId) || parentId <= 0 ||
            !long.TryParse(Environment.GetEnvironmentVariable(ParentStartVariable), out var parentStart) || parentStart <= 0 ||
            Environment.GetEnvironmentVariable(BootstrapWorkerPromotion.LauncherVariable) is not { } launcher ||
            !Path.IsPathFullyQualified(launcher)) return null;
        try
        {
            var desktop = BootstrapWorkerPromotion.ReadIdentity(Environment.ProcessId);
            if (primaryDesktopPid is { } primary)
            {
                if (desktop.ParentId != primary) return null;
                desktop = BootstrapWorkerPromotion.ReadIdentity(primary);
            }
            if (desktop.ParentId != parentId) return null;
            var coordinator = BootstrapWorkerPromotion.ReadIdentity(parentId);
            if (coordinator.StartTicks != parentStart ||
                !Path.GetFullPath(coordinator.Image).Equals(Path.GetFullPath(launcher), InstallerPlatform.PathComparison)) return null;
            PackageSafety.EnsureNoReparsePoints(coordinator.Image);
            var binding = WebBootstrapBindings.Read(coordinator.Image, publisherPin);
            using var document = JsonDocument.Parse(binding.DescriptorBytes);
            var descriptor = document.RootElement;
            if (descriptor.GetProperty("hostKind").GetString() != "installer" ||
                !string.Equals(descriptor.GetProperty("productId").GetString(), productId, StringComparison.OrdinalIgnoreCase) ||
                descriptor.GetProperty("version").GetString() != version ||
                !descriptor.TryGetProperty("applicationPackage", out var package) || package.ValueKind != JsonValueKind.Object ||
                package.GetProperty("sha256").GetString() is not { } digest || !PackageSafety.HashEquals(digest, packageSha256!) ||
                remoteUrl is not null && (package.GetProperty("url").GetString() != remoteUrl || package.GetProperty("size").GetInt64() != remoteSize))
                return null;
            var name = descriptor.GetProperty("name").GetString();
            return string.IsNullOrWhiteSpace(name) || name.Length > 160 || name.Any(char.IsControl) ? null : name;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or InvalidDataException or JsonException or KeyNotFoundException or FormatException)
        { return null; }
    }

    public static bool IsTrustedUpdaterProcess(UpdateCompanionConfiguration configuration, string verifiedRuntime)
    {
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFullPath(current).Equals(Path.GetFullPath(verifiedRuntime), InstallerPlatform.PathComparison)) return true;
        if (!OperatingSystem.IsWindows() || configuration.SharedRuntime is not { } binding ||
            !int.TryParse(Environment.GetEnvironmentVariable(ParentIdVariable), out var parentId) || parentId <= 0 || parentId == Environment.ProcessId ||
            !long.TryParse(Environment.GetEnvironmentVariable(ParentStartVariable), out var start) || start <= 0) return false;
        try
        {
            if (BootstrapWorkerPromotion.ReadIdentity(Environment.ProcessId).ParentId != parentId) return false;
            var parent = BootstrapWorkerPromotion.ReadIdentity(parentId);
            var image = parent.Image;
            if (parent.StartTicks != start || !Path.GetFullPath(image).Equals(Path.GetFullPath(verifiedRuntime), InstallerPlatform.PathComparison)) return false;
            PackageSafety.EnsureNoReparsePoints(image);
            return new FileInfo(image).Length == binding.Size && PackageSafety.HashEquals(PackageSafety.HashFile(image), binding.Sha256);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return false; }
    }

    /// <summary>Checks an owner image independently of its command-line PID. Callers also require the live owner-pipe kernel binding.</summary>
    public static bool IsTrustedUpdaterOwner(int processId, long startTicks, UpdateCompanionConfiguration configuration, string verifiedRuntime)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 || startTicks <= 0) return false;
        try
        {
            var owner = BootstrapWorkerPromotion.ReadIdentity(processId);
            if (owner.StartTicks != startTicks) return false;
            if (Path.GetFullPath(owner.Image).Equals(Path.GetFullPath(verifiedRuntime), InstallerPlatform.PathComparison))
                return configuration.SharedRuntime is { } content && new FileInfo(owner.Image).Length == content.Size &&
                    PackageSafety.HashEquals(PackageSafety.HashFile(owner.Image), content.Sha256);
            var parent = BootstrapWorkerPromotion.ReadIdentity(owner.ParentId);
            var binding = WebBootstrapBindings.Read(parent.Image, configuration.PublisherKeySha256);
            using var parsed = JsonDocument.Parse(binding.DescriptorBytes); var descriptor = parsed.RootElement;
            var role = descriptor.GetProperty("hostKind").GetString();
            if (role is not ("updater" or "installer")) return false;
            if (role == "updater" && (!Path.GetFullPath(parent.Image).Equals(Path.GetFullPath(verifiedRuntime), InstallerPlatform.PathComparison) ||
                configuration.SharedRuntime is not { } expected || new FileInfo(parent.Image).Length != expected.Size ||
                !PackageSafety.HashEquals(PackageSafety.HashFile(parent.Image), expected.Sha256))) return false;
            var hostId = descriptor.GetProperty("hostPackageId").GetString();
            var definition = descriptor.GetProperty("packages").EnumerateArray().Single(x => x.GetProperty("id").GetString() == hostId);
            var digest = definition.GetProperty("sha256").GetString()!;
            if (!PackageSafety.HashPattern().IsMatch(digest)) return false;
            var files = Path.GetDirectoryName(Path.GetFullPath(owner.Image))!;
            var directory = Path.GetDirectoryName(files)!;
            if (!Path.GetFileName(files).Equals("files", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).Equals(digest, StringComparison.OrdinalIgnoreCase)) return false;
            var archive = PackageSafety.UnderRoot(directory, "package.hinst"); PackageSafety.EnsureNoReparsePoints(archive);
            using var package = PackageReader.Open(archive); package.VerifySignature(configuration.PublisherKeySha256, requireSignature: true);
            if (package.Manifest.Id != hostId || package.Manifest.Version != definition.GetProperty("version").GetString() ||
                package.Manifest.TargetRuntime != definition.GetProperty("targetRuntime").GetString() || new FileInfo(archive).Length != definition.GetProperty("size").GetInt64() ||
                !PackageSafety.HashEquals(package.ComputePackageSha256(), digest)) return false;
            var relative = PackageSafety.NormalizeRelativePath(Path.GetRelativePath(files, owner.Image));
            if (relative is not ("heirowUpdater.exe" or "heirowInstaller.exe")) return false;
            var payload = package.Manifest.Payload.SingleOrDefault(x => x.Path == relative);
            return payload is not null && new FileInfo(owner.Image).Length == payload.Size && PackageSafety.HashEquals(PackageSafety.HashFile(owner.Image), payload.Sha256);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or InvalidDataException or JsonException or KeyNotFoundException or FormatException)
        { return false; }
    }

}
