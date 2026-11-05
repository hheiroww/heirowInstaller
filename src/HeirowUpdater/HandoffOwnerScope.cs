using System.Globalization;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal static partial class UpdaterProgram
{
    private sealed class HandoffOwnerScope : IDisposable
    {
        private readonly InstallerUserContext? user;
        private readonly UpdaterOwnerHandshake.AuthenticatedOwner? handshake;
        private readonly IDisposable? ambient;
        private readonly int pid;
        private readonly long start;
        private HandoffOwnerScope(UpdaterOwnerHandshake.AuthenticatedOwner? handshake, int pid, long start)
        { this.handshake = handshake; user = handshake?.User; this.pid = pid; this.start = start; if (OperatingSystem.IsWindows()) ambient = user?.Enter(); }

        internal static async Task<HandoffOwnerScope> OpenAsync(Arguments args, string stage, CancellationToken token)
        {
            if (!OperatingSystem.IsWindows()) return new(null, 0, 0);
            // Older medium helpers do not borrow another user's token. Elevated helpers always require kernel proof.
            if (args.Get("owner-pipe") is null && !InstallerEngine.IsElevated) return new(null, 0, 0);
            var pid = int.Parse(args.Require("owner-pid"), CultureInfo.InvariantCulture);
            var start = long.Parse(args.Require("owner-start"), CultureInfo.InvariantCulture);
            var binding = new UpdaterOwnerHandshake.Binding(args.Require("product-id"), Path.GetFullPath(args.Require("target")),
                Path.GetFullPath(args.Require("state-root")), args.Require("scope"), args.Require("target-manifest-sha256"), stage);
            var handshake = await UpdaterOwnerHandshake.AuthenticateAsync(args.Get("owner-pipe"), args.Get("owner-nonce"), pid, start, binding, token);
            return new(handshake, pid, start);
        }

        internal void ValidateImage(UpdateCompanionConfiguration configuration, string runtime)
        {
            if (!OperatingSystem.IsWindows() || user is null) return;
            // The dedicated fixture never has administrative authority or another account's token.
            if (configuration.Isolated && !InstallerEngine.IsElevated && !user.IsDifferentAccount) return;
            if (BootstrapProcessTrust.IsTrustedUpdaterOwner(pid, start, configuration, runtime)) return;
            var image = user.ProcessImagePath;
            using var package = PackageReader.Open(InstallerEngine.ListInstalled(configuration.StateRoot).Single(item => item.Id == configuration.ProductId && item.Scope == configuration.Scope).PackagePath);
            package.VerifySignature(configuration.PublisherKeySha256, true);
            var relative = Path.GetRelativePath(configuration.InstallDirectory, image).Replace('\\', '/');
            var payload = package.Manifest.Payload.SingleOrDefault(item => item.Path.Equals(relative, InstallerPlatform.PathComparison));
            if (payload is null || new FileInfo(image).Length != payload.Size || !PackageSafety.HashEquals(PackageSafety.HashFile(image), payload.Sha256))
                throw new InvalidDataException("The initiating process is not this application's signed updater or owned executable.");
        }
        internal Task AcceptAsync(CancellationToken token) => handshake?.AcceptAsync(token) ?? Task.CompletedTask;

        internal async Task<InstallResult> ApplyUserAsync(InstallOptions options, Func<Task<InstallResult>> apply,
            Func<Task<bool>> administrativeRequirements)
        {
            if (!OperatingSystem.IsWindows() || user is null || options.Scope != "user" || !InstallerEngine.IsElevated) return await apply();
            var reboot = await administrativeRequirements();
            options.AdministratorRequirementsHandled = true;
            var result = await user.RunAsOriginalUserAsync(apply); result.RebootRequired |= reboot; return result;
        }
        public void Dispose() { ambient?.Dispose(); handshake?.Dispose(); }
    }
}
