namespace HeirowInstaller.Core;

internal sealed class InstallTransaction : IDisposable
{
    private readonly List<Action> undo = [];
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileTransactionJournal? journal;
    private bool finished;
    private bool preserveRecovery;
    public string Root { get; }
    internal FileTransactionRecoveryContext? RecoveryContext { get; }

    public InstallTransaction(string root, FileTransactionRecoveryContext? recoveryContext = null)
    {
        Root = Path.GetFullPath(root);
        recoveryContext?.Validate();
        RecoveryContext = recoveryContext;
        PackageSafety.EnsureNoReparsePoints(Root);
        Directory.CreateDirectory(Root);
        if (recoveryContext is not null) journal = new(Root, recoveryContext);
    }
    public void AddRollback(Action action)
    {
        journal?.ExternalMetadata();
        undo.Add(action);
    }

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
        while (absent.TryPop(out var next)) { journal?.DirectoryCreated(next); Directory.CreateDirectory(next); directories.Add(next); }
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
            var afterHash = PackageSafety.HashFile(source);
            var afterSize = new FileInfo(source).Length;
            var operation = journal?.Prepare(destination, backup, temporary, afterHash, afterSize, source);
            undo.Add(() => { Restore(destination, backup, afterHash); if (operation is not null) journal!.Restored(operation); });
            FileTransactionJournal.CopyDurable(source, temporary);
            if (new FileInfo(temporary).Length != afterSize || !PackageSafety.HashEquals(PackageSafety.HashFile(temporary), afterHash))
                throw new IOException("A transaction source changed during copy.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void DeleteFile(string destination)
    {
        PackageSafety.EnsureNoReparsePoints(destination);
        if (!File.Exists(destination)) return;
        var backup = Backup(destination);
        var operation = journal?.Prepare(destination, backup, null, null, null);
        undo.Add(() => { Restore(destination, backup, null); if (operation is not null) journal!.Restored(operation); });
        File.Delete(destination);
    }

    private string? Backup(string destination)
    {
        if (!File.Exists(destination)) return null;
        var backup = Path.Combine(Root, "rollback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        FileTransactionJournal.CopyDurable(destination, backup);
        return backup;
    }

    private static void Restore(string destination, string? backup, string? afterHash)
    {
        PackageSafety.EnsureNoReparsePoints(destination);
        if (backup is null)
        {
            if (File.Exists(destination))
            {
                if (afterHash is null || !PackageSafety.HashEquals(PackageSafety.HashFile(destination), afterHash))
                    throw new IOException("A new transaction destination changed before rollback; recovery data was preserved.");
                File.Delete(destination);
            }
            return;
        }
        if (File.Exists(destination))
        {
            var actual = PackageSafety.HashFile(destination);
            if (PackageSafety.HashEquals(actual, PackageSafety.HashFile(backup))) return;
            if (afterHash is null || !PackageSafety.HashEquals(actual, afterHash))
                throw new IOException("A transaction destination changed before rollback; its original backup was preserved.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".hinst-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { FileTransactionJournal.CopyDurable(backup, temporary); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Commit() { journal?.Commit(); finished = true; undo.Clear(); }

    public void Rollback()
    {
        if (finished) return;
        var errors = new List<Exception>();
        foreach (var action in undo.AsEnumerable().Reverse())
            try { action(); } catch (Exception ex) { errors.Add(ex); }
        foreach (var directory in directories.OrderByDescending(x => x.Length))
            try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); } catch (Exception ex) { errors.Add(ex); }
        finished = true;
        if (errors.Count > 0) { preserveRecovery = true; throw new AggregateException("Installation rollback needs attention. Recovery files remain in " + Root, errors); }
        journal?.RollbackComplete();
    }

    public void Dispose()
    {
        try { if (!finished) Rollback(); }
        finally
        {
            journal?.Dispose();
            if (finished && !preserveRecovery)
                try
                {
                    PackageSafety.EnsureNoReparsePoints(Root);
                    if (RecoveryContext is not null) FileTransactionJournal.DeleteOwnedRoot(Root, RecoveryContext);
                    else Directory.Delete(Root, true);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
