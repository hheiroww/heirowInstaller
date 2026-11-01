using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

[assembly: InternalsVisibleTo("HeirowInstaller.CodeSigning.Tests")]

namespace HeirowInstaller.Core;

/// <summary>Windows Authenticode configuration. It contains no passwords, access tokens, or private keys.</summary>
public sealed class WindowsCodeSigningOptions
{
    public string Provider { get; set; } = "none";
    public string? CertificateThumbprint { get; set; }
    public bool UseMachineCertificateStore { get; set; }
    public string? SignToolPath { get; set; }
    public string? ArtifactSigningDlibPath { get; set; }
    public string? Endpoint { get; set; }
    public string? AccountName { get; set; }
    public string? CertificateProfileName { get; set; }
    public string? TimestampUrl { get; set; }
}

public sealed class WindowsCodeSigningAvailability
{
    public string Provider { get; set; } = "none";
    public string? SignToolPath { get; set; }
    public string? ArtifactSigningDlibPath { get; set; }
    public string? CertificateSubject { get; set; }
    public string? CertificateThumbprint { get; set; }
}

public sealed class WindowsCodeSigningResult
{
    public string Provider { get; set; } = "";
    public bool Verified { get; set; }
    public bool TimestampVerified { get; set; }
    public string? CertificateSubject { get; set; }
    public string? CertificateThumbprint { get; set; }
}

/// <summary>Signs a temporary copy and publishes it only after Windows trust, timestamp, and package verification.</summary>
public static class WindowsCodeSigner
{
    public static void Validate(WindowsCodeSigningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = Provider(options);
        if (provider == "none")
        {
            if (options.UseMachineCertificateStore || new[] { options.CertificateThumbprint, options.SignToolPath, options.ArtifactSigningDlibPath, options.Endpoint, options.AccountName, options.CertificateProfileName, options.TimestampUrl }.Any(HasValue))
                throw new ArgumentException("Select certificate or microsoft when supplying Windows signing settings.");
            return;
        }
        ValidateToolPath(options.SignToolPath, "SignTool");
        ValidateTimestamp(options.TimestampUrl);
        if (provider == "certificate")
        {
            if (NormalizeThumbprint(options.CertificateThumbprint) is not { Length: 40 } thumbprint || !thumbprint.All(Uri.IsHexDigit))
                throw new ArgumentException("Select an installed code-signing certificate using its 40-character SHA-1 thumbprint.");
            if (new[] { options.Endpoint, options.AccountName, options.CertificateProfileName, options.ArtifactSigningDlibPath }.Any(HasValue))
                throw new ArgumentException("Installed-certificate signing cannot include Microsoft Artifact Signing settings.");
        }
        else
        {
            if (HasValue(options.CertificateThumbprint) || options.UseMachineCertificateStore)
                throw new ArgumentException("Microsoft Artifact Signing cannot select an installed certificate or certificate store.");
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || !endpoint.IsDefaultPort ||
                endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/" ||
                !endpoint.Host.EndsWith(".codesigning.azure.net", StringComparison.OrdinalIgnoreCase) || endpoint.Host[..^".codesigning.azure.net".Length].Length == 0)
                throw new ArgumentException("Use a Microsoft Artifact Signing public regional HTTPS endpoint, such as https://eus.codesigning.azure.net/.");
            ValidateName(options.AccountName, "Microsoft signing account");
            ValidateName(options.CertificateProfileName, "Microsoft certificate profile");
            ValidateToolPath(options.ArtifactSigningDlibPath, "Artifact Signing Dlib");
        }
    }

    /// <summary>Checks local tools and certificate metadata only. It never signs, contacts Azure, or starts account sign-in.</summary>
    public static WindowsCodeSigningAvailability CheckAvailability(WindowsCodeSigningOptions options)
    {
        options = Snapshot(options); Validate(options);
        var provider = Provider(options);
        if (provider == "none") return new();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Authenticode signing requires Windows.");
        var result = new WindowsCodeSigningAvailability { Provider = provider, SignToolPath = FindSignTool(options) };
        if (provider == "microsoft")
        {
            if (KnownUnsupportedMicrosoftSignTool(result.SignToolPath))
                throw new InvalidOperationException("Microsoft Artifact Signing requires current Windows 11 SDK signing tools (build 22621 or newer) or Artifact Signing Client Tools. The selected older SignTool is unsupported.");
            var architecture = PeArchitecture(result.SignToolPath) ?? (Environment.Is64BitProcess ? "x64" : "x86");
            result.ArtifactSigningDlibPath = FindArtifactSigningDlib(options.ArtifactSigningDlibPath, result.SignToolPath, architecture);
            if (PeArchitecture(result.ArtifactSigningDlibPath) is { } dlibArchitecture && dlibArchitecture != architecture)
                throw new InvalidOperationException("SignTool and Azure.CodeSigning.Dlib.dll must have the same processor architecture. Select the matching tools from the Artifact Signing Client Tools installation.");
        }
        else
        {
            using var certificate = FindCertificate(options);
            result.CertificateSubject = certificate.Subject;
            result.CertificateThumbprint = certificate.Thumbprint;
        }
        return result;
    }

    public static async Task<WindowsCodeSigningResult> SignAsync(string filePath, WindowsCodeSigningOptions options,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options = Snapshot(options); Validate(options);
        if (Provider(options) == "none") throw new ArgumentException("Select certificate or microsoft to sign a Windows file.");
        var availability = CheckAvailability(options);
        return await SignCoreAsync(filePath, options, availability, RunSignToolAsync, progress, cancellationToken);
    }

    internal static async Task<WindowsCodeSigningResult> SignCoreAsync(string filePath, WindowsCodeSigningOptions options,
        WindowsCodeSigningAvailability availability, Func<string, IReadOnlyList<string>, CancellationToken, Task> run,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default,
        Func<string, X509Certificate2>? signerCertificateReader = null)
    {
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath)) throw new FileNotFoundException("The Windows file to sign does not exist.", filePath);
        if (Path.GetExtension(filePath).ToLowerInvariant() is not (".exe" or ".dll" or ".msi"))
            throw new ArgumentException("Windows signing supports EXE, DLL, and MSI files.");
        PackageSafety.EnsureNoReparsePoints(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        await using var sourceGuard = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[]? packageHash = await VerifiedPackageHashAsync(filePath, cancellationToken);
        var temporary = Path.Combine(Path.GetDirectoryName(filePath)!, "." + Path.GetFileNameWithoutExtension(filePath) + "." + Guid.NewGuid().ToString("N") + ".signing" + Path.GetExtension(filePath));
        var metadata = Path.Combine(Path.GetTempPath(), "heirowInstaller-signing-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await sourceGuard.CopyToAsync(output, cancellationToken);
            if (Provider(options) == "microsoft")
                await File.WriteAllTextAsync(metadata, CreateMicrosoftMetadata(options), cancellationToken);
            progress?.Report(new("windows-signing", "Signing the completed Windows file", 0, 1));
            await run(availability.SignToolPath!, CreateSignArguments(temporary, options, availability, metadata), cancellationToken);
            progress?.Report(new("windows-signing", "Verifying Windows publisher trust and timestamp", 0, 1));
            await run(availability.SignToolPath!, ["verify", "/pa", "/all", "/tw", temporary], cancellationToken);
            // SignTool /tw makes an absent timestamp a warning; all nonzero exit codes are rejected.
            using var signer = (signerCertificateReader ?? ReadSignerCertificate)(temporary);
            RequireCodeSigningCertificate(signer);
            if (Provider(options) == "certificate" && !string.Equals(signer.Thumbprint, NormalizeThumbprint(options.CertificateThumbprint), StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("The Windows signature does not match the selected code-signing certificate.");
            var finalPackageHash = await VerifiedPackageHashAsync(temporary, cancellationToken);
            if ((packageHash is null) != (finalPackageHash is null) ||
                (packageHash is not null && !CryptographicOperations.FixedTimeEquals(packageHash, finalPackageHash!)))
                throw new CryptographicException("Windows signing changed or removed the embedded application package.");
            var result = new WindowsCodeSigningResult
            {
                Provider = Provider(options), Verified = true, TimestampVerified = true,
                CertificateSubject = signer.Subject, CertificateThumbprint = signer.Thumbprint
            };
            cancellationToken.ThrowIfCancellationRequested();
            PackageSafety.EnsureNoReparsePoints(filePath);
            await sourceGuard.DisposeAsync();
            File.Move(temporary, filePath, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(metadata)) File.Delete(metadata);
        }
    }

    internal static IReadOnlyList<string> CreateSignArguments(string path, WindowsCodeSigningOptions options, WindowsCodeSigningAvailability availability, string metadataPath)
    {
        var arguments = new List<string> { "sign", "/fd", "SHA256", "/tr", options.TimestampUrl ?? (Provider(options) == "microsoft" ? "http://timestamp.acs.microsoft.com" : "https://timestamp.digicert.com"), "/td", "SHA256" };
        if (Provider(options) == "certificate")
        {
            arguments.AddRange(["/sha1", NormalizeThumbprint(options.CertificateThumbprint)!, "/s", "My"]);
            if (options.UseMachineCertificateStore) arguments.Add("/sm");
        }
        else arguments.AddRange(["/dlib", availability.ArtifactSigningDlibPath!, "/dmdf", metadataPath]);
        arguments.Add(path);
        return arguments;
    }

    internal static string CreateMicrosoftMetadata(WindowsCodeSigningOptions options) => JsonSerializer.Serialize(new
    {
        Endpoint = new Uri(options.Endpoint!).GetLeftPart(UriPartial.Authority),
        CodeSigningAccountName = options.AccountName,
        CertificateProfileName = options.CertificateProfileName,
        ExcludeCredentials = new[] { "InteractiveBrowserCredential" }
    });

    internal static async Task RunSignToolAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new IOException("Windows signing tool did not start."); }
        catch (Win32Exception) { throw new IOException("Windows signing tool could not start. Check the configured executable and its required runtimes."); }
        // Discard diagnostics in fixed-size buffers: child tools can print ambient credentials or an unbounded response.
        static async Task Drain(StreamReader reader, CancellationToken token)
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
        }
        var drains = Task.WhenAll(Drain(process.StandardOutput, deadline.Token), Drain(process.StandardError, deadline.Token));
        try { await process.WaitForExitAsync(deadline.Token); await drains; }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await drains; } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Windows signing or trust verification exceeded its five-minute deadline.");
        }
        if (process.ExitCode != 0)
            throw new IOException(arguments[0] == "verify"
                ? "Windows publisher or timestamp verification failed. The original file was preserved."
                : "Windows signing failed. Check certificate access, timestamp service, or Microsoft account permissions and ambient Azure credentials. The original file was preserved.");
    }

    private static async Task<byte[]?> VerifiedPackageHashAsync(string file, CancellationToken token)
    {
        if (!PackageReader.HasEmbeddedPackage(file)) return null;
        using (var reader = PackageReader.Open(file)) await reader.VerifyAsync(cancellationToken: token);
        using var package = BundleBuilder.OpenPackageStream(file);
        return await SHA256.HashDataAsync(package, token);
    }

    private static X509Certificate2 FindCertificate(WindowsCodeSigningOptions options)
    {
        using var store = new X509Store(StoreName.My, options.UseMachineCertificateStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var thumbprint = NormalizeThumbprint(options.CertificateThumbprint);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint!, validOnly: false);
        try
        {
            var certificate = matches.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey);
            if (certificate is null) throw new InvalidOperationException("The selected code-signing certificate with an accessible private key is not installed in the selected My certificate store. Import the CA-issued certificate or connect its hardware token.");
            RequireCodeSigningCertificate(certificate);
            return new X509Certificate2(certificate);
        }
        finally { foreach (var certificate in matches) certificate.Dispose(); }
    }

    private static X509Certificate2 ReadSignerCertificate(string file)
    {
#pragma warning disable SYSLIB0057 // X509CertificateLoader cannot read a signer's certificate embedded in an Authenticode file.
        using var certificate = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
        return new X509Certificate2(certificate);
    }
    internal static void RequireCodeSigningCertificate(X509Certificate2 certificate)
    {
        if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("The code-signing certificate is not currently valid.");
        if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            throw new InvalidOperationException("A self-signed certificate cannot establish a publicly verified Windows publisher. Select a CA-issued code-signing certificate.");
        if (!certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(x => x.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3")))
            throw new InvalidOperationException("The certificate is not issued for code signing.");
    }

    private static string FindSignTool(WindowsCodeSigningOptions options)
    {
        if (HasValue(options.SignToolPath)) return RequireFile(options.SignToolPath!, "The configured SignTool executable is missing.");
        var architecture = Environment.Is64BitProcess ? "x64" : "x86";
        var microsoft = Provider(options) == "microsoft";
        if (microsoft)
        {
            if (HasValue(options.ArtifactSigningDlibPath))
            {
                var sibling = Path.Combine(Path.GetDirectoryName(options.ArtifactSigningDlibPath!)!, "signtool.exe");
                if (File.Exists(sibling) && !KnownUnsupportedMicrosoftSignTool(sibling)) return sibling;
            }
            foreach (var directory in ArtifactToolDirectories(architecture))
            {
                var candidate = Path.Combine(directory, "signtool.exe");
                if (File.Exists(candidate) && !KnownUnsupportedMicrosoftSignTool(candidate)) return candidate;
            }
            if (FindSdkSignTool(architecture, microsoft: true) is { } sdkTool) return sdkTool;
        }
        foreach (var path in PathCandidates("signtool.exe"))
            if (File.Exists(path) && (!microsoft || !KnownUnsupportedMicrosoftSignTool(path))) return path;
        if (!microsoft && FindSdkSignTool(architecture, microsoft: false) is { } fallback) return fallback;
        throw new FileNotFoundException(microsoft
            ? "Compatible SignTool was not found. Install current Windows 11 SDK signing tools (build 22621 or newer) or Microsoft Artifact Signing Client Tools, or configure a compatible signtool.exe path."
            : "SignTool was not found. Install the Windows SDK Signing Tools or configure the full signtool.exe path.");
    }

    private static string? FindSdkSignTool(string architecture, bool microsoft)
    {
        var candidates = new List<(Version Version, string Path)>();
        foreach (var programFiles in ProgramFilesDirectories())
        {
            var root = Path.Combine(programFiles, "Windows Kits", "10", "bin");
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(directory, architecture, "signtool.exe");
                if (File.Exists(path) && (!microsoft || !KnownUnsupportedMicrosoftSignTool(path)))
                    candidates.Add((Version.TryParse(Path.GetFileName(directory), out var version) ? version : new Version(0, 0), path));
            }
            var legacy = Path.Combine(root, architecture, "signtool.exe");
            if (File.Exists(legacy) && (!microsoft || !KnownUnsupportedMicrosoftSignTool(legacy))) candidates.Add((new Version(0, 0), legacy));
        }
        return candidates.OrderByDescending(x => x.Version).Select(x => x.Path).FirstOrDefault();
    }

    private static bool KnownUnsupportedMicrosoftSignTool(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        // Reject known older SDK binaries, including build 20348. Unknown version metadata
        // remains subject to the actual SignTool signing and trust-verification checks.
        return version.FileMajorPart == 10 && version.FileMinorPart == 0 && version.FileBuildPart < 22621;
    }

    private static string FindArtifactSigningDlib(string? explicitPath, string signTool, string architecture)
    {
        if (HasValue(explicitPath)) return RequireFile(explicitPath!, "The configured Azure.CodeSigning.Dlib.dll is missing.");
        var sibling = Path.Combine(Path.GetDirectoryName(signTool)!, "Azure.CodeSigning.Dlib.dll");
        if (File.Exists(sibling)) return sibling;
        foreach (var directory in ArtifactToolDirectories(architecture))
        {
            var candidate = Path.Combine(directory, "Azure.CodeSigning.Dlib.dll");
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var path in PathCandidates("Azure.CodeSigning.Dlib.dll")) if (File.Exists(path)) return path;
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var name in new[] { "microsoft.artifactsigning.client", "microsoft.trusted.signing.client" })
        {
            var root = Path.Combine(nuget, name);
            if (!Directory.Exists(root)) continue;
            foreach (var version in Directory.EnumerateDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(version, "bin", architecture, "Azure.CodeSigning.Dlib.dll");
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException("Microsoft Artifact Signing Client Tools were not found. Install Microsoft.Azure.ArtifactSigningClientTools or configure the full Azure.CodeSigning.Dlib.dll path matching SignTool's architecture.");
    }

    private static IEnumerable<string> ArtifactToolDirectories(string architecture)
    {
        foreach (var programFiles in ProgramFilesDirectories())
            foreach (var name in new[] { Path.Combine("Microsoft", "ArtifactSigningClientTools"), "ArtifactSigningClientTools", "Microsoft Artifact Signing Client Tools", "Microsoft Artifact Signing", "Microsoft Trusted Signing Client Tools" })
                foreach (var relative in new[] { Path.Combine("bin", architecture), "bin", architecture, "" })
                    yield return Path.Combine(programFiles, name, relative);
    }
    private static string? PeArchitecture(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 64) return null;
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt16() != 0x5a4d) return null;
        file.Position = 0x3c; var offset = reader.ReadInt32();
        if (offset < 64 || offset > file.Length - 6) return null;
        file.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) return null;
        return reader.ReadUInt16() switch { 0x8664 => "x64", 0x014c => "x86", 0xaa64 => "arm64", _ => null };
    }

    private static IEnumerable<string> ProgramFilesDirectories() => new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => x.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase);
    private static IEnumerable<string> PathCandidates(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(x => x.Trim().Trim('"')).Where(Path.IsPathFullyQualified).Select(x => Path.Combine(x, name));
    private static string RequireFile(string path, string message)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException(message); return path;
    }
    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string Provider(WindowsCodeSigningOptions options) => options.Provider?.Trim().ToLowerInvariant() switch
    {
        "none" => "none", "certificate" => "certificate", "microsoft" => "microsoft",
        _ => throw new ArgumentException("Windows signing provider must be none, certificate, or microsoft.")
    };
    private static string? NormalizeThumbprint(string? value) => value is null ? null : string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    private static void ValidateToolPath(string? value, string name)
    {
        if (HasValue(value) && (!Path.IsPathFullyQualified(value!) || value!.Any(char.IsControl))) throw new ArgumentException(name + " must be a full local file path.");
    }
    private static void ValidateName(string? value, string name)
    {
        if (value is null || value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException(name + " must contain 1–128 letters, digits, hyphens, or underscores and begin with a letter or digit.");
    }
    private static void ValidateTimestamp(string? value)
    {
        if (value is null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback || uri.Host.Length == 0)
            throw new ArgumentException("The RFC 3161 timestamp URL must be an HTTP or HTTPS service URL without credentials, query, or fragment.");
    }
    private static WindowsCodeSigningOptions Snapshot(WindowsCodeSigningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new() { Provider = options.Provider, CertificateThumbprint = options.CertificateThumbprint, UseMachineCertificateStore = options.UseMachineCertificateStore,
            SignToolPath = options.SignToolPath, ArtifactSigningDlibPath = options.ArtifactSigningDlibPath, Endpoint = options.Endpoint,
            AccountName = options.AccountName, CertificateProfileName = options.CertificateProfileName, TimestampUrl = options.TimestampUrl };
    }
}
