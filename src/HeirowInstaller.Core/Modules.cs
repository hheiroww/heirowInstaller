using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>An explicitly registered installer extension. Modules must enlist rollback before every mutation and preserve original state in their returned snapshot.</summary>
public interface IInstallerModule
{
    string Id { get; }
    IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context);
    Task<ModuleSnapshot> InstallAsync(JsonElement configuration, InstallerModuleContext context, CancellationToken cancellationToken);
    Task UninstallAsync(ModuleSnapshot originalState, InstallerModuleContext context, CancellationToken cancellationToken);
}

public sealed class InstallerModuleContext
{
    private readonly InstallTransaction? transaction;
    public InstallPlan Plan { get; }
    public string InstallDirectory => Plan.InstallDirectory;
    public string Scope => Plan.Scope;
    public IReadOnlyList<string> Features => Plan.Features;
    public bool Isolated { get; }
    internal VerifiedModuleAuthority? Authority { get; private init; }
    internal bool ProductLockHeld { get; private init; }
    internal InstallTransaction Transaction => transaction ?? throw new InvalidOperationException("Planning cannot mutate state.");
    internal InstallerModuleContext(InstallPlan plan, InstallTransaction? transaction, bool isolated) { Plan = plan; this.transaction = transaction; Isolated = isolated; }
    internal static InstallerModuleContext ForVerifiedPackage(InstallPlan plan, InstallTransaction? transaction, PackageReader package,
        InstallOptions options, InstalledProduct? previous = null, bool productLockHeld = false) => new(plan, transaction, options.Isolated)
        {
            Authority = VerifiedModuleAuthority.Create(package, plan, options, previous), ProductLockHeld = productLockHeld
        };
    public bool IsFeatureSelected(string? feature) => InstallerEngine.IsSelected(feature, Features);
    public string Expand(string value) => InstallerEngine.Expand(value, InstallDirectory);
    public string ResolveTarget(string relativePath) => PackageSafety.UnderRoot(InstallDirectory, relativePath);
    public void EnlistRollback(Action rollback) => (transaction ?? throw new InvalidOperationException("Planning cannot mutate state.")).AddRollback(rollback);
    public void ReplaceFile(string relativeTarget, string verifiedSourcePath) => (transaction ?? throw new InvalidOperationException("Planning cannot mutate state.")).ReplaceFile(ResolveTarget(relativeTarget), verifiedSourcePath);
    public void DeleteFile(string relativeTarget) => (transaction ?? throw new InvalidOperationException("Planning cannot mutate state.")).DeleteFile(ResolveTarget(relativeTarget));
}
