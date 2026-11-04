using System.Reflection;
using Microsoft.Xna.Framework.Graphics;

namespace HeirowInstaller.App;

/// <summary>Chooses the Windows CPU presentation path only for graphics startup failures.</summary>
internal static class GraphicsStartup
{
    /// <summary>
    /// Runs one presentation path. The hardware callback owns and disposes its Game before
    /// unwinding; contentStarted must become true at the first line of LoadContent.
    /// </summary>
    internal static void Run(Action hardware, Action software, Func<bool> contentStarted,
        bool windows, bool forceSoftware = false)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(software);
        ArgumentNullException.ThrowIfNull(contentStarted);

        if (forceSoftware)
        {
            if (!windows)
                throw new PlatformNotSupportedException("CPU presentation is available on Windows only.");
            software();
            return;
        }

        try
        {
            hardware();
        }
        catch (Exception error) when (ShouldUseSoftwareFallback(error, contentStarted(), windows))
        {
            // Never retry an installation, content load, or software failure. Only the
            // presentation callback is replaced, and it is invoked at most once.
            software();
        }
    }

    internal static bool ShouldUseSoftwareFallback(Exception error, bool contentStarted, bool windows)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!windows || contentStarted) return false;

        // Runtime/reflection wrappers can surround a genuine MonoGame initialization
        // failure. Arbitrary application wrappers and aggregates are not equivalent.
        for (var depth = 0; depth < 8; depth++)
        {
            if (error is NoSuitableGraphicsDeviceException)
                return IsKnownInitializationMessage(error.Message);

            if (error is not TypeInitializationException and not TargetInvocationException ||
                error.InnerException is not { } inner)
                return false;
            error = inner;
        }
        return false;
    }

    private static bool IsKnownInitializationMessage(string message) => message is
        "Failed to create graphics device!" or
        "Failed to initialize SDL platform!" or
        "Failed to initialize SDL window!" or
        "Failed to initialize graphics system!" or
        "No graphics adapters were found!";
}
