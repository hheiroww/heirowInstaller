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

    private void StartCaptureEndpoint()
    {
        if (OperatingSystem.IsWindows() && (!arguments.Isolated || arguments.SmokeDirectory is not null))
            capturePipe = new(capturePresentation, RequestComposedCaptureAsync);
    }

    private void PrepareCapturePresentation()
    {
        // The installer is already opaque. Capture never changes the scene or enables a backdrop.
        opaqueDrawnGeneration = capturePresentation.PendingGeneration;
    }

    protected override void EndDraw()
    {
        // The graphics device's actual Present occurs in Game.EndDraw.
        base.EndDraw();
        if (opaqueDrawnGeneration != 0 && DwmFlush() == 0)
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

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
