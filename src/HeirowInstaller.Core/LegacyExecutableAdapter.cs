namespace HeirowInstaller.Core;

/// <summary>Preserves authored interactive and silent behavior while forwarding explicit backend overrides as individual process arguments.</summary>
public static class LegacyExecutableAdapter
{
    public static IReadOnlyList<string> BuildArguments(string operation, LegacyExecutableDefinition definition, string installDirectory, InstallOptions options)
    {
        var authored = operation switch
        {
            "install" => options.Silent && definition.SilentInstallArguments is not null ? definition.SilentInstallArguments : definition.InstallArguments,
            "repair" when definition.RepairArguments.Count > 0 => definition.RepairArguments,
            "uninstall" when definition.UninstallArguments.Count > 0 || definition.UninstallExecutable is not null => definition.UninstallArguments,
            _ => throw new NotSupportedException($"The legacy executable author has not configured {operation} arguments.")
        };
        var result = authored.Select(x => InstallerEngine.Expand(x, installDirectory)).ToList();
        if (operation == "install" && options.TargetDirectory is not null && definition.InstallDirectoryArgument is { Length: > 0 } directoryArgument)
            result.Add(InstallerEngine.Expand(directoryArgument, installDirectory));
        result.AddRange(options.LegacyArguments);
        return result;
    }
}
