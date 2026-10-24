namespace HeirowInstaller.Core;

/// <summary>Builds an argument-list invocation without shell parsing, preserving authored MSI defaults when no destination override was requested.</summary>
public static class MsiAdapter
{
    public static IReadOnlyList<string> BuildArguments(string operation, string packagePath, string? productCode,
        LegacyMsiDefinition definition, InstallPlan plan, InstallOptions options)
    {
        var arguments = new List<string>();
        if (operation == "install")
        {
            arguments.AddRange(["/i", packagePath]);
            if (!plan.UseOriginalInstallDirectory) arguments.Add(definition.InstallDirectoryProperty + "=" + plan.InstallDirectory);
            var msiFeatures = plan.Features.Where(x => !string.Equals(x, plan.Manifest.UpdateCompanion?.Feature, StringComparison.OrdinalIgnoreCase)).ToList();
            if (msiFeatures.Count > 0) arguments.Add("ADDLOCAL=" + string.Join(',', msiFeatures));
            arguments.AddRange(definition.Arguments);
        }
        else
        {
            if (operation is not ("repair" or "uninstall")) throw new ArgumentException("Unknown MSI operation.", nameof(operation));
            if (!Guid.TryParse(productCode, out _)) throw new InvalidDataException("Repair and uninstall require the original MSI ProductCode.");
            var source = operation == "repair" && packagePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? packagePath : productCode!;
            arguments.AddRange([operation == "repair" ? "/fa" : "/x", source]);
        }
        if (options.Silent) arguments.Add("/qn");
        arguments.Add("/norestart");
        if (options.LogPath is { } log) { arguments.Add("/L*v"); arguments.Add(Path.GetFullPath(log)); }
        return arguments;
    }
}
