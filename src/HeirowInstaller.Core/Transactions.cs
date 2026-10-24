namespace HeirowInstaller.Core;

internal sealed class InstallTransaction : IDisposable
{
    private readonly List<Action> undo = [];
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private bool finished;
    public string Root { get; }

    public InstallTransaction(string root)
    {
        Root = root;
        PackageSafety.EnsureNoReparsePoints(root);
        Directory.CreateDirectory(root);
    }
    public void AddRollback(Action action) => undo.Add(action);

    public void EnsureDirectory(string directory)
    {
        PackageSafety.EnsureNoReparsePoints(directory);
        var absent = new Stack<string>();
        var current = Path.GetFullPath(directory);
        while (!Directory.Exists(current))
        {
            if (File.Exists(current)) throw new IOException($"A file blocks a required directory: {current}");
            absent.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new IOException("Cannot resolve destination parent.");
        }
        while (absent.TryPop(out var next)) { Directory.CreateDirectory(next); directories.Add(next); }
    }

    public void ReplaceFile(string destination, string source)
    {
        PackageSafety.EnsureNoReparsePoints(destination);
        PackageSafety.EnsureNoReparsePoints(source);
        if (Directory.Exists(destination)) throw new IOException($"A directory blocks a payload file: {destination}");
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        var backup = Backup(destination);
        var temporary = destination + ".hinst-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            AddRollback(() => Restore(destination, backup));
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void DeleteFile(string destination)
    {
        PackageSafety.EnsureNoReparsePoints(destination);
        if (!File.Exists(destination)) return;
        var backup = Backup(destination);
        AddRollback(() => Restore(destination, backup));
        File.Delete(destination);
    }

    private string? Backup(string destination)
    {
        if (!File.Exists(destination)) return null;
        var backup = Path.Combine(Root, "rollback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(destination, backup);
        return backup;
    }

    private static void Restore(string destination, string? backup)
    {
        PackageSafety.EnsureNoReparsePoints(destination);
        if (backup is null) { if (File.Exists(destination)) File.Delete(destination); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".hinst-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(backup, temporary); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Commit() { finished = true; undo.Clear(); }

    public void Rollback()
    {
        if (finished) return;
        var errors = new List<Exception>();
        foreach (var action in undo.AsEnumerable().Reverse())
            try { action(); } catch (Exception ex) { errors.Add(ex); }
        foreach (var directory in directories.OrderByDescending(x => x.Length))
            try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); } catch (Exception ex) { errors.Add(ex); }
        finished = true;
        if (errors.Count > 0) throw new AggregateException("Installation rollback needs attention. Recovery files remain in " + Root, errors);
    }

    public void Dispose()
    {
        if (!finished) Rollback();
        if (finished)
            try { PackageSafety.EnsureNoReparsePoints(Root); Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
