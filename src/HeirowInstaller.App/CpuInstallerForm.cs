#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using HeirowInstaller.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace HeirowInstaller.App;

/// <summary>CPU-rendered Windows installation view; all operations remain in AppController.</summary>
internal sealed partial class CpuInstallerForm : Forms.Form, IInstallerDesktopHost
{
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private readonly AppArguments arguments;
    private readonly ConcurrentQueue<Action> uiActions = new();
    private readonly Dictionary<string, Forms.Control> controls = new(StringComparer.Ordinal);
    private readonly Forms.Label heading = new();
    private readonly Forms.Label subtitle = new();
    private readonly Forms.Label status = new();
    private readonly Forms.ProgressBar progress = new();
    private readonly Forms.Panel body = new();
    private readonly Forms.FlowLayoutPanel footer = new();
    private readonly Drawing.Font headingFont = new(Drawing.SystemFonts.DefaultFont.FontFamily, 18, Drawing.FontStyle.Bold);
    private readonly Drawing.Font sectionFont = new(Drawing.SystemFonts.DefaultFont, Drawing.FontStyle.Bold);
    private int drainScheduled;
    private int refreshScheduled;
    private int progressScheduled;
    private int activeCommands;
    private bool initialized;
    private bool closePending;
    private bool controllerExitRequested;
    private bool controllerDisposed;
    private bool disposingAfterOperation;
    private bool fontsDisposed;
    private string? viewError;

    internal AppController Controller { get; private set; }
    internal bool ClosePending => closePending;
    internal bool ControllerDisposed => controllerDisposed;
    internal Task? StartupTask { get; private set; }
    internal bool HasControl(string id) => controls.ContainsKey(id);
    internal Forms.Control Control(string id) => controls.TryGetValue(id, out var value)
        ? value : throw new InvalidOperationException("CPU wizard control is not visible: " + id);

    internal CpuInstallerForm(AppArguments arguments, bool startOnShown = true)
    {
        this.arguments = arguments;
        Text = arguments.PackagePath is not null || arguments.MaintenanceProductId is not null ? "Application setup" : "heirowInstaller";
        Name = "CpuInstallerForm";
        AutoScaleMode = Forms.AutoScaleMode.Dpi;
        ClientSize = new Drawing.Size(850, 680);
        MinimumSize = new Drawing.Size(660, 520);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        Font = Drawing.SystemFonts.DefaultFont;
        BackColor = Drawing.SystemColors.Window;
        ForeColor = Drawing.SystemColors.WindowText;
        DoubleBuffered = true;

        var header = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, Height = 112, Padding = new Forms.Padding(24, 15, 24, 8), ColumnCount = 1, RowCount = 2 };
        header.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 55));
        header.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 45));
        heading.Dock = Forms.DockStyle.Fill;
        heading.Font = headingFont;
        heading.AutoEllipsis = true;
        subtitle.Dock = Forms.DockStyle.Fill;
        subtitle.AutoEllipsis = true;
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(subtitle, 0, 1);

        var bottom = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 126, Padding = new Forms.Padding(24, 6, 24, 12), ColumnCount = 1, RowCount = 3 };
        bottom.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 34));
        bottom.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, 16));
        bottom.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100));
        status.Dock = Forms.DockStyle.Fill;
        status.AutoEllipsis = true;
        progress.Dock = Forms.DockStyle.Fill;
        progress.Maximum = 1000;
        footer.Dock = Forms.DockStyle.Fill;
        footer.FlowDirection = Forms.FlowDirection.RightToLeft;
        footer.WrapContents = false;
        footer.Padding = new Forms.Padding(0, 7, 0, 0);
        bottom.Controls.Add(status, 0, 0);
        bottom.Controls.Add(progress, 0, 1);
        bottom.Controls.Add(footer, 0, 2);
        body.Dock = Forms.DockStyle.Fill;
        body.AutoScroll = true;
        body.Padding = new Forms.Padding(24, 12, 24, 12);
        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(header);

        Controller = CreateController(arguments);
        FormClosing += HandleClosing;
        if (startOnShown) Shown += (_, _) => StartupTask ??= RunCommandAsync(() => InitializeAsync(loadPreferences: true));
        RenderState();
    }

    string IInstallerDesktopHost.Title { set => Text = value; }

    private AppController CreateController(AppArguments options)
    {
        var controller = new AppController(this, options);
        controller.Changed += ScheduleRefresh;
        controller.ProgressChanged += ScheduleProgress;
        return controller;
    }

    private void DisposeController()
    {
        if (controllerDisposed) return;
        Controller.Changed -= ScheduleRefresh;
        Controller.ProgressChanged -= ScheduleProgress;
        Controller.Dispose();
        controllerDisposed = true;
    }

    internal Task InitializeForTestsAsync() => RunCommandAsync(() => InitializeAsync(loadPreferences: false));

    private async Task InitializeAsync(bool loadPreferences)
    {
        if (initialized) return;
        initialized = true;
        // The compatibility hub has no publisher/workspace surface. It does
        // not need the full desktop's account/network initialization.
        if (loadPreferences && Controller.IsWizard) await Controller.InitializeDesktopAsync();
        if (closePending) return;
        if (arguments.MaintenanceOperation is not null) await Controller.InitializeMaintenanceWizardAsync();
        else if (arguments.PackagePath is not null) await Controller.OpenPackageAsync(arguments.PackagePath);
        ScheduleRefresh();
    }

    internal Task OpenPackageForTestsAsync(string path) => RunCommandAsync(() => OpenPackageAsync(path));

    private async Task OpenPackageAsync(string path)
    {
        if (Controller.Busy || closePending) return;
        // A picker-opened package receives the exact same wizard semantics as
        // an original package argument, with a fresh explicit Install action.
        if (arguments.PackagePath is null && !Controller.IsWizard)
        {
            DisposeController();
            controllerDisposed = false;
            Controller = CreateController(arguments with { PackagePath = path, Install = false });
        }
        await Controller.OpenPackageAsync(path);
    }

    private async Task RunCommandAsync(Func<Task> operation)
    {
        if (controllerDisposed || IsDisposed) return;
        Interlocked.Increment(ref activeCommands);
        try { await operation(); }
        catch (Exception error)
        {
            await OnUiAsync(() => { viewError = error.Message; ScheduleRefresh(); });
        }
        finally
        {
            Interlocked.Decrement(ref activeCommands);
            Enqueue(() => { ScheduleRefresh(); CloseWhenSettled(); });
        }
    }

    public void Enqueue(Action action)
    {
        if (IsDisposed || controllerDisposed) return;
        uiActions.Enqueue(action);
        ScheduleDrain();
    }

    public Task OnUiAsync(Action action)
    {
        if (IsDisposed || controllerDisposed) return Task.FromException(new ObjectDisposedException(nameof(CpuInstallerForm)));
        if (Environment.CurrentManagedThreadId == uiThread)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception error) { return Task.FromException(error); }
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => { try { action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } });
        return completion.Task;
    }

    private void ScheduleDrain()
    {
        if (!IsHandleCreated || IsDisposed || Interlocked.Exchange(ref drainScheduled, 1) != 0) return;
        try { BeginInvoke((Action)DrainQueue); }
        catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { Interlocked.Exchange(ref drainScheduled, 0); }
    }

    private void DrainQueue()
    {
        if (Environment.CurrentManagedThreadId != uiThread) throw new InvalidOperationException("CPU wizard dispatch requires its owning UI thread.");
        Interlocked.Exchange(ref drainScheduled, 0);
        var started = Stopwatch.GetTimestamp();
        for (var count = 0; count < 64 && uiActions.TryDequeue(out var action); count++)
        {
            if (IsDisposed || controllerDisposed) break;
            action();
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 4) break;
        }
        if (!uiActions.IsEmpty) ScheduleDrain();
    }

    internal void DrainForTests() => DrainQueue();
    internal void RequestCloseForTests() => RequestClose();

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref refreshScheduled, 1) != 0) return;
        Enqueue(() => { Interlocked.Exchange(ref refreshScheduled, 0); if (!IsDisposed) RenderState(); CloseWhenSettled(); });
    }

    private void ScheduleProgress()
    {
        if (Interlocked.Exchange(ref progressScheduled, 1) != 0) return;
        Enqueue(() => { Interlocked.Exchange(ref progressScheduled, 0); RefreshProgress(); });
    }

    public void Exit()
    {
        // Controller completion may request exit before its async command has
        // returned. Keep dispatch alive until that command has fully settled.
        controllerExitRequested = true;
        closePending = true;
        Enqueue(CloseWhenSettled);
    }

    private void RequestClose()
    {
        if (controllerDisposed || IsDisposed) return;
        closePending = true;
        if (Controller.Busy || activeCommands != 0) Controller.Cancel();
        ScheduleRefresh();
        CloseWhenSettled();
    }

    private void HandleClosing(object? sender, Forms.FormClosingEventArgs eventArgs)
    {
        if (disposingAfterOperation) return;
        if (Controller.Busy || activeCommands != 0)
        {
            eventArgs.Cancel = true;
            RequestClose();
        }
    }

    private void CloseWhenSettled()
    {
        if (!closePending || IsDisposed || activeCommands != 0 || (Controller.Busy && !controllerExitRequested)) return;
        disposingAfterOperation = true;
        Close();
    }

    protected override void OnFormClosed(Forms.FormClosedEventArgs e)
    {
        DisposeController();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !controllerDisposed)
        {
            if ((Controller.Busy && !(disposingAfterOperation && controllerExitRequested)) || activeCommands != 0)
            {
                RequestClose();
                return; // Never tear down dispatch while rollback/worker completion still needs it.
            }
            DisposeController();
        }
        base.Dispose(disposing);
        if (disposing && !fontsDisposed)
        {
            headingFont.Dispose();
            sectionFont.Dispose();
            fontsDisposed = true;
        }
    }

    private static void ClearOwnedControls(Forms.Control container)
    {
        foreach (var control in container.Controls.Cast<Forms.Control>().ToArray()) control.Dispose();
        container.Controls.Clear();
    }

    private Forms.TableLayoutPanel NewPage()
    {
        var page = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Top, AutoSize = true, AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Forms.Padding(0, 0, 8, 0) };
        page.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        body.Controls.Add(page);
        return page;
    }

    private T Remember<T>(string id, T control) where T : Forms.Control
    {
        control.Name = id;
        control.AccessibleName = control.Text;
        controls[id] = control;
        return control;
    }

    private static void AddRow(Forms.TableLayoutPanel page, Forms.Control control, int height = 0)
    {
        var row = page.RowCount++;
        page.RowStyles.Add(height > 0 ? new Forms.RowStyle(Forms.SizeType.Absolute, height) : new Forms.RowStyle(Forms.SizeType.AutoSize));
        control.Dock = Forms.DockStyle.Fill;
        control.Margin = new Forms.Padding(0, 0, 0, 12);
        page.Controls.Add(control, 0, row);
    }

    private Forms.Label Paragraph(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Drawing.Size(740, 0),
        Font = bold ? sectionFont : Drawing.SystemFonts.DefaultFont
    };

    private Forms.Button ActionButton(string id, string text, Action action, bool enabled = true, bool primary = false)
    {
        var button = Remember(id, new Forms.Button { Text = text, AutoSize = true, MinimumSize = new Drawing.Size(104, 34), Padding = new Forms.Padding(9, 3, 9, 3), Enabled = enabled && !closePending, UseVisualStyleBackColor = true });
        button.Click += (_, _) => action();
        footer.Controls.Add(button);
        if (primary) AcceptButton = button;
        return button;
    }

    private void RefreshProgress()
    {
        if (IsDisposed || controllerDisposed) return;
        status.Text = closePending && !controllerExitRequested ? "Canceling safely. Keep this window open while setup rolls back or finishes." : viewError ?? Controller.Status + " — " + Controller.StatusDetail;
        progress.Style = Controller.Busy && Controller.Progress <= 0 ? Forms.ProgressBarStyle.Marquee : Forms.ProgressBarStyle.Continuous;
        progress.MarqueeAnimationSpeed = Controller.Busy ? 30 : 0;
        progress.Value = (int)Math.Clamp(Controller.Progress * 1000, 0, 1000);
        if (HasControl("setup.progress.status")) Control("setup.progress.status").Text = Controller.Status;
        if (HasControl("setup.progress.detail")) Control("setup.progress.detail").Text = Controller.StatusDetail;
        if (HasControl("setup.activity")) Control("setup.activity").Text = ActivityText();
    }
}
#endif
