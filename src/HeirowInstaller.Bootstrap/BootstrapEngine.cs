using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HeirowInstaller.Bootstrap;

internal sealed class BootstrapEngine
{
    private const long MaximumExpandedBytes = 16L * 1024 * 1024 * 1024;
    private readonly BootstrapDescriptor descriptor;
    private readonly HttpClient http;
    private readonly IProgress<BootstrapProgress> progress;
    internal BootstrapEngine(BootstrapDescriptor descriptor, HttpClient http, IProgress<BootstrapProgress> progress)
    { Validate(descriptor); this.descriptor = descriptor; this.http = http; this.progress = progress; }

    internal void AppendStartupArguments(ProcessStartInfo start, IEnumerable<string> arguments)
    {
        // Scope remains a setup choice. Its verified installation plan requests
        // approval when Install is selected; explicit signed requirements can
        // prepare the administrator worker before downloading the application.
        if (descriptor.HostKind == "installer" && descriptor.RequiresAdministrator) start.ArgumentList.Add("--request-admin-on-open");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    internal static BootstrapDescriptor ReadEmbeddedDescriptor()
    {
        var binding = HeirowInstaller.BootstrapContract.WebBootstrapBindings.Read(Environment.ProcessPath!);
        var descriptor = JsonSerializer.Deserialize(binding.DescriptorBytes, BootstrapJson.Default.BootstrapDescriptor) ?? throw new InvalidDataException("Empty bootstrap binding.");
        if (!binding.PublisherKeySha256.Equals(descriptor.PublisherKeySha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("The bootstrap publisher differs from its signed descriptor.");
        return descriptor;
    }

    internal static void Validate(BootstrapDescriptor value)
    {
        if (value.FormatVersion != 1 || !Identifier(value.ProductId) || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 160 ||
            !Identifier(value.Version) || value.Scope is not ("user" or "machine") || value.HostKind is not ("installer" or "updater" or "application") || !HashText(value.PublisherKeySha256) || value.Packages.Count is < 1 or > 128)
            throw new InvalidDataException("Invalid bootstrap product binding.");
        if (value.AllowedDownloadOrigins.Count > 16 || value.AllowedDownloadOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.GetLeftPart(UriPartial.Authority) != origin))
            throw new InvalidDataException("Bootstrap download origins must be explicit HTTPS origins.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in value.Packages)
        {
            ValidateDownload(package);
            if (!Identifier(package.Id) || !Identifier(package.Version) || !Identifier(package.TargetRuntime) || !ids.Add(package.Id)) throw new InvalidDataException("Invalid or duplicate shared runtime identity.");
            if (package.TargetRuntime != System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier) throw new PlatformNotSupportedException("This installer targets a different platform or architecture.");
        }
        if (value.HostKind != "application" && !ids.Contains(value.HostPackageId) || value.DotNetPackageId is { } dotnet && !ids.Contains(dotnet)) throw new InvalidDataException("The selected host is absent from the shared runtime packages.");
        Relative(value.HostEntryPoint);
        if (value.DotNetPackageId is not null) Relative(value.DotNetEntryPoint ?? "dotnet.exe");
        if (value.HostArguments.Count > 128 || value.HostArguments.Any(argument => argument.Length > 4096 || argument.Contains('\0'))) throw new InvalidDataException("Invalid installer arguments.");
        if (value.ApplicationPackage is { } app)
        {
            if (value.HostKind != "installer" || value.OfflineOnly) throw new InvalidDataException("Only an online setup launcher may download an application package.");
            ValidateDownload(app);
        }
        if (value.HostKind == "application")
        {
            if (value.DotNetPackageId is null || value.ApplicationPackage is not null || value.RuntimeConfigPath is null ||
                !value.RuntimeConfigPath.EndsWith(".runtimeconfig.json", StringComparison.Ordinal) || value.ApplicationFiles.Count is < 2 or > 100000 ||
                value.ProbingPackageIds.Count > 128 || value.ProbingPackageIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.ProbingPackageIds.Count ||
                value.ProbingPackageIds.Any(id => !ids.Contains(id))) throw new InvalidDataException("Invalid offline application launch binding.");
            Relative(value.RuntimeConfigPath);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in value.ApplicationFiles)
            {
                Relative(file.Path);
                if (!paths.Add(file.Path) || !HashText(file.Sha256) || file.Size < 0 || file.Size > MaximumExpandedBytes - total) throw new InvalidDataException("Invalid local application file binding.");
                total += file.Size;
            }
            if (!paths.Contains(value.HostEntryPoint) || !paths.Contains(value.RuntimeConfigPath))
                throw new InvalidDataException("The actual apphost and runtime configuration must be signed local files.");
            if (paths.Contains(value.RuntimeConfigPath[..^5] + ".dev.json")) throw new InvalidDataException("Application payload cannot replace its generated shared-library binding.");
        }
    }

    internal static string SharedRoot(string scope) => scope == "machine"
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SocketJack", "Shared")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "Shared");

    internal string PackageRoot(string sharedRoot, BootstrapPackage package) => UnderRoot(sharedRoot,
        $"packages/{descriptor.PublisherKeySha256.ToLowerInvariant()[..16]}/{package.Sha256.ToLowerInvariant()}");

    internal async Task<ProcessStartInfo> PrepareAsync(string sharedRoot, string stagingRoot, CancellationToken token, string? applicationDirectory = null, bool allowProvision = true)
    {
        NoLinks(sharedRoot); NoLinks(stagingRoot);
        allowProvision = allowProvision && descriptor.HostKind == "installer" && !descriptor.OfflineOnly;
        if (allowProvision) Directory.CreateDirectory(stagingRoot);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in descriptor.Packages)
        {
            token.ThrowIfCancellationRequested();
            var root = PackageRoot(sharedRoot, package);
            var archivePath = Path.Combine(root, "package.hinst");
            var complete = Path.Combine(root, "complete.json");
            if (File.Exists(complete) && File.Exists(archivePath) && await FileMatchesAsync(archivePath, package.Size, package.Sha256, token))
            {
                progress.Report(new("Checking shared files", package.Id));
                await VerifyPackageAsync(archivePath, Path.Combine(root, "files"), package, extract: false, token);
            }
            else
            {
                if (!allowProvision) throw new InvalidDataException("Shared application or maintenance files are missing or changed. Run setup to repair the installation before opening it.");
                NoLinks(root); Directory.CreateDirectory(root);
                using var lease = await AcquirePackageAsync(Path.Combine(root, "provision.lock"), token);
                if (File.Exists(archivePath))
                {
                    if (!await FileMatchesAsync(archivePath, package.Size, package.Sha256, token)) throw new CryptographicException("An immutable shared runtime archive changed; it was preserved for repair.");
                    await VerifyPackageAsync(archivePath, Path.Combine(root, "files"), package, extract: false, token);
                }
                else
                {
                    if (File.Exists(complete) || Directory.Exists(Path.Combine(root, "files"))) throw new InvalidDataException("A shared runtime directory is incomplete. Run repair before reusing it.");
                    var work = UnderRoot(stagingRoot, package.Id.ToLowerInvariant());
                    Directory.CreateDirectory(work);
                    var download = Path.Combine(work, "package.hinst");
                    await DownloadAsync(package, download, token);
                    var files = Path.Combine(work, "files");
                    await VerifyPackageAsync(download, files, package, extract: true, token);
                    // Publish verified files under the per-package lease. Active versions are never overwritten.
                    Directory.Move(files, Path.Combine(root, "files"));
                    File.Move(download, archivePath);
                }
                if (!File.Exists(complete)) await File.WriteAllTextAsync(complete, "{\"formatVersion\":1}", token);
            }
            roots.Add(package.Id, Path.Combine(root, "files"));
        }
        string? application = null;
        if (descriptor.ApplicationPackage is { } payload && !descriptor.ApplicationPackageRequiresAuthentication)
        {
            application = Path.Combine(stagingRoot, "application.hinst");
            await DownloadAsync(payload, application, token);
        }
        var localApplication = descriptor.HostKind == "application";
        var hostRoot = localApplication ? Path.GetFullPath(applicationDirectory ?? Path.GetDirectoryName(Environment.ProcessPath!)!) : roots[descriptor.HostPackageId];
        if (localApplication) await VerifyLocalApplicationAsync(hostRoot, roots, token);
        var host = UnderRoot(hostRoot, descriptor.HostEntryPoint);
        if (!File.Exists(host)) throw new InvalidDataException("The verified installer host is missing.");
        var start = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = hostRoot };
        if (descriptor.DotNetPackageId is { } runtime)
        {
            var runtimeRoot = roots[runtime];
            var managedDll = !localApplication && Path.GetExtension(host).Equals(".dll", StringComparison.OrdinalIgnoreCase);
            start.FileName = managedDll ? UnderRoot(runtimeRoot, descriptor.DotNetEntryPoint ?? "dotnet.exe") : host;
            start.Environment["DOTNET_ROOT"] = runtimeRoot;
            start.Environment["DOTNET_ROOT_X64"] = runtimeRoot;
            start.Environment["DOTNET_ROOT_ARM64"] = runtimeRoot;
            if (managedDll)
            {
                start.ArgumentList.Add("exec");
                foreach (var root in roots.Values) { start.ArgumentList.Add("--additionalprobingpath"); start.ArgumentList.Add(root); }
                start.ArgumentList.Add(host);
            }
        }
        else start.FileName = host;
        foreach (var argument in descriptor.HostArguments) start.ArgumentList.Add(argument);
        if (descriptor.ApplicationPackage is { } app)
        {
            if (application is not null) start.ArgumentList.Add(application);
            else
            {
                start.ArgumentList.Add("--remote-package"); start.ArgumentList.Add(app.Url);
                start.ArgumentList.Add("--remote-package-size"); start.ArgumentList.Add(app.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            start.ArgumentList.Add("--require-signature");
            start.ArgumentList.Add("--trust-key"); start.ArgumentList.Add(descriptor.PublisherKeySha256);
            start.ArgumentList.Add("--expected-product-id"); start.ArgumentList.Add(descriptor.ProductId);
            start.ArgumentList.Add("--expected-version"); start.ArgumentList.Add(descriptor.Version);
            start.ArgumentList.Add("--expected-sha256"); start.ArgumentList.Add(app.Sha256);
        }
        if (descriptor.HostKind == "installer") { start.ArgumentList.Add("--scope"); start.ArgumentList.Add(descriptor.Scope); }
        return start;
    }

    // Scope is a cache-location hint, never authority to select other bytes. Each
    // candidate still verifies every signed archive/payload and the application's
    // generated probing paths before any process can start. No launch-time downloads.
    internal async Task<ProcessStartInfo> PrepareOfflineAsync(IEnumerable<string> sharedRoots, string stagingRoot,
        CancellationToken token, string? applicationDirectory = null)
    {
        Exception? last = null;
        foreach (var root in sharedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { return await PrepareAsync(root, stagingRoot, token, applicationDirectory, allowProvision: false); }
            catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException)
            { last = error; }
        }
        throw new InvalidDataException("The installed shared files are unavailable. Run setup to repair this installation.", last);
    }

    private async Task VerifyLocalApplicationAsync(string root, IReadOnlyDictionary<string, string> packages, CancellationToken token)
    {
        foreach (var file in descriptor.ApplicationFiles)
        {
            var path = UnderRoot(root, file.Path);
            if (!File.Exists(path) || !await FileMatchesAsync(path, file.Size, file.Sha256, token))
                throw new InvalidDataException("An application file is missing or changed. Run setup to repair this installation.");
        }
        var probing = UnderRoot(root, descriptor.RuntimeConfigPath![..^5] + ".dev.json");
        NoLinks(probing);
        if (!File.Exists(probing) || new FileInfo(probing).Length > 128 * 1024)
            throw new InvalidDataException("The application's shared-library binding is missing. Run setup to repair it.");
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(probing, token));
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 1 ||
            !document.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object ||
            options.EnumerateObject().Count() != 1 || !options.TryGetProperty("additionalProbingPaths", out var paths) || paths.ValueKind != JsonValueKind.Array ||
            paths.GetArrayLength() != descriptor.ProbingPackageIds.Count)
            throw new InvalidDataException("The application's runtime probing configuration differs from its signed dependency binding.");
        for (var index = 0; index < descriptor.ProbingPackageIds.Count; index++)
            if (paths[index].ValueKind != JsonValueKind.String || !string.Equals(paths[index].GetString(), packages[descriptor.ProbingPackageIds[index]], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The application's runtime probing configuration selects an unverified directory.");
    }

    internal async Task DownloadAsync(BootstrapDownload artifact, string path, CancellationToken token)
    {
        if (File.Exists(path))
        {
            if (await FileMatchesAsync(path, artifact.Size, artifact.Sha256, token)) return;
            throw new InvalidDataException("An existing staged download has different bytes; it was retained for review.");
        }
        var current = new Uri(artifact.Url);
        for (var redirects = 0; ; redirects++)
        {
            var original = new Uri(artifact.Url);
            if (current.Scheme != "https" || current.UserInfo.Length != 0 || current.Fragment.Length != 0 ||
                current.GetLeftPart(UriPartial.Authority) != original.GetLeftPart(UriPartial.Authority) && !descriptor.AllowedDownloadOrigins.Contains(current.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("A download redirected outside its approved HTTPS origins.");
            using var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 5 || response.Headers.Location is null) throw new InvalidDataException("Invalid download redirect.");
                current = new Uri(current, response.Headers.Location); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != artifact.Size) throw new InvalidDataException("Download length differs from the product binding.");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            if (drive.AvailableFreeSpace < artifact.Size + 16 * 1024 * 1024) throw new IOException("There is insufficient disk space for this download.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); NoLinks(path);
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072]; long completed = 0; int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                if (read > artifact.Size - completed) throw new InvalidDataException("Download exceeds its bound size.");
                hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token); completed += read;
                progress.Report(new("Downloading", Path.GetFileName(original.LocalPath), completed, artifact.Size));
            }
            if (completed != artifact.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Download verification failed.");
            await output.FlushAsync(token); return;
        }
    }

    internal async Task VerifyPackageAsync(string path, string filesRoot, BootstrapPackage expected, bool extract, CancellationToken token)
    {
        NoLinks(path); NoLinks(filesRoot);
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 2 or > 100000) throw new InvalidDataException("Invalid runtime archive entry count.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            Relative(entry.FullName);
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (mode is 0xa000 or 0x4000 || (entry.ExternalAttributes & 0x400) != 0 || !entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Runtime archives cannot contain links, directories, or duplicate paths.");
        }
        byte[] Metadata(string name, int limit)
        {
            if (!entries.TryGetValue(name, out var entry) || entry.Length > limit) throw new InvalidDataException("Missing or oversized signed runtime metadata.");
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output);
            if (output.Length != entry.Length || output.Length > limit) throw new InvalidDataException("Runtime metadata exceeds its declared length.");
            return output.ToArray();
        }
        var manifestBytes = Metadata("manifest.json", 4 * 1024 * 1024);
        var signature = JsonSerializer.Deserialize(Metadata("signature.json", 64 * 1024), BootstrapJson.Default.BootstrapSignature) ?? throw new InvalidDataException("Empty runtime signature.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(signature.PublicKey);
        if (signature.Algorithm != "RSA-PSS-SHA256" || rsa.KeySize < 2048 || !Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).Equals(descriptor.PublisherKeySha256, StringComparison.OrdinalIgnoreCase) ||
            !rsa.VerifyData(manifestBytes, Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new CryptographicException("Runtime publisher signature is invalid.");
        var manifest = JsonSerializer.Deserialize(manifestBytes, BootstrapJson.Default.BootstrapManifest) ?? throw new InvalidDataException("Empty runtime manifest.");
        if (manifest.FormatVersion is < 1 or > 5 || !manifest.Id.Equals(expected.Id, StringComparison.OrdinalIgnoreCase) || manifest.Version != expected.Version || manifest.TargetRuntime != expected.TargetRuntime || manifest.Payload.Count > 99998)
            throw new InvalidDataException("The runtime archive has a different signed identity.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in manifest.Payload)
        {
            Relative(file.Path);
            if (!paths.Add(file.Path) || !HashText(file.Sha256) || file.Size < 0 || file.Size > MaximumExpandedBytes - total || !entries.TryGetValue("payload/" + file.Path, out var entry) || entry.Length != file.Size) throw new InvalidDataException("Invalid runtime payload binding.");
            total += file.Size;
        }
        if (entries.Count != manifest.Payload.Count + 2) throw new InvalidDataException("The runtime archive contains undeclared files.");
        if (extract && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(filesRoot))!).AvailableFreeSpace < total + 16 * 1024 * 1024) throw new IOException("There is insufficient disk space to extract this runtime.");
        long completed = 0;
        foreach (var file in manifest.Payload)
        {
            token.ThrowIfCancellationRequested(); var destination = UnderRoot(filesRoot, file.Path); NoLinks(destination);
            if (!extract)
            {
                if (!await FileMatchesAsync(destination, file.Size, file.Sha256, token)) throw new CryptographicException("An installed shared runtime file changed; repair is required.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entries["payload/" + file.Path].Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[131072]; long written = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    if (read > file.Size - written) throw new InvalidDataException("Runtime file exceeds its signed length.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token); written += read;
                }
                if (written != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Runtime file verification failed.");
            }
            completed += file.Size; progress.Report(new(extract ? "Preparing shared files" : "Checking shared files", file.Path, completed, total));
        }
    }

    internal static async Task<bool> FileMatchesAsync(string path, long length, string hash, CancellationToken token)
    {
        NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length != length) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    internal static async Task<FileStream> AcquirePackageAsync(string path, CancellationToken token)
    {
        NoLinks(path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, deadline.Token); }
        }
    }
    internal static bool Identifier(string value) => value.Length is > 0 and <= 128 && value is not ("." or "..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    internal static bool HashText(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void ValidateDownload(BootstrapDownload value)
    {
        if (!Uri.TryCreate(value.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !HashText(value.Sha256) || value.Size is <= 0 or > MaximumExpandedBytes)
            throw new InvalidDataException("Downloads require an HTTPS address and pinned size/SHA-256.");
    }
    internal static void Relative(string path)
    {
        if (path.Length is < 1 or > 240 || Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|') || Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) throw new InvalidDataException("Invalid runtime archive path.");
    }
    internal static string UnderRoot(string root, string path)
    {
        Relative(path); root = Path.GetFullPath(root); var target = Path.GetFullPath(Path.Combine(root, path));
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Runtime path leaves its root.");
        return target;
    }
    internal static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Shared runtime paths cannot use reparse points.");
    }
}
