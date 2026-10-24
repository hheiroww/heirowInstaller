using System.Diagnostics;

namespace HeirowInstaller.Core;

/// <summary>Build-time settings for an explicitly selected WiX toolchain. WiX is never required on the installer recipient's computer.</summary>
public sealed class WixBuildOptions
{
    public string WixExecutable { get; set; } = "wix";
    public string DotNetExecutable { get; set; } = "dotnet";
    /// <summary>Optional full-framework MSBuild for legacy .wixproj projects. SDK projects otherwise use dotnet build.</summary>
    public string? MsBuildExecutable { get; set; }
    public string Configuration { get; set; } = "Release";
    public string Architecture { get; set; } = "x64";
    public string? WorkingDirectory { get; set; }
    public bool Restore { get; set; } = true;
    public List<string> Sources { get; set; } = [];
    public List<string> Extensions { get; set; } = [];
    public Dictionary<string, string> Defines { get; set; } = [];
    public List<string> BindPaths { get; set; } = [];
    public List<string> IncludeDirectories { get; set; } = [];
    public List<string> LocalizationFiles { get; set; } = [];
    /// <summary>Passed as individual arguments to wix build or the selected MSBuild host; no shell is used.</summary>
    public List<string> AdditionalArguments { get; set; } = [];
}

public sealed class WixBuildResult
{
    public string Backend { get; set; } = "WiX";
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public List<string> OutputFiles { get; set; } = [];
    public long ElapsedMilliseconds { get; set; }
}

public sealed class WixBuildException(int exitCode, string diagnostics) : IOException($"The WiX build backend exited with code {exitCode}.{System.Environment.NewLine}{diagnostics}")
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Compiles original WiX authoring through the official, separately installed compiler or project SDK, preserving its extension/custom-action pipeline.</summary>
public static class WixCompiler
{
    public static async Task<WixBuildResult> BuildAsync(string inputPath, string outputPath, WixBuildOptions? options = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        inputPath = Path.GetFullPath(inputPath); outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("WiX authoring input does not exist.", inputPath);
        var project = Path.GetExtension(inputPath).Equals(".wixproj", StringComparison.OrdinalIgnoreCase);
        if (!project && !Path.GetExtension(inputPath).Equals(".wxs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("compile requires a .wxs source or .wixproj project.");
        var kind = Path.GetExtension(outputPath).ToLowerInvariant() switch
        { ".msi" => "Package", ".msm" => "Module", ".msp" => "Patch", ".exe" => "Bundle", ".wixlib" => "Library", _ => throw new ArgumentException("WiX output must be .msi, .msm, .msp, .exe, or .wixlib.") };
        if (options.Architecture is not ("x86" or "x64" or "arm64")) throw new ArgumentException("WiX architecture must be x86, x64, or arm64.");
        if (inputPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Compiler output must differ from its input.");
        if (project && options.Sources.Count + options.Extensions.Count + options.BindPaths.Count + options.IncludeDirectories.Count + options.LocalizationFiles.Count > 0)
            throw new ArgumentException("For .wixproj builds, declare sources, PackageReference extensions, bind/include paths, and localization in the original project. AdditionalArguments accepts native MSBuild options.");
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Path.GetDirectoryName(inputPath)!);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("The compiler working directory does not exist.");
        PackageSafety.EnsureNoReparsePoints(outputPath);
        var parent = Path.GetDirectoryName(outputPath)!; Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".heirow-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var stagedOutput = Path.Combine(stage, Path.GetFileName(outputPath));
        var start = new ProcessStartInfo
        {
            FileName = project ? options.MsBuildExecutable ?? options.DotNetExecutable : options.WixExecutable,
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        void Add(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
        if (project)
        {
            if (options.MsBuildExecutable is null) Add("build", inputPath, "--nologo", "--configuration", options.Configuration);
            else Add(inputPath, "-nologo", "-p:Configuration=" + options.Configuration);
            if (options.Restore && options.MsBuildExecutable is not null) Add("-restore");
            if (!options.Restore && options.MsBuildExecutable is null) Add("--no-restore");
            Add("-p:OutputPath=" + stage + Path.DirectorySeparatorChar, "-p:OutputName=" + Path.GetFileNameWithoutExtension(outputPath),
                "-p:OutputType=" + kind, "-p:InstallerPlatform=" + options.Architecture);
            if (options.Defines.Count > 0) Add("-p:DefineConstants=" + string.Join("%3B", options.Defines.Select(x => x.Key + "=" + x.Value)));
        }
        else
        {
            Add("build", inputPath);
            foreach (var source in options.Sources) Add(Path.GetFullPath(source, workingDirectory));
            Add("-o", stagedOutput, "-arch", options.Architecture);
            foreach (var extension in options.Extensions) Add("-ext", extension);
            foreach (var define in options.Defines) Add("-d", define.Key + "=" + define.Value);
            foreach (var path in options.BindPaths) Add("-b", path);
            foreach (var path in options.IncludeDirectories) Add("-i", path);
            foreach (var path in options.LocalizationFiles) Add("-loc", path);
        }
        foreach (var argument in options.AdditionalArguments) start.ArgumentList.Add(argument);
        var diagnostics = new Queue<string>(); var gate = new object();
        async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lock (gate) { diagnostics.Enqueue(line); if (diagnostics.Count > 100) diagnostics.Dequeue(); }
                progress?.Report(new("compiler", line, 0, 0));
            }
        }
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("The WiX compiler backend did not start.");
            var output = DrainAsync(process.StandardOutput); var error = DrainAsync(process.StandardError);
            try { await process.WaitForExitAsync(cancellationToken); await Task.WhenAll(output, error); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
                throw;
            }
            if (process.ExitCode != 0) throw new WixBuildException(process.ExitCode, string.Join(System.Environment.NewLine, diagnostics));
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(stagedOutput)) throw new IOException("The backend reported success but did not produce the requested output: " + Path.GetFileName(outputPath));
            PackageSafety.EnsureNoReparsePoints(stagedOutput);
            var result = new WixBuildResult { InputPath = inputPath, OutputPath = outputPath, Sha256 = PackageSafety.HashFile(stagedOutput) };
            // Detached cabinets and symbols remain beside the compiled artifact; original project files are never rewritten.
            foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                PackageSafety.EnsureNoReparsePoints(file);
                var destination = Path.Combine(parent, Path.GetFileName(file)); PackageSafety.EnsureNoReparsePoints(destination);
                File.Move(file, destination, overwrite: true); result.OutputFiles.Add(destination);
            }
            result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            progress?.Report(new("complete", "WiX authoring compiled", 1, 1));
            return result;
        }
        finally
        {
            // Only this call's freshly generated build directory is removed.
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }
}
