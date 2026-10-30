using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace HeirowInstaller.Core;

internal static class WindowsIntegration
{
    public static async Task<List<ModuleSnapshot>> ApplyModulesAsync(PackageManifest manifest, InstallPlan plan,
        InstallTransaction transaction, InstallOptions options, CancellationToken token, InstalledProduct? product = null)
    {
        var snapshots = new List<ModuleSnapshot>();
        if (options.Isolated) return snapshots;
        if (!OperatingSystem.IsWindows())
        {
            if (manifest.Registry.Count + manifest.Environment.Count + manifest.Services.Count + manifest.Shortcuts.Count > 0)
                throw new PlatformNotSupportedException("Windows integration modules require Windows.");
            return snapshots;
        }
        foreach (var registry in manifest.Registry.Where(x => InstallerEngine.IsSelected(x.Feature, plan.Features)))
        {
            token.ThrowIfCancellationRequested();
            ValidateRegistry(registry.Hive, registry.Key);
            var snapshot = CaptureRegistry(registry.Hive, registry.Key, registry.Name);
            transaction.AddRollback(() => { if (OperatingSystem.IsWindows()) RestoreRegistry(snapshot); });
            using var hive = Hive(registry.Hive);
            using var key = hive.CreateSubKey(registry.Key, writable: true);
            var value = InstallerEngine.Expand(registry.Value, plan.InstallDirectory);
            var kind = ParseRegistryKind(registry.Type);
            object data = kind switch
            {
                RegistryValueKind.DWord => int.Parse(value, CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(value, CultureInfo.InvariantCulture),
                RegistryValueKind.MultiString => value.Split('\n'),
                RegistryValueKind.Binary => Convert.FromBase64String(value),
                _ => value
            };
            key.SetValue(registry.Name, data, kind);
            snapshots.Add(snapshot);
        }
        foreach (var definition in manifest.Environment.Where(x => InstallerEngine.IsSelected(x.Feature, plan.Features)))
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Contains('=') || definition.Name.Contains('\0'))
                throw new InvalidDataException("Invalid environment variable name.");
            var target = plan.Scope == "machine" ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
            var previous = System.Environment.GetEnvironmentVariable(definition.Name, target);
            var value = InstallerEngine.Expand(definition.Value, plan.InstallDirectory);
            if (definition.Append && !string.IsNullOrEmpty(previous))
                value = previous.Split(';').Contains(value, StringComparer.OrdinalIgnoreCase) ? previous : previous.TrimEnd(';') + ";" + value;
            var snapshot = new ModuleSnapshot { Kind = "environment", Key = definition.Name, Values = new() { ["scope"] = plan.Scope, ["value"] = previous } };
            transaction.AddRollback(() => RestoreEnvironment(snapshot));
            System.Environment.SetEnvironmentVariable(definition.Name, value, target);
            snapshots.Add(snapshot);
        }
        var shortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in manifest.Shortcuts.Where(x => InstallerEngine.IsSelected(x.Feature, plan.Features)))
        {
            token.ThrowIfCancellationRequested();
            var path = ShortcutPath(definition, plan.Scope);
            if (!shortcuts.Add(path)) throw new InvalidDataException("Duplicate shortcut destination.");
            var target = PackageSafety.UnderRoot(plan.InstallDirectory, definition.Target);
            if (!File.Exists(target)) throw new FileNotFoundException("Shortcut target is not installed: " + definition.Target);
            var arguments = definition.Arguments;
            var applicationTarget = target;
            if (plan.UpdateCompanionEnabled && definition.Target.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase))
            {
                var companion = manifest.UpdateCompanion!;
                target = companion.RuntimeMode == "shared"
                    ? SharedUpdaterRuntime.Resolve(InstallerEngine.ReadBoundCompanionConfiguration(
                        product ?? throw new InvalidDataException("Shared updater shortcuts require the validated installation identity."), manifest))
                    : PackageSafety.UnderRoot(plan.InstallDirectory, companion.ExecutablePath);
                var configuration = PackageSafety.UnderRoot(plan.InstallDirectory, companion.ConfigurationPath);
                if (!File.Exists(target) || !File.Exists(configuration)) throw new FileNotFoundException("The selected application updater is not installed.");
                arguments = "launch --config " + Quote(configuration) + " --" + (arguments.Length > 0 ? " " + arguments : "");
            }
            var snapshot = new ModuleSnapshot { Kind = "shortcut", Key = path, Values = new() { ["data"] = File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null } };
            var temporary = Path.Combine(transaction.Root, Guid.NewGuid().ToString("N") + ".lnk");
            CreateShortcut(temporary, target, arguments, PackageSafety.ResolveWorkingDirectory(plan.InstallDirectory, definition.WorkingDirectory),
                definition.Icon is { Length: > 0 } ? PackageSafety.UnderRoot(plan.InstallDirectory, definition.Icon) : applicationTarget);
            transaction.ReplaceFile(path, temporary);
            snapshots.Add(snapshot);
        }
        foreach (var definition in manifest.Services.Where(x => InstallerEngine.IsSelected(x.Feature, plan.Features)))
        {
            token.ThrowIfCancellationRequested();
            if (plan.Scope != "machine") throw new InvalidDataException("Windows service packages must use machine scope.");
            ValidateService(definition);
            if (await ServiceExistsAsync(definition.Name, token)) throw new InvalidOperationException($"Service already exists and is not owned by this install transaction: {definition.Name}");
            var snapshot = new ModuleSnapshot { Kind = "service", Key = definition.Name, Values = new() { ["definition"] = JsonSerializer.Serialize(definition, InstallerJson.Options), ["directory"] = plan.InstallDirectory } };
            transaction.AddRollback(() => DeleteServiceAsync(definition.Name, CancellationToken.None).GetAwaiter().GetResult());
            await CreateServiceAsync(definition, plan.InstallDirectory, token);
            snapshots.Add(snapshot);
        }
        if (manifest.Environment.Count > 0) BroadcastEnvironmentChange();
        return snapshots;
    }

    public static async Task RestoreModulesAsync(InstalledProduct product, InstallTransaction transaction, InstallOptions options, CancellationToken token)
    {
        if (product.Isolated) return;
        if (!OperatingSystem.IsWindows())
        {
            if (product.Modules.Any(x => !x.Kind.StartsWith("extension:", StringComparison.Ordinal))) throw new PlatformNotSupportedException("Windows integration modules require Windows.");
            return;
        }
        foreach (var snapshot in product.Modules.Where(x => !x.Kind.StartsWith("extension:", StringComparison.Ordinal)).Reverse())
        {
            token.ThrowIfCancellationRequested();
            switch (snapshot.Kind)
            {
                case "registry":
                    var current = CaptureRegistry(snapshot.Values["hive"]!, snapshot.Values["path"]!, snapshot.Key, snapshot.Values.GetValueOrDefault("view"));
                    transaction.AddRollback(() => { if (OperatingSystem.IsWindows()) RestoreRegistry(current); });
                    RestoreRegistry(snapshot);
                    break;
                case "environment":
                    var envCurrent = new ModuleSnapshot { Kind = "environment", Key = snapshot.Key, Values = new() { ["scope"] = snapshot.Values["scope"], ["value"] = System.Environment.GetEnvironmentVariable(snapshot.Key, snapshot.Values["scope"] == "machine" ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User) } };
                    transaction.AddRollback(() => RestoreEnvironment(envCurrent));
                    RestoreEnvironment(snapshot);
                    break;
                case "shortcut":
                    ValidateShortcutReceipt(snapshot.Key, product.Scope);
                    if (snapshot.Values["data"] is { } data)
                    {
                        var temporary = Path.Combine(transaction.Root, Guid.NewGuid().ToString("N") + ".lnk");
                        await File.WriteAllBytesAsync(temporary, Convert.FromBase64String(data), token);
                        transaction.ReplaceFile(snapshot.Key, temporary);
                    }
                    else transaction.DeleteFile(snapshot.Key);
                    break;
                case "service":
                    var definition = JsonSerializer.Deserialize<ServiceDefinition>(snapshot.Values["definition"]!, InstallerJson.Options) ?? throw new InvalidDataException("Invalid cached service definition.");
                    ValidateService(definition);
                    transaction.AddRollback(() => CreateServiceAsync(definition, snapshot.Values["directory"]!, CancellationToken.None).GetAwaiter().GetResult());
                    await DeleteServiceAsync(definition.Name, token);
                    break;
                default: throw new InvalidDataException("Unknown installed module kind: " + snapshot.Kind);
            }
        }
        if (product.Modules.Any(x => x.Kind == "environment")) BroadcastEnvironmentChange();
    }

    public static async Task<bool> InstallPrerequisitesAsync(PackageManifest manifest, InstallPlan plan, string extracted,
        InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var reboot = false;
        foreach (var prerequisite in manifest.Prerequisites.Where(x => InstallerEngine.IsSelected(x.Feature, plan.Features)))
        {
            token.ThrowIfCancellationRequested();
            PackageSafety.ValidateId(prerequisite.Id);
            if (prerequisite.Detection is { } detection)
            {
                if (PrerequisiteDetection.IsSatisfied(detection)) continue;
            }
            else if (prerequisite.DetectFile is { Length: > 0 } detect && File.Exists(InstallerEngine.Expand(detect, plan.InstallDirectory))) continue;
            if (options.Isolated) throw new InvalidOperationException("Isolated validation cannot run an external prerequisite installer.");
            progress?.Report(new("prerequisite", prerequisite.Name, 0, 1));
            string executable;
            if (prerequisite.Executable is { Length: > 0 } relative)
            {
                executable = PackageSafety.UnderRoot(extracted, relative);
                if (!manifest.Payload.Any(x => x.Path.Equals(relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Prerequisite executable must be declared in the verified payload.");
            }
            else if (prerequisite.DownloadUrl is { } url && prerequisite.Sha256 is { } hash)
            {
                var feed = new UpdateFeed { InstallerUrl = url, Sha256 = hash, PackageType = "exe", ProductId = prerequisite.Id, Version = "0" };
                using var client = new UpdateClient();
                executable = await client.DownloadAsync(feed, Path.Combine(extracted, ".prerequisites"), progress, token);
            }
            else throw new InvalidDataException("A prerequisite requires a verified payload executable or HTTPS download and SHA256.");
            var exit = await RunProcessAsync(executable, prerequisite.Arguments.Select(x => InstallerEngine.Expand(x, plan.InstallDirectory)), token);
            if (!prerequisite.SuccessExitCodes.Contains(exit)) throw new InvalidOperationException($"Prerequisite {prerequisite.Id} failed with exit code {exit}.");
            reboot |= exit is 3010 or 1641;
        }
        return reboot;
    }

    public static async Task<int> RunMsiAsync(string operation, string packagePath, string? productCode, LegacyMsiDefinition definition,
        InstallPlan plan, InstallOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Installer requires Windows.");
        if (options.Isolated) throw new InvalidOperationException("Isolated validation cannot execute Windows Installer.");
        var arguments = MsiAdapter.BuildArguments(operation, packagePath, productCode, definition, plan, options);
        // Windows Installer handles its own cancellation and rollback. Once started, await completion before changing our receipt.
        token.ThrowIfCancellationRequested();
        var exit = await RunProcessAsync(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "msiexec.exe"), arguments, CancellationToken.None, msiArguments: true);
        if (exit is not (0 or 3010 or 1641)) throw new InvalidOperationException($"Windows Installer {operation} failed with exit code {exit}.");
        return exit;
    }

    public static string GetMsiProperty(string path, string property)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint database = 0, view = 0, record = 0;
        try
        {
            CheckMsi(MsiOpenDatabase(path, IntPtr.Zero, out database));
            CheckMsi(MsiDatabaseOpenView(database, "SELECT `Value` FROM `Property` WHERE `Property`='" + property + "'", out view));
            CheckMsi(MsiViewExecute(view, 0)); CheckMsi(MsiViewFetch(view, out record));
            uint size = 0; _ = MsiRecordGetString(record, 1, null, ref size);
            var value = new System.Text.StringBuilder((int)size + 1); size++;
            CheckMsi(MsiRecordGetString(record, 1, value, ref size)); return value.ToString();
        }
        finally { if (record != 0) MsiCloseHandle(record); if (view != 0) MsiCloseHandle(view); if (database != 0) MsiCloseHandle(database); }
    }

    public static string? GetMsiInstallLocation(string? productCode)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(productCode, out _)) return null;
        uint size = 0; var error = MsiGetProductInfo(productCode!, "InstallLocation", null, ref size);
        if (error is not (0 or 234) || size == 0 || size > 32768) return null;
        var value = new System.Text.StringBuilder((int)size + 1); size++;
        if (MsiGetProductInfo(productCode!, "InstallLocation", value, ref size) != 0) return null;
        var path = value.ToString();
        if (!Path.IsPathFullyQualified(path)) return null;
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetDirectoryName(path) is null) return null;
        PackageSafety.EnsureNoReparsePoints(path);
        return path;
    }

    public static bool IsMsiInstalled(string? productCode) => OperatingSystem.IsWindows() && Guid.TryParse(productCode, out _) && MsiQueryProductState(productCode!) == 5;

    public static async Task<int> RunExecutableAsync(string operation, LegacyExecutableDefinition definition, string extracted,
        string target, InstallOptions options, CancellationToken token)
    {
        if (options.Isolated) throw new InvalidOperationException("Isolated validation cannot run an external installer.");
        var executable = operation == "uninstall" && definition.UninstallExecutable is { Length: > 0 } uninstall
            ? Path.GetFullPath(InstallerEngine.Expand(uninstall, target))
            : PackageSafety.UnderRoot(extracted, definition.PayloadPath);
        PackageSafety.EnsureNoReparsePoints(executable);
        var arguments = LegacyExecutableAdapter.BuildArguments(operation, definition, target, options);
        token.ThrowIfCancellationRequested();
        var exit = await RunProcessAsync(executable, arguments, CancellationToken.None);
        if (!definition.SuccessExitCodes.Contains(exit)) throw new InvalidOperationException($"Legacy executable {operation} failed with exit code {exit}.");
        return exit;
    }

    public static void RegisterProduct(InstalledProduct product, string stateRoot, InstallTransaction transaction, PackageManifest manifest,
        IReadOnlyDictionary<string, object>? preparedValues = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = ArpPath(product.Id);
        using var hive = Hive(product.Scope);
        using (var existing = hive.OpenSubKey(path))
            if (existing is not null && existing.GetValue("HeirowInstallerProductId") as string != product.Id)
                throw new InvalidOperationException("Another product owns this uninstall registration.");
        var values = preparedValues ?? ProductRegistrationValues(product, stateRoot, manifest);
        foreach (var pair in values)
        {
            var snapshot = CaptureRegistry(product.Scope, path, pair.Key);
            transaction.AddRollback(() => { if (OperatingSystem.IsWindows()) RestoreRegistry(snapshot); });
            using var key = hive.CreateSubKey(path);
            key.SetValue(pair.Key, pair.Value, pair.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
    }

    internal static IReadOnlyDictionary<string, object> ProductRegistrationValues(InstalledProduct product, string stateRoot, PackageManifest manifest)
    {
        var values = new Dictionary<string, object>
        {
            ["DisplayName"] = product.Name, ["DisplayVersion"] = product.Version, ["Publisher"] = product.Publisher,
            ["InstallLocation"] = product.InstallDirectory, ["InstallDate"] = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            ["UninstallString"] = Quote(product.UninstallerPath!) + " uninstall " + Quote(product.Id) + " --state-root " + Quote(stateRoot) + " --scope " + product.Scope,
            ["QuietUninstallString"] = Quote(product.UninstallerPath!) + " uninstall " + Quote(product.Id) + " --state-root " + Quote(stateRoot) + " --scope " + product.Scope + " --silent",
            ["ModifyPath"] = Quote(product.UninstallerPath!) + " repair " + Quote(product.Id) + " --state-root " + Quote(stateRoot) + " --scope " + product.Scope,
            ["HeirowInstallerProductId"] = product.Id, ["NoModify"] = 0, ["NoRepair"] = 0,
            ["EstimatedSize"] = (int)Math.Min(int.MaxValue, manifest.Payload.Sum(x => x.Size) / 1024)
        };
        if (!string.IsNullOrWhiteSpace(manifest.Homepage)) values["URLInfoAbout"] = manifest.Homepage;
        if (manifest.EntryPoint.Length > 0) values["DisplayIcon"] = PackageSafety.UnderRoot(product.InstallDirectory, manifest.EntryPoint);
        return values;
    }

    public static void UnregisterProduct(InstalledProduct product, InstallTransaction transaction)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = ArpPath(product.Id);
        using var hive = Hive(product.Scope);
        using var key = hive.OpenSubKey(path);
        if (key is null) return;
        if (key.GetValue("HeirowInstallerProductId") as string != product.Id) throw new InvalidDataException("Uninstall registration ownership mismatch.");
        foreach (var name in key.GetValueNames())
        {
            var snapshot = CaptureRegistry(product.Scope, path, name);
            transaction.AddRollback(() => { if (OperatingSystem.IsWindows()) RestoreRegistry(snapshot); });
        }
        hive.DeleteSubKeyTree(path, false);
    }

    [SupportedOSPlatform("windows")]
    internal static ModuleSnapshot CaptureRegistry(string hiveName, string path, string name, string? view = null)
    {
        ValidateRegistry(hiveName, path);
        using var hive = Hive(hiveName, view);
        using var key = hive.OpenSubKey(path);
        var exists = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
        var kind = exists ? key!.GetValueKind(name) : RegistryValueKind.String;
        return new()
        {
            Kind = "registry", Key = name,
            Values = new() { ["hive"] = hiveName, ["view"] = view, ["path"] = path, ["keyExisted"] = (key is not null).ToString(), ["exists"] = exists.ToString(),
                ["type"] = kind.ToString(), ["data"] = exists ? JsonSerializer.Serialize(key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)) : null }
        };
    }

    [SupportedOSPlatform("windows")]
    internal static void RestoreRegistry(ModuleSnapshot snapshot)
    {
        var path = snapshot.Values["path"]!;
        ValidateRegistry(snapshot.Values["hive"]!, path);
        using var hive = Hive(snapshot.Values["hive"]!, snapshot.Values.GetValueOrDefault("view"));
        if (snapshot.Values["exists"] == bool.TrueString)
        {
            using var key = hive.CreateSubKey(path);
            var kind = Enum.Parse<RegistryValueKind>(snapshot.Values["type"]!);
            var json = snapshot.Values["data"]!;
            object value = kind switch
            {
                RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(json),
                RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(json),
                RegistryValueKind.Binary => JsonSerializer.Deserialize<byte[]>(json)!,
                RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(json)!,
                _ => JsonSerializer.Deserialize<string>(json)!
            };
            key.SetValue(snapshot.Key, value, kind);
        }
        else
        {
            using (var key = hive.OpenSubKey(path, writable: true)) key?.DeleteValue(snapshot.Key, false);
            if (snapshot.Values["keyExisted"] != bool.TrueString)
            {
                using var key = hive.OpenSubKey(path);
                if (key is not null && key.SubKeyCount == 0 && key.ValueCount == 0) hive.DeleteSubKey(path, false);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static RegistryValueKind ParseRegistryKind(string value) => value.ToLowerInvariant() switch
    {
        "string" => RegistryValueKind.String, "expandstring" => RegistryValueKind.ExpandString,
        "dword" => RegistryValueKind.DWord, "qword" => RegistryValueKind.QWord,
        "multistring" => RegistryValueKind.MultiString, "binary" => RegistryValueKind.Binary,
        _ => throw new InvalidDataException("Unsupported registry value type: " + value)
    };
    [SupportedOSPlatform("windows")]
    internal static RegistryKey Hive(string value, string? view = null) => RegistryKey.OpenBaseKey(value switch
    {
        "user" => RegistryHive.CurrentUser, "machine" => RegistryHive.LocalMachine,
        _ => throw new InvalidDataException("Registry hive must be user or machine.")
    }, view switch { "registry64" => RegistryView.Registry64, "registry32" => RegistryView.Registry32, null => System.Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32, _ => throw new InvalidDataException("Invalid registry view.") });
    private static void ValidateRegistry(string hive, string path)
    {
        if (hive is not ("user" or "machine") || string.IsNullOrWhiteSpace(path) || path.StartsWith('\\') || path.Contains("..", StringComparison.Ordinal) || path.Contains('/'))
            throw new InvalidDataException("Invalid registry hive or key path.");
    }
    private static void RestoreEnvironment(ModuleSnapshot snapshot) => System.Environment.SetEnvironmentVariable(snapshot.Key, snapshot.Values["value"], snapshot.Values["scope"] == "machine" ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User);
    private static string ShortcutPath(ShortcutDefinition definition, string scope)
    {
        var name = PackageSafety.NormalizeRelativePath(definition.Name);
        if (name.Contains('/')) throw new InvalidDataException("Shortcut names cannot contain directories.");
        if (!name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) name += ".lnk";
        var folder = definition.Location.ToLowerInvariant() switch
        {
            "startmenu" => System.Environment.GetFolderPath(scope == "machine" ? System.Environment.SpecialFolder.CommonPrograms : System.Environment.SpecialFolder.Programs),
            "desktop" => System.Environment.GetFolderPath(scope == "machine" ? System.Environment.SpecialFolder.CommonDesktopDirectory : System.Environment.SpecialFolder.DesktopDirectory),
            _ => throw new InvalidDataException("Shortcut location must be startMenu or desktop.")
        };
        return PackageSafety.UnderRoot(folder, name);
    }
    private static void ValidateShortcutReceipt(string path, string scope)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        var expected = new[] { ShortcutPath(new() { Name = name, Location = "startMenu" }, scope), ShortcutPath(new() { Name = name, Location = "desktop" }, scope) };
        if (!expected.Contains(full, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Cached shortcut path escapes standard shortcut folders.");
    }
    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string path, string target, string arguments, string workingDirectory, string icon)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("Windows Script Host shortcut support is unavailable.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(path);
        try { shortcut.TargetPath = target; shortcut.Arguments = arguments; shortcut.WorkingDirectory = workingDirectory; shortcut.IconLocation = icon; shortcut.Save(); }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }
    private static void ValidateService(ServiceDefinition definition)
    {
        PackageSafety.ValidateId(definition.Name);
        if (definition.Start is not ("auto" or "demand" or "disabled")) throw new InvalidDataException("Service start must be auto, demand, or disabled.");
        _ = PackageSafety.NormalizeRelativePath(definition.Executable);
    }
    private static async Task<bool> ServiceExistsAsync(string name, CancellationToken token) => await RunProcessAsync("sc.exe", ["query", name], token) == 0;
    private static async Task CreateServiceAsync(ServiceDefinition definition, string directory, CancellationToken token)
    {
        var executable = PackageSafety.UnderRoot(directory, definition.Executable);
        var binary = Quote(executable) + (definition.Arguments.Count > 0 ? " " + string.Join(' ', definition.Arguments.Select(Quote)) : "");
        var exit = await RunProcessAsync("sc.exe", ["create", definition.Name, "binPath=", binary, "start=", definition.Start, "DisplayName=", string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName], token);
        if (exit != 0) throw new InvalidOperationException($"Service creation failed ({exit}): {definition.Name}");
        if (definition.StartAfterInstall && definition.Start != "disabled")
        {
            exit = await RunProcessAsync("sc.exe", ["start", definition.Name], token);
            if (exit is not (0 or 1056)) throw new InvalidOperationException($"Service start failed ({exit}): {definition.Name}");
        }
    }
    private static async Task DeleteServiceAsync(string name, CancellationToken token)
    {
        _ = await RunProcessAsync("sc.exe", ["stop", name], token);
        var exit = await RunProcessAsync("sc.exe", ["delete", name], token);
        if (exit is not (0 or 1060)) throw new InvalidOperationException($"Service removal failed ({exit}): {name}");
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (!await ServiceExistsAsync(name, token)) return;
            await Task.Delay(250, token);
        }
        throw new IOException("Service removal is still pending: " + name);
    }
    internal static async Task<int> RunProcessAsync(string executable, IEnumerable<string> arguments, CancellationToken token, bool msiArguments = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (msiArguments) start.Arguments = MsiAdapter.SerializeArguments(arguments);
        else foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Win32Exception("Could not start installer component.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); await Task.WhenAll(output, error); return process.ExitCode; }
        catch (OperationCanceledException)
        {
            // A running external installer must finish its own transaction; never kill it midway.
            await process.WaitForExitAsync(CancellationToken.None); throw;
        }
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal).TrimEnd('\\') + "\"";
    private static string ArpPath(string id) { PackageSafety.ValidateId(id); return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\heirowInstaller." + id; }
    private static void CheckMsi(uint value) { if (value != 0) throw new Win32Exception((int)value, "Could not read MSI metadata."); }
    private static void BroadcastEnvironmentChange() { if (OperatingSystem.IsWindows()) SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "Environment", 2, 1000, out _); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint handle);
    [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(uint record, uint field, System.Text.StringBuilder? value, ref uint length);
    [DllImport("msi.dll", EntryPoint = "MsiGetProductInfoW", CharSet = CharSet.Unicode)] private static extern uint MsiGetProductInfo(string productCode, string property, System.Text.StringBuilder? value, ref uint length);
    [DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = CharSet.Unicode)] private static extern int MsiQueryProductState(string productCode);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
}
