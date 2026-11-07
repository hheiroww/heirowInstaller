using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HeirowInstaller.BootstrapContract;

namespace HeirowInstaller.Core;

/// <summary>Elevates the small native bootstrap, which provisions its managed worker only into protected machine storage.</summary>
public static class BootstrapWorkerPromotion
{
    public const string LauncherVariable = "SOCKETJACK_BOOTSTRAP_LAUNCHER";
    public const string PromoterIdVariable = "SOCKETJACK_BOOTSTRAP_PROMOTER_PID";
    public const string PromoterStartVariable = "SOCKETJACK_BOOTSTRAP_PROMOTER_START";

    internal static string MaintenanceLauncher(string image, string? publisherPin)
    {
        var native = Environment.GetEnvironmentVariable(LauncherVariable);
        if (native is null) return image;
        if (publisherPin is null || !OperatingSystem.IsWindows())
            throw new CryptographicException("Native maintenance requires a verified publisher.");
        var self = ReadIdentity(Environment.ProcessId);
        if (!PackageSafety.HashEquals(PackageSafety.HashFile(image), PackageSafety.HashFile(self.Image)) ||
            !BootstrapProcessTrust.IsTrustedUpdaterOwner(self.Id, self.StartTicks,
                new UpdateCompanionConfiguration { PublisherKeySha256 = publisherPin }, native))
            throw new CryptographicException("The maintenance host differs from its signed native coordinator.");
        var parent = ReadIdentity(self.ParentId);
        if (!Path.GetFullPath(native).Equals(Path.GetFullPath(parent.Image), InstallerPlatform.PathComparison))
            throw new CryptographicException("The native maintenance coordinator changed.");
        SharedInstallerHost.RequireMaintenanceHost(native);
        return native;
    }

    public static bool TryStart(string pipeName, int originalGuiPid, long originalGuiStart, string nonce, string? expectedPublisherPin,
        out BootstrapWorkerLaunch? launch)
    {
        launch = null;
        var native = Environment.GetEnvironmentVariable(LauncherVariable);
        if (native is null) return false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native worker elevation requires Windows.");
        if (originalGuiPid != Environment.ProcessId || originalGuiStart <= 0 || nonce.Length != 64 || !nonce.All(Uri.IsHexDigit) ||
            pipeName.Length is < 1 or > 160 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            throw new InvalidDataException("Invalid elevated worker request binding.");
        using var self = Process.GetCurrentProcess();
        if (self.StartTime.ToUniversalTime().Ticks != originalGuiStart ||
            !int.TryParse(Environment.GetEnvironmentVariable(BootstrapProcessTrust.ParentIdVariable), out var nativeId) ||
            !long.TryParse(Environment.GetEnvironmentVariable(BootstrapProcessTrust.ParentStartVariable), out var nativeStart) || Parent(self) != nativeId)
            throw new CryptographicException("The native setup coordinator identity changed.");
        using var coordinator = Process.GetProcessById(nativeId);
        RequireProcess(coordinator, nativeStart, native);
        var binding = WebBootstrapBindings.Read(native);
        using var descriptor = JsonDocument.Parse(binding.DescriptorBytes);
        RequireDescriptor(descriptor.RootElement, expectedPublisherPin);
        var start = new ProcessStartInfo(native) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var value in new[] { "--bootstrap-worker", pipeName, originalGuiPid.ToString(CultureInfo.InvariantCulture), originalGuiStart.ToString(CultureInfo.InvariantCulture), nonce }) start.ArgumentList.Add(value);
        var child = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the protected installation host.");
        launch = new(child, binding, descriptor.RootElement.Clone()); return true;
    }

    /// <summary>The worker independently verifies its native promoter and the original medium-integrity GUI.</summary>
    public static bool ValidatePromotedWorkerParent(int originalGuiPid, long originalGuiStart, string ownImage, string? expectedPublisherPin = null)
    {
        var native = Environment.GetEnvironmentVariable(LauncherVariable);
        var idText = Environment.GetEnvironmentVariable(PromoterIdVariable);
        if (native is null || idText is null) return false;
        if (!OperatingSystem.IsWindows() || !int.TryParse(idText, out var id) ||
            !long.TryParse(Environment.GetEnvironmentVariable(PromoterStartVariable), out var ticks)) throw new CryptographicException("Invalid protected worker coordinator.");
        using var self = Process.GetCurrentProcess();
        if (Parent(self) != id) throw new CryptographicException("The worker was not started by its native promoter.");
        using var promoter = Process.GetProcessById(id); RequireProcess(promoter, ticks, native);
        var binding = WebBootstrapBindings.Read(native);
        using var descriptor = JsonDocument.Parse(binding.DescriptorBytes); RequireDescriptor(descriptor.RootElement, expectedPublisherPin);
        var expected = ProtectedHost(descriptor.RootElement, binding.PublisherKeySha256);
        if (!Path.GetFullPath(ownImage).Equals(expected.Path, InstallerPlatform.PathComparison) ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(ownImage), expected.Hash)) throw new CryptographicException("The worker differs from the protected signed host.");
        var original = ReadIdentity(originalGuiPid);
        if (original.StartTicks != originalGuiStart || !PackageSafety.HashEquals(PackageSafety.HashFile(original.Image), expected.Hash))
            throw new CryptographicException("The original setup window identity changed.");
        return true;
    }

    /// <summary>Authorizes data independently of the unprivileged GUI's managed assemblies.</summary>
    public static bool ValidatePromotedPackage(string packagePath, string productId, string version, string? publisherPin, string scope, string operation = "install")
    {
        var native = Environment.GetEnvironmentVariable(LauncherVariable);
        if (native is null || Environment.GetEnvironmentVariable(PromoterIdVariable) is null) return false;
        var binding = WebBootstrapBindings.Read(native);
        using var document = JsonDocument.Parse(binding.DescriptorBytes); var descriptor = document.RootElement;
        RequireDescriptor(descriptor, publisherPin);
        if (scope is not ("user" or "machine") || operation is not ("install" or "repair" or "uninstall" or "files") || publisherPin is null || !PackageSafety.HashEquals(publisherPin, binding.PublisherKeySha256))
            throw new CryptographicException("The privileged request has a different native publisher or operation binding.");
        using var package = PackageReader.Open(packagePath);
        package.VerifySignature(binding.PublisherKeySha256, requireSignature: true);
        if (package.Manifest.Id != productId || package.Manifest.Version != version) throw new CryptographicException("The privileged package identity changed.");
        if (descriptor.TryGetProperty("applicationPackage", out var application) && application.ValueKind != JsonValueKind.Null)
        {
            if (descriptor.GetProperty("productId").GetString() != productId) throw new CryptographicException("The native setup cannot authorize maintenance for another application.");
            if (operation == "install" && (descriptor.GetProperty("version").GetString() != version || package.IsMetadataOnly ||
                new FileInfo(packagePath).Length != application.GetProperty("size").GetInt64() || !PackageSafety.HashEquals(PackageSafety.HashFile(packagePath), application.GetProperty("sha256").GetString()!)))
                throw new CryptographicException("Initial setup bytes differ from the signed native product binding.");
            // Repair, removal and later file updates also validate their signed installed
            // receipts, current scope and owned roots in the engine before mutation.
        }
        return true;
    }

    private static void RequireDescriptor(JsonElement descriptor, string? targetPublisher)
    {
        if (descriptor.GetProperty("hostKind").GetString() != "installer" || !descriptor.GetProperty("hostEntryPoint").GetString()!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only an installer bootstrap with an exact native apphost can promote an installation worker.");
        if (descriptor.TryGetProperty("applicationPackage", out var application) && application.ValueKind != JsonValueKind.Null &&
            targetPublisher is not null && !PackageSafety.HashEquals(descriptor.GetProperty("publisherKeySha256").GetString()!, targetPublisher))
            throw new CryptographicException("The setup publisher differs from its native product binding.");
    }

    internal static (string Path, string Hash) ProtectedHost(JsonElement descriptor, string publisher)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var id = descriptor.GetProperty("hostPackageId").GetString()!;
        var package = descriptor.GetProperty("packages").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var digest = package.GetProperty("sha256").GetString()!;
        if (!PackageSafety.HashPattern().IsMatch(publisher) || !PackageSafety.HashPattern().IsMatch(digest)) throw new InvalidDataException("Invalid protected host binding.");
        var shared = SharedPackages.Root("machine", InstallerEngine.DefaultStateRoot("machine"));
        var directory = PackageSafety.UnderRoot(shared, "packages/" + publisher[..16].ToLowerInvariant() + "/" + digest.ToLowerInvariant());
        MachineStateSecurity.ValidateProtectedTreeUnderRoot(shared, directory);
        var archive = PackageSafety.UnderRoot(directory, "package.hinst");
        using var reader = PackageReader.Open(archive);
        reader.VerifySignature(publisher, requireSignature: true);
        if (new FileInfo(archive).Length != package.GetProperty("size").GetInt64() || !PackageSafety.HashEquals(reader.ComputePackageSha256(), digest) ||
            reader.Manifest.Id != id || reader.Manifest.Version != package.GetProperty("version").GetString() || reader.Manifest.TargetRuntime != package.GetProperty("targetRuntime").GetString())
            throw new CryptographicException("The protected host archive differs from the native descriptor.");
        var entry = descriptor.GetProperty("hostEntryPoint").GetString()!;
        var payload = reader.Manifest.Payload.Single(x => x.Path.Equals(entry, StringComparison.OrdinalIgnoreCase));
        var image = PackageSafety.UnderRoot(PackageSafety.UnderRoot(directory, "files"), payload.Path);
        if (new FileInfo(image).Length != payload.Size || !PackageSafety.HashEquals(PackageSafety.HashFile(image), payload.Sha256)) throw new CryptographicException("The protected host executable changed.");
        return (image, payload.Sha256);
    }
    internal static int Parent(Process process)
    {
        return ReadIdentity(process.Id).ParentId;
    }
    private static void RequireProcess(Process process, long ticks, string image)
    {
        var identity = ReadIdentity(process.Id);
        if (identity.StartTicks != ticks)
            throw new CryptographicException("The native coordinator start-time identity changed.");
        if (!Path.GetFullPath(identity.Image).Equals(Path.GetFullPath(image), InstallerPlatform.PathComparison))
            throw new CryptographicException("The native coordinator executable identity changed.");
        PackageSafety.EnsureNoReparsePoints(identity.Image);
    }
    internal sealed record KernelIdentity(int Id, long StartTicks, string Image, int ParentId);
    internal static KernelIdentity ReadIdentity(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION works across credentialed elevation.
        if (process.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var image = new StringBuilder(32768); var length = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref length) || !GetProcessTimes(process, out var created, out _, out _, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ImageName = "" };
        if (Process32First(snapshot, ref entry))
            do { if (entry.ProcessId == processId) return new(processId, DateTime.FromFileTimeUtc(created).Ticks, image.ToString(), checked((int)entry.ParentId)); }
            while (Process32Next(snapshot, ref entry));
        throw new InvalidOperationException("The process exited during identity inspection.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public nuint Heap; public uint ModuleId, Threads, ParentId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ImageName;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry value);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry value);
}

public sealed class BootstrapWorkerLaunch : IDisposable
{
    private readonly WebBootstrapBinding binding;
    private readonly JsonElement descriptor;
    private (string Path, string Hash)? protectedHost;
    internal BootstrapWorkerLaunch(Process process, WebBootstrapBinding binding, JsonElement descriptor) { Process = process; this.binding = binding; this.descriptor = descriptor; }
    public Process Process { get; }
    private (string Path, string Hash) Host => protectedHost ??= BootstrapWorkerPromotion.ProtectedHost(descriptor, binding.PublisherKeySha256);
    public string ExpectedWorkerImage => Host.Path;
    public string ExpectedWorkerSha256 => Host.Hash;
    public void ValidatePeer(int processId)
    {
        var peer = BootstrapWorkerPromotion.ReadIdentity(processId);
        var expected = Host;
        if (peer.ParentId != Process.Id || !Path.GetFullPath(peer.Image).Equals(expected.Path, InstallerPlatform.PathComparison) ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(peer.Image), expected.Hash))
            throw new CryptographicException("The protected worker pipe peer changed.");
    }
    public void Dispose() => Process.Dispose();
}
