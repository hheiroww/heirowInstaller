using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeirowInstaller.Bootstrap;

// Source handles are opened by the original user; every byte is verified before
// the administrator publishes the protected directory. No source probing file,
// extra DLL, plug-in, runtime hook or environment setting is promoted.
internal static class ApplicationPromotionPayloads
{
    private const int MaximumArgumentsBytes = 1024 * 1024;
    internal static void ValidateArguments(IReadOnlyList<string> args)
    {
        long total = 0;
        if (args.Count > 4096 || args.Any(argument => argument is null || argument.Contains('\0') || argument.Length > 32768 || (total += Encoding.UTF8.GetByteCount(argument)) > MaximumArgumentsBytes))
            throw new InvalidDataException("The application argument handoff exceeds its supported size.");
    }
    internal static async Task WriteHandoffAsync(Stream output, string nonce, string digest, string[] arguments, CancellationToken token)
    {
        ValidateArguments(arguments); await output.WriteAsync(Convert.FromHexString(nonce), token); await output.WriteAsync(Convert.FromHexString(digest), token);
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, arguments.Length); await output.WriteAsync(length, token);
        foreach (string argument in arguments)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(argument); BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length); await output.WriteAsync(length, token); await output.WriteAsync(bytes, token);
        }
        await output.FlushAsync(token);
    }
    internal static async Task<string[]> ReadHandoffAsync(Stream input, string nonce, string digest, CancellationToken token)
    {
        byte[] proof = new byte[64]; await input.ReadExactlyAsync(proof, token);
        if (!CryptographicOperations.FixedTimeEquals(proof.AsSpan(0, 32), Convert.FromHexString(nonce)) || !CryptographicOperations.FixedTimeEquals(proof.AsSpan(32, 32), Convert.FromHexString(digest)))
            throw new CryptographicException("The original application request changed.");
        byte[] length = new byte[4]; await input.ReadExactlyAsync(length, token); int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is < 0 or > 4096) throw new InvalidDataException("Too many application arguments.");
        var arguments = new string[count]; int total = 0;
        for (int index = 0; index < count; index++)
        {
            await input.ReadExactlyAsync(length, token); int size = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (size < 0 || size > MaximumArgumentsBytes - total) throw new InvalidDataException("An application argument exceeds its bound.");
            total += size; var bytes = new byte[size]; await input.ReadExactlyAsync(bytes, token); arguments[index] = new UTF8Encoding(false, true).GetString(bytes);
        }
        ValidateArguments(arguments); return arguments;
    }
    internal static async Task CopyVerifiedAsync(Func<string, FileStream> openSource, string source, string destination,
        long size, string digest, IProgress<BootstrapProgress> progress, CancellationToken token)
    {
        BootstrapEngine.NoLinks(destination);
        await using var input = openSource(source);
        if (input.Length != size) throw new CryptographicException("A signed application source has a different length.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; long copied = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (read > size - copied) throw new CryptographicException("An application source exceeds its signed length.");
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token); copied += read;
            progress.Report(new("Preparing administrator application", Path.GetFileName(source), copied, size));
        }
        if (copied != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("An application source changed. Run setup to repair it.");
        await output.FlushAsync(token);
    }

    internal static async Task<IReadOnlyList<string>> SourceCacheCandidatesAsync(BootstrapDescriptor descriptor, string applicationRoot,
        IReadOnlyList<string> allowedSharedRoots, Func<string, BootstrapPackage, string> packageRoot, Func<string, FileStream> openSource, CancellationToken token)
    {
        // The receipt-generated file is a selector among fixed scope roots. It
        // never authorizes an arbitrary path or changes signed archive hashes.
        string probing = BootstrapEngine.UnderRoot(applicationRoot, descriptor.RuntimeConfigPath![..^5] + ".dev.json");
        await using var input = openSource(probing);
        if (input.Length is <= 0 or > 128 * 1024) throw new InvalidDataException("The application's shared-library binding is missing or oversized.");
        using var bytes = new MemoryStream(); await input.CopyToAsync(bytes, token);
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 1 ||
            !document.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Count() != 1 ||
            !options.TryGetProperty("additionalProbingPaths", out var paths) || paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() != descriptor.ProbingPackageIds.Count)
            throw new InvalidDataException("The application's shared-library binding has an unexpected shape.");
        var definitions = descriptor.Packages.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (string candidate in allowedSharedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool matches = true;
            for (int index = 0; index < descriptor.ProbingPackageIds.Count; index++)
                if (paths[index].ValueKind != JsonValueKind.String || !string.Equals(paths[index].GetString(),
                    Path.Combine(packageRoot(candidate, definitions[descriptor.ProbingPackageIds[index]]), "files"), StringComparison.OrdinalIgnoreCase)) { matches = false; break; }
            if (matches) candidates.Add(candidate);
        }
        if (candidates.Count == 0) throw new InvalidDataException("The application's shared-library binding selects an unverified cache. Run setup to repair it.");
        return candidates;
    }

    internal static async Task StageApplicationAsync(BootstrapDescriptor descriptor, string sourceRoot, string destination,
        IReadOnlyDictionary<string, string> protectedPackages, Func<string, FileStream> openSource,
        IProgress<BootstrapProgress> progress, CancellationToken token)
    {
        BootstrapEngine.Validate(descriptor);
        if (descriptor.HostKind != "application") throw new InvalidDataException("Only a signed application may use application promotion.");
        long size = descriptor.ApplicationFiles.Sum(file => file.Size);
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace < size + 16 * 1024 * 1024)
            throw new IOException("There is insufficient space for the protected application files.");
        foreach (var file in descriptor.ApplicationFiles)
        {
            token.ThrowIfCancellationRequested();
            await CopyVerifiedAsync(openSource, BootstrapEngine.UnderRoot(sourceRoot, file.Path), BootstrapEngine.UnderRoot(destination, file.Path),
                file.Size, file.Sha256, progress, token);
        }
        string probing = BootstrapEngine.UnderRoot(destination, descriptor.RuntimeConfigPath![..^5] + ".dev.json");
        Directory.CreateDirectory(Path.GetDirectoryName(probing)!);
        await using var output = new FileStream(probing, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteStartObject("runtimeOptions"); writer.WriteStartArray("additionalProbingPaths");
            foreach (string id in descriptor.ProbingPackageIds)
            {
                if (!protectedPackages.TryGetValue(id, out var root) || !Path.IsPathFullyQualified(root)) throw new InvalidDataException("A protected runtime binding is absent.");
                writer.WriteStringValue(root);
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject(); await writer.FlushAsync(token);
        }
    }

    internal static void ValidateInventory(BootstrapDescriptor descriptor, string root)
    {
        var expected = descriptor.ApplicationFiles.Select(file => file.Path).Append(descriptor.RuntimeConfigPath![..^5] + ".dev.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Inspect(string directory)
        {
            BootstrapEngine.NoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                BootstrapEngine.NoLinks(path);
                if (Directory.Exists(path)) Inspect(path);
                else if (!expected.Remove(Path.GetRelativePath(root, path).Replace('\\', '/'))) throw new InvalidDataException("The protected application contains an undeclared file.");
            }
        }
        Inspect(root);
        if (expected.Count != 0) throw new InvalidDataException("A signed protected application file is absent.");
    }

    internal static void ConfigureEnvironment(ProcessStartInfo start, string temporary, string profile, string roaming, string local, string originalRoot)
    {
        var dotnet = start.Environment.TryGetValue("DOTNET_ROOT", out var runtime) ? runtime : null;
        start.Environment.Clear();
        string windows = Path.GetDirectoryName(Environment.SystemDirectory)!;
        start.Environment["SystemRoot"] = windows; start.Environment["WINDIR"] = windows;
        start.Environment["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd(Path.DirectorySeparatorChar);
        start.Environment["ComSpec"] = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        start.Environment["PATH"] = Environment.SystemDirectory + ";" + windows;
        start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary;
        start.Environment["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        start.Environment["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        start.Environment["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        start.Environment["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        start.Environment["USERPROFILE"] = profile; start.Environment["APPDATA"] = roaming; start.Environment["LOCALAPPDATA"] = local;
        start.Environment["HOMEDRIVE"] = Path.GetPathRoot(profile)!.TrimEnd(Path.DirectorySeparatorChar);
        start.Environment["HOMEPATH"] = profile[(Path.GetPathRoot(profile)!.Length - 1)..];
        start.Environment["APP_INSTALL_ROOT"] = start.WorkingDirectory;
        start.Environment["SOCKETJACK_ORIGINAL_INSTALL_ROOT"] = originalRoot;
        if (dotnet is not null)
        { start.Environment["DOTNET_ROOT"] = dotnet; start.Environment["DOTNET_ROOT_X64"] = dotnet; start.Environment["DOTNET_ROOT_ARM64"] = dotnet; }
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"; start.Environment["DOTNET_EnableDiagnostics"] = "0";
    }
}
