using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HeirowInstaller.Core;

public enum RunningApplicationsChoice { CloseAppsAndContinue, Cancel }

/// <summary>A process observed using this application's exact executable or installed resources.</summary>
public sealed record RunningApplication(int ProcessId, long StartTimeUtcTicks, string ExecutablePath,
    string DisplayName, string AffectedPath, string Kind, bool CanRequestClose);

public sealed record RunningApplicationsRequest(string ProductId, string ProductName, string Operation,
    IReadOnlyList<RunningApplication> Applications, bool IsRetry, string Message);

public sealed class RunningApplicationsException : InvalidOperationException
{
    public IReadOnlyList<RunningApplication> Applications { get; }
    public RunningApplicationsException(IReadOnlyList<RunningApplication> applications)
        : base("Close the applications using this installation and try again: " +
            string.Join(", ", applications.Select(x => $"{x.DisplayName} (PID {x.ProcessId})"))) => Applications = applications;
}

/// <summary>Constructed from verified package/profile metadata; a PID or host name is never a closure target.</summary>
public sealed class RunningApplicationTarget
{
    public string ProductId { get; }
    public string ProductName { get; }
    public string Operation { get; }
    public string InstallDirectory { get; }
    public string? ExecutablePath { get; }
    public IReadOnlyList<string> AffectedResourceFiles { get; }
    public IReadOnlyList<string> WaitOnlyExecutables { get; } = [];
    public IReadOnlyList<RunningApplication> WaitOnlyProcesses { get; private init; } = [];
    internal IReadOnlySet<string> ExcludedExecutables { get; }

    private RunningApplicationTarget(string id, string name, string operation, string directory, string entryPoint,
        IEnumerable<string> resources, IEnumerable<string> excluded)
    {
        PackageSafety.ValidateId(id);
        ProductId = id; ProductName = name; Operation = operation;
        InstallDirectory = Path.GetFullPath(directory);
        PackageSafety.EnsureNoReparsePoints(InstallDirectory);
        var folderRoots = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile };
        if (Path.GetDirectoryName(InstallDirectory) is null || IsSystemPath(InstallDirectory) ||
            folderRoots.Select(Environment.GetFolderPath).Any(x => x.Length > 0 && x.Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("An application process guard requires an application directory outside Windows system directories.");
        ExecutablePath = entryPoint.Length == 0 ? null : PackageSafety.UnderRoot(InstallDirectory, entryPoint);
        AffectedResourceFiles = resources.Select(ValidateResourceFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ExcludedExecutables = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static RunningApplicationTarget FromPlan(InstallPlan plan, string operation = "install") =>
        FromManifest(plan.Manifest, plan.InstallDirectory, operation, plan.Features);

    public static RunningApplicationTarget FromManifest(PackageManifest manifest, string installDirectory,
        string operation = "install", IEnumerable<string>? features = null)
    {
        var selected = (features ?? manifest.Features.Where(x => x.Default || x.Required).Select(x => x.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = manifest.Services.Select(x => PackageSafety.UnderRoot(installDirectory, x.Executable)).ToList();
        if (manifest.UpdateCompanion is { } companion) excluded.Add(PackageSafety.UnderRoot(installDirectory, companion.ExecutablePath));
        return new(manifest.Id, manifest.Name, operation, installDirectory, manifest.EntryPoint,
            manifest.AffectedResources.Where(x => InstallerEngine.IsSelected(x.Feature, selected))
                .Select(x => ResolveResourceFile(x.Path, installDirectory)), excluded);
    }

    public static RunningApplicationTarget FromConfiguration(UpdateCompanionConfiguration configuration, string operation = "update") =>
        new(configuration.ProductId, configuration.Name, operation, configuration.InstallDirectory, configuration.EntryPoint,
            configuration.AffectedResourceFiles, [PackageSafety.UnderRoot(configuration.InstallDirectory, "heirowUpdater.exe")]);

    private RunningApplicationTarget(RunningApplicationTarget source, IReadOnlyList<string> waitOnly)
    {
        ProductId = source.ProductId; ProductName = source.ProductName; Operation = source.Operation;
        InstallDirectory = source.InstallDirectory; ExecutablePath = source.ExecutablePath;
        AffectedResourceFiles = source.AffectedResourceFiles; ExcludedExecutables = source.ExcludedExecutables;
        WaitOnlyExecutables = waitOnly;
        WaitOnlyProcesses = source.WaitOnlyProcesses;
    }

    /// <summary>Registered host paths can require manual closure, but never authorize sending a close request.</summary>
    public RunningApplicationTarget WithWaitOnlyExecutables(IEnumerable<string> executables) =>
        new(this, WaitOnlyExecutables.Concat(executables).Select(ValidateResourceFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    /// <summary>A dependency on one exact process never grants closure authority to it or other users of its executable.</summary>
    public RunningApplicationTarget WithWaitOnlyProcesses(IEnumerable<RunningApplication> processes)
    {
        var identities = WaitOnlyProcesses.Concat(processes).Select(identity =>
        {
            if (identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0 || !Path.IsPathFullyQualified(identity.ExecutablePath))
                throw new InvalidDataException("A wait-only dependency requires an exact process identity.");
            var image = Path.GetFullPath(identity.ExecutablePath); PackageSafety.EnsureNoReparsePoints(image);
            return identity with { ExecutablePath = image, Kind = "manual", CanRequestClose = false };
        }).DistinctBy(x => (x.ProcessId, x.StartTimeUtcTicks)).ToArray();
        if (identities.Length > 16) throw new InvalidDataException("Too many explicit updater process dependencies.");
        return new(this, WaitOnlyExecutables) { WaitOnlyProcesses = identities };
    }

    /// <summary>Only fixed Windows folders and the authored application root can be substituted.</summary>
    public static string ResolveResourceFile(string value, string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("An affected resource must name an exact installed file.");
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CommonProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        value = value.Replace("{app}", Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase);
        value = Regex.Replace(value, "%([^%]+)%", match => folders.TryGetValue(match.Groups[1].Value, out var folder) && folder.Length > 0
            ? folder : throw new InvalidDataException("Unsupported affected-resource folder variable."));
        return ValidateResourceFile(value);
    }

    internal static string ValidateResourceFile(string value)
    {
        if (value.IndexOfAny(['*', '?', '%', '{', '}', '"', '\0']) >= 0 || !Path.IsPathFullyQualified(value) ||
            value.Split(['/', '\\']).Any(x => x is "." or "..") ||
            (OperatingSystem.IsWindows() && (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] is not ('\\' or '/'))) ||
            value.Skip(OperatingSystem.IsWindows() ? 2 : 0).Contains(':'))
            throw new InvalidDataException("Affected resources must be exact absolute local file paths without traversal, streams or wildcards.");
        var path = Path.GetFullPath(value);
        if (IsSystemPath(path) || !IsResourceExtension(path) || Directory.Exists(path))
            throw new InvalidDataException("Affected resources must name application executable, library or plugin files outside Windows system directories.");
        PackageSafety.EnsureNoReparsePoints(path);
        return path;
    }

    internal static bool IsResourceExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".vst3" or ".clap";
    internal static bool IsWithin(string path, string directory) => path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool IsSystemPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && (path.Equals(windows, StringComparison.OrdinalIgnoreCase) || IsWithin(path, windows));
    }
}

/// <summary>Query-only discovery and explicitly consented normal window closure. Never terminates a process.</summary>
public static class RunningApplications
{
    public static Task<IReadOnlyList<RunningApplication>> InspectAsync(RunningApplicationTarget target, CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(target, cancellationToken), cancellationToken);

    public static async Task EnsureClosedAsync(RunningApplicationTarget target,
        Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>>? prompt = null,
        CancellationToken cancellationToken = default)
    {
        var retry = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applications = await InspectAsync(target, cancellationToken);
            if (applications.Count == 0)
            {
                // A short quiet interval catches an application restarting during the transition.
                await Task.Delay(750, cancellationToken);
                applications = await InspectAsync(target, cancellationToken);
                if (applications.Count == 0) return;
            }
            if (prompt is null) throw new RunningApplicationsException(applications);
            var message = retry
                ? "Some applications are still open or restarted. Save your work, finish any save dialogs, then retry or cancel installation."
                : "These applications use files that must be updated. Save your work before closing them. Applications that cannot be verified must be closed manually.";
            var choice = await prompt(new(target.ProductId, target.ProductName, target.Operation, applications, retry, message), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (choice != RunningApplicationsChoice.CloseAppsAndContinue)
                throw new OperationCanceledException("Installation cancelled before changing application files.", cancellationToken);
            var fresh = await InspectAsync(target, cancellationToken);
            retry = true;
            // New instances require their own consent; an old PID/name is not authority over a replacement process.
            if (fresh.Any(x => !applications.Any(y => SameIdentity(x, y)))) continue;
            foreach (var application in fresh)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (application.CanRequestClose) RequestClose(target, application, cancellationToken);
            }
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(200, cancellationToken);
                var remaining = await InspectAsync(target, cancellationToken);
                if (remaining.Count == 0) break;
                if (remaining.Any(x => !fresh.Any(y => SameIdentity(x, y)))) break;
            }
        }
    }

    private static bool SameIdentity(RunningApplication left, RunningApplication right) =>
        left.ProcessId == right.ProcessId && left.StartTimeUtcTicks == right.StartTimeUtcTicks &&
        left.ExecutablePath.Equals(right.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RunningApplication> Inspect(RunningApplicationTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return [];
        var resources = new HashSet<string>(target.AffectedResourceFiles.Where(File.Exists), StringComparer.OrdinalIgnoreCase);
        if (target.ExecutablePath is { } entry && File.Exists(entry)) resources.Add(entry);
        if (Directory.Exists(target.InstallDirectory))
        {
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(target.InstallDirectory, "*", enumeration))
            {
                token.ThrowIfCancellationRequested();
                if (!RunningApplicationTarget.IsResourceExtension(file)) continue;
                PackageSafety.EnsureNoReparsePoints(file);
                resources.Add(Path.GetFullPath(file));
                if (resources.Count > 8192) throw new InvalidDataException("The application has too many executable resources to inspect safely.");
            }
        }
        var candidates = resources.Count == 0 ? new Dictionary<int, RestartManager.Candidate>() : RestartManager.Query(resources.ToArray());
        // Explicit executable matching also covers very short-lived startup transitions before RM reports the image.
        foreach (var executable in target.WaitOnlyExecutables.Concat(target.ExecutablePath is { } entryPoint ? [entryPoint] : []).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                using (process)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var image = process.MainModule?.FileName;
                        if (image is not null && Path.GetFullPath(image).Equals(executable, StringComparison.OrdinalIgnoreCase))
                            candidates.TryAdd(process.Id, new(process.Id, process.StartTime.ToUniversalTime().Ticks, "", 0));
                    }
                    catch (Win32Exception) { candidates.TryAdd(process.Id, new(process.Id, 0, Path.GetFileName(executable), 0)); }
                    catch (InvalidOperationException) { }
                }
        var found = new List<RunningApplication>();
        using var currentProcess = Process.GetCurrentProcess();
        var currentSession = currentProcess.SessionId;
        foreach (var candidate in candidates.Values)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Id == Environment.ProcessId) continue;
            try
            {
                using var process = Process.GetProcessById(candidate.Id);
                if (process.HasExited) continue;
                if (candidate.StartTicks == 0) { found.Add(Manual(candidate)); continue; }
                if (process.StartTime.ToUniversalTime().Ticks != candidate.StartTicks) continue;
                var image = process.MainModule?.FileName;
                if (image is null) { found.Add(Manual(candidate)); continue; }
                image = Path.GetFullPath(image); PackageSafety.EnsureNoReparsePoints(image);
                var excluded = target.ExcludedExecutables.Contains(image);
                if (excluded && !target.WaitOnlyExecutables.Contains(image, StringComparer.OrdinalIgnoreCase)) continue;
                var isEntry = image.Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                string? affected = isEntry ? image : null;
                var plugin = false;
                if (!isEntry)
                    foreach (ProcessModule module in process.Modules)
                    {
                        var modulePath = Path.GetFullPath(module.FileName);
                        if (!resources.Contains(modulePath)) continue;
                        affected ??= modulePath;
                        // Only plugin modules (or explicitly authored library resources) identify an external GUI host.
                        if (Path.GetExtension(modulePath).ToLowerInvariant() is ".vst3" or ".clap" ||
                            target.AffectedResourceFiles.Contains(modulePath, StringComparer.OrdinalIgnoreCase)) plugin = true;
                    }
                affected ??= resources.Contains(image) || target.WaitOnlyExecutables.Contains(image, StringComparer.OrdinalIgnoreCase) ? image : null;
                // RM may report a file handle rather than a mapped module. It can block replacement, but cannot grant closure.
                var canClose = !excluded && affected is not null && (isEntry || plugin) && !RunningApplicationTarget.IsSystemPath(image) &&
                    candidate.ApplicationType is not (3 or 4 or 1000) && process.SessionId == currentSession &&
                    process.SessionId != 0 && process.MainWindowHandle != IntPtr.Zero;
                var display = candidate.Name.Length > 0 ? candidate.Name : Path.GetFileName(image);
                found.Add(new(candidate.Id, candidate.StartTicks, image, display, affected ?? "Installed resource (manual closure required)",
                    isEntry ? "application" : plugin ? "plugin-host" : "manual", canClose));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { found.Add(Manual(candidate)); }
        }
        foreach (var dependency in target.WaitOnlyProcesses)
        {
            token.ThrowIfCancellationRequested();
            if (dependency.ProcessId == Environment.ProcessId || found.Any(x => x.ProcessId == dependency.ProcessId)) continue;
            try
            {
                using var process = Process.GetProcessById(dependency.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != dependency.StartTimeUtcTicks) continue;
                if (process.MainModule?.FileName is { } image && Path.GetFullPath(image).Equals(dependency.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    found.Add(dependency with { CanRequestClose = false, Kind = "manual" });
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
            { found.Add(dependency with { CanRequestClose = false, Kind = "manual" }); }
        }
        return found.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProcessId).ToArray();
    }

    private static RunningApplication Manual(RestartManager.Candidate candidate) =>
        new(candidate.Id, candidate.StartTicks, "", candidate.Name.Length > 0 ? candidate.Name : $"Process {candidate.Id}",
            "Installed resource (identity cannot be inspected)", "manual", false);

    private static void RequestClose(RunningApplicationTarget target, RunningApplication application, CancellationToken cancellationToken)
    {
        // Re-query resource ownership as well as PID/start/image immediately before sending WM_CLOSE.
        var verified = Inspect(target, cancellationToken).FirstOrDefault(x => SameIdentity(x, application) && x.CanRequestClose);
        if (verified is null) return;
        try
        {
            using var process = Process.GetProcessById(verified.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != verified.StartTimeUtcTicks) return;
            var image = process.MainModule?.FileName;
            if (image is null || !Path.GetFullPath(image).Equals(verified.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
            cancellationToken.ThrowIfCancellationRequested();
            process.CloseMainWindow(); // A refusal/save dialog stays open. No Kill, service stop or Restart Manager shutdown.
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    private static class RestartManager
    {
        internal sealed record Candidate(int Id, long StartTicks, string Name, int ApplicationType);
        internal static Dictionary<int, Candidate> Query(string[] files)
        {
            var key = new StringBuilder(33);
            var error = RmStartSession(out var session, 0, key);
            if (error != 0) throw new Win32Exception(error, "Unable to inspect applications using the installed files.");
            try
            {
                error = RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null);
                if (error != 0) throw new Win32Exception(error, "Unable to register exact installed resources for inspection.");
                uint count = 0;
                error = RmGetList(session, out var needed, ref count, null, out _);
                for (var attempt = 0; error == 234 && attempt < 4; attempt++)
                {
                    count = needed;
                    var entries = new ProcessInfo[count];
                    error = RmGetList(session, out needed, ref count, entries, out _);
                    if (error == 0)
                        return entries.Take((int)count).ToDictionary(x => x.Process.Id, x => new Candidate(x.Process.Id,
                            DateTime.FromFileTimeUtc(((long)x.Process.Start.dwHighDateTime << 32) | (uint)x.Process.Start.dwLowDateTime).Ticks,
                            x.Name ?? "", x.ApplicationType));
                }
                if (error != 0) throw new Win32Exception(error, "Unable to finish inspecting applications using installed resources.");
                return [];
            }
            finally { RmEndSession(session); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct UniqueProcess { public int Id; public System.Runtime.InteropServices.ComTypes.FILETIME Start; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
            public int ApplicationType;
            public uint AppStatus;
            public uint SessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint count, string[] files, uint applications, IntPtr processes, uint services, string[]? serviceNames);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? applications, out uint reasons);
        [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    }
}
