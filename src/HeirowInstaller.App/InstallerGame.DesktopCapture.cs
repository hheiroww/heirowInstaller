using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace HeirowInstaller.App;

internal sealed partial class InstallerGame
{
    private const int MaximumCapturePixels = 16 * 1024 * 1024;
    private readonly DesktopCapturePresentation capturePresentation = new();
    private ComposedCapture? pendingComposedCapture;
    private DesktopCapturePipe? capturePipe;
    private long opaqueDrawnGeneration;
    private Color originalBackground;
    private bool originalBackdropEnabled;
    private bool renderingOpaque;

    private void StartCaptureEndpoint()
    {
        if (OperatingSystem.IsWindows() && !arguments.Isolated)
            capturePipe = new(capturePresentation, RequestComposedCaptureAsync);
    }

    private void PrepareCapturePresentation()
    {
        opaqueDrawnGeneration = 0;
        if (capturePipe is null) return;
        var (opaque, generation) = capturePresentation.RenderState;
        if (renderingOpaque != opaque)
        {
            var backdrop = manager.DesktopBackdrop.Settings;
            if (opaque)
            {
                originalBackground = MainScene.BackgroundColor;
                originalBackdropEnabled = backdrop.Enabled;
            }
            MainScene.BackgroundColor = opaque ? Theme.Background : originalBackground;
            backdrop.Enabled = !opaque && originalBackdropEnabled;
            renderingOpaque = opaque;
        }
        if (opaque) opaqueDrawnGeneration = generation;
    }

    protected override void EndDraw()
    {
        // The graphics device's actual Present occurs in Game.EndDraw.
        base.EndDraw();
        if (opaqueDrawnGeneration == 0) return;
        var backdropWindow = manager.DesktopBackdrop.NativeWindowHandle;
        if (backdropWindow == IntPtr.Zero ||
            !GetWindowDisplayAffinity(backdropWindow, out var affinity) || affinity != 0 ||
            DwmFlush() != 0)
            return;
        capturePresentation.AcknowledgeOpaque(opaqueDrawnGeneration);
    }

    private Task<byte[]?> RequestComposedCaptureAsync(CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ComposedCapture(completion, cancellation);
        Interlocked.Exchange(ref pendingComposedCapture, request)?.Completion.TrySetResult(null);
        return completion.Task.WaitAsync(cancellation);
    }

    private void SavePendingComposedCapture(GameTime gameTime)
    {
        // Read back offscreen before drawing the frame that will be presented.
        var capture = Interlocked.Exchange(ref pendingComposedCapture, null);
        if (capture is null || capture.Cancellation.IsCancellationRequested) return;
        try
        {
            var parameters = GraphicsDevice.PresentationParameters;
            if ((long)parameters.BackBufferWidth * parameters.BackBufferHeight > MaximumCapturePixels)
            {
                capture.Completion.TrySetResult(null);
                return;
            }
            using var png = new MemoryStream();
            WriteComposedScene(png, gameTime);
            capture.Completion.TrySetResult(png.Length <= DesktopCapturePipe.MaximumPngBytes ? png.ToArray() : null);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        {
            capture.Completion.TrySetResult(null);
        }
    }

    private void WriteComposedScene(Stream output, GameTime gameTime)
    {
        var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
        using var target = new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
        var previous = GraphicsDevice.GetRenderTargets();
        GraphicsDevice.SetRenderTarget(target);
        try
        {
            GraphicsDevice.Clear(Theme.Background);
            manager.Draw(gameTime);
        }
        finally
        {
            if (previous.Length == 0) GraphicsDevice.SetRenderTarget(null);
            else GraphicsDevice.SetRenderTargets(previous);
        }
        target.SaveAsPng(output, width, height);
    }

    private sealed record ComposedCapture(TaskCompletionSource<byte[]?> Completion, CancellationToken Cancellation);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
