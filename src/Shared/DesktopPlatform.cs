using System.Diagnostics;
using System.Collections.Concurrent;

namespace HeirowInstaller.Desktop;

internal enum PickerResult { Cancel, OK }

internal static class DesktopPlatform
{
    private static readonly BlockingCollection<Action> uiActions = new();
    internal static int RunOnMainThread(Func<Task<int>> work)
    {
        var task = work();
        while (!task.IsCompleted)
            if (uiActions.TryTake(out var action, 50)) action();
        return task.GetAwaiter().GetResult();
    }
    internal static void RunUi(Action work) => uiActions.Add(work);

    internal static void Initialize()
    {
#if WINDOWS
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
#endif
    }
    internal static void ShowError(string message, string title = "heirowInstaller")
    {
#if WINDOWS
        System.Windows.Forms.MessageBox.Show(message, title, System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
#else
        Console.Error.WriteLine(message);
#endif
    }

    internal static string? Pick(string title, string initial, bool folder, bool save)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsMacOS())
        {
            start = new("/usr/bin/osascript");
            var selection = folder ? "choose folder" : save ? "choose file name" : "choose file";
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("on run argv\ntry\nreturn POSIX path of (" + selection + " with prompt (item 1 of argv))\non error number -128\nreturn \"\"\nend try\nend run");
            start.ArgumentList.Add(title);
        }
        else
        {
            var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(path => Path.Combine(path, "zenity")).FirstOrDefault(File.Exists);
            if (executable is null) throw new InvalidOperationException("Install zenity to browse files, or type the path into the field.");
            start = new(executable);
            start.ArgumentList.Add("--file-selection"); start.ArgumentList.Add("--title=" + title);
            if (initial.Length > 0) start.ArgumentList.Add("--filename=" + initial);
            if (folder) start.ArgumentList.Add("--directory");
            if (save) { start.ArgumentList.Add("--save"); start.ArgumentList.Add("--confirm-overwrite"); }
        }
        start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("The file chooser did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(output, error);
        return process.ExitCode == 0 && output.Result.TrimEnd('\r', '\n') is { Length: > 0 } path ? path : null;
    }
}

internal class DesktopOpenFileDialog : IDisposable
{
    public string Title { get; set; } = "Choose file";
    public string Filter { get; set; } = "All files|*.*";
    public bool CheckFileExists { get; set; }
    public string FileName { get; set; } = "";
    protected virtual bool Save => false;
    public string DefaultExt { get; set; } = "";
    public bool AddExtension { get; set; }
    public PickerResult ShowDialog()
    {
#if WINDOWS
        using System.Windows.Forms.FileDialog picker = Save ? new System.Windows.Forms.SaveFileDialog { AddExtension = AddExtension, DefaultExt = DefaultExt } : new System.Windows.Forms.OpenFileDialog();
        picker.Title = Title; picker.Filter = Filter; picker.FileName = FileName; picker.CheckFileExists = CheckFileExists;
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return PickerResult.Cancel;
        FileName = picker.FileName;
#else
        var path = DesktopPlatform.Pick(Title, FileName, folder: false, save: Save);
        if (path is null) return PickerResult.Cancel;
        FileName = path;
        if (CheckFileExists && !File.Exists(path)) return PickerResult.Cancel;
#endif
        return PickerResult.OK;
    }
    public void Dispose() { }
}

internal sealed class DesktopSaveFileDialog : DesktopOpenFileDialog { protected override bool Save => true; }

internal sealed class DesktopFolderDialog : IDisposable
{
    public string Description { get; set; } = "Choose folder";
    public bool UseDescriptionForTitle { get; set; }
    public string InitialDirectory { get; set; } = "";
    public string SelectedPath { get; set; } = "";
    public PickerResult ShowDialog()
    {
#if WINDOWS
        using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = Description, UseDescriptionForTitle = UseDescriptionForTitle, InitialDirectory = InitialDirectory };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return PickerResult.Cancel;
        SelectedPath = picker.SelectedPath;
#else
        var path = DesktopPlatform.Pick(Description, InitialDirectory, folder: true, save: false);
        if (path is null) return PickerResult.Cancel;
        SelectedPath = path;
#endif
        return PickerResult.OK;
    }
    public void Dispose() { }
}
