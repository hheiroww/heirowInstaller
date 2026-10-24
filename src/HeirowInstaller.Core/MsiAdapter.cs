namespace HeirowInstaller.Core;

/// <summary>Builds logical arguments and encodes Windows Installer's own command-line grammar without a shell.</summary>
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

    public static string SerializeArguments(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(argument =>
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0')) throw new ArgumentException("MSI arguments cannot contain a null character.", nameof(arguments));
        var equals = argument.IndexOf('=');
        if (equals > 0 && (char.IsAsciiLetter(argument[0]) || argument[0] == '_') &&
            argument.AsSpan(0, equals).ToArray().All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            return argument[..(equals + 1)] + Quoted(argument[(equals + 1)..]);
        return argument.Length == 0 || argument.Any(character => char.IsWhiteSpace(character) || character == '"') ? Quoted(argument) : argument;
    }));

    // msiexec does not use the C runtime argv parser. Quote the VALUE of a
    // property, double literal quotes, and leave trailing backslashes intact.
    private static string Quoted(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
