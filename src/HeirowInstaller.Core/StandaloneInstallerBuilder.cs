using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace HeirowInstaller.Core;

public sealed class StandaloneBuildOptions
{
    public string? SourceDirectory { get; set; }
    /// <summary>Optional .NET application project to publish with its runtime before packaging.</summary>
    public string? ApplicationProjectPath { get; set; }
    public string? LauncherPath { get; set; }
    public string? SigningKeyPath { get; set; }
    public string? UpdateCompanionExecutablePath { get; set; }
    public bool? UpdaterDefaultSelected { get; set; }
    public string DotNetExecutable { get; set; } = "dotnet";
    public string Configuration { get; set; } = "Release";
    public string Runtime { get; set; } = "win-x64";
    public string? Framework { get; set; }
    public PackageLegalDocuments? LegalDocuments { get; set; }
}

public sealed class StandaloneBuildResult
{
    public string OutputPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string LauncherSha256 { get; set; } = "";
    public bool ApplicationRuntimePublished { get; set; }
    public PackageManifest Manifest { get; set; } = new();
}

/// <summary>Builds a single setup executable from a self-contained launcher and complete app payload; no installed heirowInstaller is needed.</summary>
public static class StandaloneInstallerBuilder
{
    public static async Task<StandaloneBuildResult> BuildAsync(string manifestPath, string outputPath, StandaloneBuildOptions options,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        manifestPath = Path.GetFullPath(manifestPath); outputPath = Path.GetFullPath(outputPath);
        if (!outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Standalone installer output must end in .exe.");
        if ((options.SourceDirectory is null) == (options.ApplicationProjectPath is null)) throw new ArgumentException("Select either a complete source directory or an application project to publish.");
        var launcher = Path.GetFullPath(options.LauncherPath ?? FindLauncher() ?? throw new FileNotFoundException("A published self-contained heirowInstaller launcher is required. Pass --launcher or keep heirowInstaller.exe beside the portable builder."));
        StandaloneRuntimeInspector.RequireEmbeddedRuntime(launcher);
        if (PackageReader.HasEmbeddedPackage(launcher)) throw new InvalidDataException("Use a clean published launcher, not an already bundled application setup.");
        if (outputPath.Equals(launcher, StringComparison.OrdinalIgnoreCase) || outputPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) ||
            (options.SigningKeyPath is { } key && outputPath.Equals(Path.GetFullPath(key), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Installer output must differ from the launcher, manifest, and signing key.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath), InstallerJson.Options) ?? throw new InvalidDataException("Application manifest is empty.");
        manifest.LegalDocuments = LegalDocumentRules.PrepareForCreation(manifest.Id, options.LegalDocuments is { } suppliedLegal
            ? new() { PrivacyPolicy = suppliedLegal.PrivacyPolicy ?? manifest.LegalDocuments?.PrivacyPolicy,
                TermsAndConditions = suppliedLegal.TermsAndConditions ?? manifest.LegalDocuments?.TermsAndConditions }
            : manifest.LegalDocuments);
        PackageSafety.EnsureNoReparsePoints(outputPath);
        var work = Path.Combine(Path.GetTempPath(), "heirowInstaller-build-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(work, "payload"); Directory.CreateDirectory(payload);
        try
        {
            if (options.ApplicationProjectPath is { } project)
            {
                if (options.Runtime is not ("win-x64" or "win-x86" or "win-arm64")) throw new ArgumentException("Application runtime must be win-x64, win-x86, or win-arm64.");
                await PublishApplicationAsync(Path.GetFullPath(project), payload, options, progress, cancellationToken);
                if (options.SigningKeyPath is { } publishedKey)
                {
                    var keyInfo = new FileInfo(Path.GetFullPath(publishedKey)); var keyHash = PackageSafety.HashFile(keyInfo.FullName);
                    if (EnumerateFiles(payload).Any(file => new FileInfo(file).Length == keyInfo.Length && PackageSafety.HashEquals(PackageSafety.HashFile(file), keyHash)))
                        throw new InvalidDataException("Application publication copied the known signing key into its payload. Remove that publish item before building the installer.");
                }
            }
            else
            {
                var source = Path.GetFullPath(options.SourceDirectory!); PackageSafety.EnsureNoReparsePoints(source);
                var injectedPath = manifest.UpdateCompanion?.ExecutablePath ?? "heirowUpdater.exe";
                var injectedDeclaration = manifest.Payload.FirstOrDefault(x => x.Path.Equals(injectedPath, StringComparison.OrdinalIgnoreCase));
                var injectedSource = injectedDeclaration is null ? injectedPath : injectedDeclaration.Source.Length > 0 ? injectedDeclaration.Source : injectedDeclaration.Path;
                var files = manifest.Payload.Count == 0 ? EnumerateFiles(source).Select(path => Path.GetRelativePath(source, path)).ToList()
                    : manifest.Payload.Select(x => x.Source.Length > 0 ? x.Source : x.Path).ToList();
                foreach (var relative in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.UpdateCompanionExecutablePath is not null && PackageSafety.NormalizeRelativePath(relative).Equals(PackageSafety.NormalizeRelativePath(injectedSource), StringComparison.OrdinalIgnoreCase)) continue;
                    var input = PackageSafety.UnderRoot(source, relative);
                    if (input.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) || input.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (options.SigningKeyPath is { } signingKey && input.Equals(Path.GetFullPath(signingKey), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The signing key cannot be included as application payload.");
                    var destination = PackageSafety.UnderRoot(payload, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(input, destination, overwrite: true);
                }
            }
            if (options.UpdateCompanionExecutablePath is { } updater)
            {
                updater = Path.GetFullPath(updater); StandaloneRuntimeInspector.RequireEmbeddedRuntime(updater);
                manifest.UpdateCompanion ??= new();
                var feature = manifest.Features.FirstOrDefault(x => x.Id.Equals(manifest.UpdateCompanion.Feature, StringComparison.OrdinalIgnoreCase));
                if (feature is null)
                {
                    feature = new() { Id = manifest.UpdateCompanion.Feature, Name = "Application updates", Description = "Check for updates after this application exits.", Default = options.UpdaterDefaultSelected ?? false };
                    manifest.Features.Add(feature);
                }
                else if (options.UpdaterDefaultSelected is { } defaultSelected) feature.Default = defaultSelected;
                if (manifest.Payload.Count == 0)
                    manifest.Payload = EnumerateFiles(payload).Select(path => { var relative = Path.GetRelativePath(payload, path).Replace('\\', '/'); return new PayloadFile { Source = relative, Path = relative }; }).ToList();
                var destination = PackageSafety.UnderRoot(payload, manifest.UpdateCompanion.ExecutablePath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(updater, destination, overwrite: true);
                var declared = manifest.Payload.FirstOrDefault(x => x.Path.Equals(manifest.UpdateCompanion.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                if (declared is null) manifest.Payload.Add(new() { Source = manifest.UpdateCompanion.ExecutablePath, Path = manifest.UpdateCompanion.ExecutablePath, Feature = manifest.UpdateCompanion.Feature });
                else { declared.Source = manifest.UpdateCompanion.ExecutablePath; declared.Feature = manifest.UpdateCompanion.Feature; }
            }
            if (manifest.UpdateCompanion is not null && options.SigningKeyPath is null)
                throw new InvalidDataException("Building an app-local update companion requires --sign-key so generated configurations pin the publisher identity.");
            var stagedManifest = Path.Combine(work, "manifest.json"); File.WriteAllText(stagedManifest, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            cancellationToken.ThrowIfCancellationRequested();
            var package = Path.Combine(work, "application.hinst");
            var built = PackageBuilder.Build(stagedManifest, payload, package, new() { SigningKeyPath = options.SigningKeyPath });
            cancellationToken.ThrowIfCancellationRequested();
            BundleBuilder.Build(launcher, package, outputPath);
            progress?.Report(new("complete", "Standalone application installer built", 1, 1));
            return new() { OutputPath = outputPath, Sha256 = PackageSafety.HashFile(outputPath), Size = new FileInfo(outputPath).Length,
                LauncherSha256 = PackageSafety.HashFile(launcher), ApplicationRuntimePublished = options.ApplicationProjectPath is not null, Manifest = built };
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    private static string? FindLauncher()
    {
        var process = System.Environment.ProcessPath;
        var candidates = new List<string>();
        if (process is not null && !Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(Path.GetDirectoryName(process)!, "heirowInstaller.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "heirowInstaller.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Application source cannot contain reparse points.");
            if (Directory.Exists(path)) { foreach (var child in EnumerateFiles(path)) yield return child; }
            else yield return path;
        }
    }
    private static async Task PublishApplicationAsync(string project, string output, StandaloneBuildOptions options, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (!File.Exists(project) || Path.GetExtension(project).ToLowerInvariant() is not (".csproj" or ".vbproj" or ".fsproj"))
            throw new ArgumentException("Application publication requires a .NET application project.");
        var start = new ProcessStartInfo(options.DotNetExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(project)! };
        foreach (var argument in new[] { "publish", project, "--configuration", options.Configuration, "--runtime", options.Runtime, "--self-contained", "true", "--output", output,
            "-m:1", "-nr:false", "-p:UseSharedCompilation=false", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:IncludeAllContentForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:CopyOutputSymbolsToPublishDirectory=false" }) start.ArgumentList.Add(argument);
        if (options.Framework is not null) { start.ArgumentList.Add("--framework"); start.ArgumentList.Add(options.Framework); }
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync(token) is { } line) progress?.Report(new("publish-application", line, 0, 0)); }
        using var process = Process.Start(start) ?? throw new IOException("The application publisher did not start.");
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        try { await process.WaitForExitAsync(token); await Task.WhenAll(stdout, stderr); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            throw;
        }
        if (process.ExitCode != 0) throw new IOException("Application self-contained publication failed with exit code " + process.ExitCode + ".");
    }
}

/// <summary>Checks the .NET single-file manifest for an embedded runtime rather than assuming that any .exe is self-contained.</summary>
public static class StandaloneRuntimeInspector
{
    // Format: https://github.com/dotnet/runtime/tree/main/src/installer/managed/Microsoft.NET.HostModel/Bundle
    private static readonly byte[] BundleSignature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
    public static void RequireEmbeddedRuntime(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 64) throw new InvalidDataException("A published self-contained single-file executable is required.");
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("The executable is not a Windows apphost.");
        stream.Position = 0;
        var buffer = new byte[128 * 1024]; var carried = 0; long scanned = 0; long marker = -1;
        var scanLimit = Math.Min(stream.Length, 64L * 1024 * 1024);
        while (scanned < scanLimit)
        {
            var read = stream.Read(buffer, carried, (int)Math.Min(buffer.Length - carried, scanLimit - scanned));
            if (read == 0) break;
            var total = carried + read;
            var found = buffer.AsSpan(0, total).IndexOf(BundleSignature);
            if (found >= 0) { marker = scanned - carried + found; break; }
            scanned += read;
            // Retain signature-length overlap when the marker straddles two reads.
            carried = Math.Min(BundleSignature.Length - 1, total);
            Buffer.BlockCopy(buffer, total - carried, buffer, 0, carried);
        }
        if (marker < 8) throw new InvalidDataException("The executable is not a .NET single-file apphost with an embedded runtime.");
        stream.Position = marker - 8; var pointer = new byte[8]; stream.ReadExactly(pointer);
        var header = BitConverter.ToInt64(pointer);
        if (header <= 0 || header > stream.Length - 24) throw new InvalidDataException("The executable has no valid single-file bundle manifest.");
        stream.Position = header; using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var major = reader.ReadUInt32(); var minor = reader.ReadUInt32(); var count = reader.ReadInt32();
        if ((major is not (2 or 6)) || minor != 0 || count is < 1 or > 50000) throw new InvalidDataException("Unsupported .NET single-file bundle manifest.");
        string ReadName()
        {
            var length = reader.Read7BitEncodedInt();
            if (length is < 1 or > 32768 || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid bundle entry name.");
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
        _ = ReadName(); _ = reader.ReadInt64(); _ = reader.ReadInt64();
        var configurationOffset = reader.ReadInt64(); var configurationSize = reader.ReadInt64(); _ = reader.ReadUInt64();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64(); var size = reader.ReadInt64(); var compressed = major >= 6 ? reader.ReadInt64() : 0;
            _ = reader.ReadByte(); var name = ReadName();
            if (offset < 0 || size < 0 || compressed < 0 || offset > header || (compressed > 0 ? compressed : size) > header - offset || !names.Add(name))
                throw new InvalidDataException("Invalid single-file bundle entry.");
        }
        if (!names.Contains("System.Private.CoreLib.dll") || (!names.Contains("coreclr.dll") && !HasEmbeddedRuntimeExport(path)) || configurationSize is < 1 or > 4 * 1024 * 1024 ||
            configurationOffset < 0 || configurationOffset > header || configurationSize > header - configurationOffset)
            throw new InvalidDataException("The executable omits its .NET runtime. Publish with --self-contained true and IncludeNativeLibrariesForSelfExtract=true.");
        stream.Position = configurationOffset; var json = new byte[(int)configurationSize]; stream.ReadExactly(json);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtime) || !runtime.TryGetProperty("includedFrameworks", out _) || runtime.TryGetProperty("framework", out _) || runtime.TryGetProperty("frameworks", out _))
            throw new InvalidDataException("The executable still requires an installed .NET framework.");
    }

    private static bool HasEmbeddedRuntimeExport(string path)
    {
        // Newer .NET Windows single-file hosts can statically include CoreCLR instead of a coreclr.dll bundle entry.
        using var input = File.OpenRead(path); using var pe = new PEReader(input);
        var directory = pe.PEHeaders.PEHeader?.ExportTableDirectory;
        if (directory is null || directory.Value.Size < 40 || directory.Value.RelativeVirtualAddress <= 0) return false;
        var exports = pe.GetSectionData(directory.Value.RelativeVirtualAddress).GetReader(); exports.Offset = 24;
        var count = exports.ReadInt32(); _ = exports.ReadInt32(); var nameTable = exports.ReadInt32();
        if (count is < 1 or > 65536 || nameTable <= 0) return false;
        var names = pe.GetSectionData(nameTable).GetReader();
        for (var i = 0; i < count; i++)
        {
            var name = pe.GetSectionData(names.ReadInt32()).GetReader(); var text = new StringBuilder();
            for (var j = 0; j < 128; j++) { var character = name.ReadByte(); if (character == 0) break; text.Append((char)character); }
            if (text.ToString() is "DotNetRuntimeInfo" or "DotNetRuntimeContractDescriptor" or "coreclr_initialize") return true;
        }
        return false;
    }
}
