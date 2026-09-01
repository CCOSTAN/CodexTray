using CodexTray.Core;
using Microsoft.Win32;

namespace CodexTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HoverGracePeriod = TimeSpan.FromSeconds(8);
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _profileItem = new("Profile & usage ↗");
    private readonly ToolStripMenuItem _sessionItem = DisabledItem("Short window: loading...");
    private readonly ToolStripMenuItem _weeklyItem = DisabledItem("Weekly: loading...");
    private readonly ToolStripMenuItem _creditsItem = DisabledItem("Reset credits: loading...");
    private readonly ToolStripMenuItem _usageItem = DisabledItem("Token usage: loading...");
    private readonly ToolStripMenuItem _updatedItem = DisabledItem("Not refreshed yet");
    private readonly ToolStripMenuItem _refreshItem = new("Refresh now");
    private readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = false };
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _initialTimer;
    private readonly System.Windows.Forms.Timer _hoverTimer;
    private readonly NotificationStateStore _notificationStateStore = new();
    private readonly UsageHoverForm _hoverForm = new();
    private readonly Control _dispatcher = new();
    private readonly SessionRefreshGate _refreshGate = new();
    private CodexAppServerClient? _client;
    private CodexSnapshot? _lastSnapshot;
    private NotificationState? _notificationState;
    private ResetNotificationForm? _notificationForm;
    private CancellationTokenSource? _refreshCancellation;
    private Icon? _currentIcon;
    private DateTimeOffset _lastTrayMouseMove = DateTimeOffset.MinValue;
    private Point _lastTrayMousePosition = Point.Empty;
    private bool _refreshing;
    private bool _refreshAfterCurrent;
    private bool _shownFailure;
    private bool _keepHoverOpenForTest;

    public TrayApplicationContext(bool showTestNotification = false, bool showTestHover = false)
    {
        _notificationState = _notificationStateStore.Load();
        _menu.Items.AddRange([
            _profileItem,
            new ToolStripSeparator(),
            _sessionItem,
            _weeklyItem,
            _creditsItem,
            _usageItem,
            new ToolStripSeparator(),
            _updatedItem,
            _refreshItem,
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread())
        ]);
        _menu.Opening += (_, _) =>
        {
            _hoverForm.Hide();
            SyncStartupMenu();
        };
        _profileItem.Click += (_, _) => Brand.OpenUrl(Brand.CodexUsageUrl);
        _refreshItem.Click += async (_, _) => await RefreshAsync(showFailureBalloon: true);
        _startupItem.Click += (_, _) => ToggleStartup();

        _currentIcon = TrayIconRenderer.Render(null);
        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "Codex: loading usage",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseMove += (_, _) => ShowHoverCard();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = (int)RefreshInterval.TotalMilliseconds };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(showFailureBalloon: false);
        _refreshTimer.Start();

        _hoverTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _hoverTimer.Tick += (_, _) => HideHoverCardWhenPointerLeaves();
        _hoverTimer.Start();

        _initialTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _initialTimer.Tick += async (_, _) =>
        {
            _initialTimer.Stop();
            await RefreshAsync(showFailureBalloon: true);
            if (showTestNotification)
            {
                ShowTestNotification();
            }
            if (showTestHover && _lastSnapshot is not null)
            {
                _keepHoverOpenForTest = true;
                _hoverForm.UpdateSnapshot(_lastSnapshot);
                var workArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
                _hoverForm.ShowNear(new Point(workArea.Right - 10, workArea.Bottom - 10));
            }
        };
        _initialTimer.Start();

        _ = _dispatcher.Handle;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _refreshCancellation?.Cancel();
        _client?.Dispose();
        _initialTimer.Stop();
        _refreshTimer.Stop();
        _hoverTimer.Stop();
        _hoverForm.Close();
        _notificationForm?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _initialTimer.Dispose();
        _refreshTimer.Dispose();
        _hoverTimer.Dispose();
        _hoverForm.Dispose();
        _notificationForm?.Dispose();
        _refreshCancellation?.Dispose();
        _dispatcher.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }

    private async Task RefreshAsync(bool showFailureBalloon)
    {
        if (_refreshing || _refreshGate.IsPaused)
        {
            return;
        }

        _refreshing = true;
        _refreshItem.Enabled = false;
        _updatedItem.Text = "Refreshing...";
        using var refreshCancellation = new CancellationTokenSource();
        _refreshCancellation = refreshCancellation;
        try
        {
            _client ??= new CodexAppServerClient(CodexLocator.Locate());
            var snapshot = await _client.FetchAsync(refreshCancellation.Token);
            var notifications = UsageNotificationDetector.Detect(_notificationState, snapshot);
            _lastSnapshot = snapshot;
            _notificationState = NotificationState.FromSnapshot(snapshot);
            _notificationStateStore.Save(_notificationState);
            _shownFailure = false;
            ApplySnapshot(snapshot);
            ShowUsageNotifications(notifications);
        }
        catch (OperationCanceledException) when (_refreshGate.IsPaused)
        {
            _updatedItem.Text = "Refresh paused while Windows is locked";
        }
        catch (Exception ex)
        {
            ApplyError(ex.Message);
            if (showFailureBalloon && !_shownFailure)
            {
                _shownFailure = true;
                _notifyIcon.BalloonTipTitle = "Codex Tray could not refresh";
                _notifyIcon.BalloonTipText = Shorten(ex.Message, 220);
                _notifyIcon.ShowBalloonTip(5000);
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, refreshCancellation))
            {
                _refreshCancellation = null;
            }
            _refreshing = false;
            _refreshItem.Enabled = !_refreshGate.IsPaused;
            if (_refreshAfterCurrent && !_refreshGate.IsPaused)
            {
                _refreshAfterCurrent = false;
                _ = RefreshAsync(showFailureBalloon: false);
            }
        }
    }

    private void ApplySnapshot(CodexSnapshot snapshot)
    {
        _profileItem.Text = string.IsNullOrWhiteSpace(snapshot.PlanType)
            ? "Profile & usage ↗"
            : $"Profile & usage · {snapshot.PlanType} ↗";
        _profileItem.ToolTipText = snapshot.AccountDisplay ?? "Open official Codex usage analytics";
        _sessionItem.Visible = snapshot.SessionWindow is not null;
        _sessionItem.Text = DisplayFormatter.WindowLine("Short window", snapshot.SessionWindow, DateTimeOffset.Now);
        _weeklyItem.Text = DisplayFormatter.WindowLine("Weekly", snapshot.WeeklyWindow, DateTimeOffset.Now);
        _creditsItem.Text = DisplayFormatter.CreditsLine(
            snapshot.AvailableResetCredits,
            snapshot.EarliestResetCreditExpiresAt,
            DateTimeOffset.Now);
        _usageItem.Text = DisplayFormatter.UsageLine(snapshot.Usage);
        _updatedItem.Text = $"Updated {snapshot.FetchedAt.LocalDateTime:t}";
        _notifyIcon.Text = DisplayFormatter.BuildTooltip(snapshot);
        _hoverForm.UpdateSnapshot(snapshot);
        ReplaceIcon(snapshot.DisplayWindow?.RemainingPercent);
    }

    private void ApplyError(string message)
    {
        _updatedItem.Text = $"Refresh failed: {Shorten(message, 90)}";
        _notifyIcon.Text = _lastSnapshot is null
            ? "Codex: refresh failed"
            : DisplayFormatter.BuildTooltip(_lastSnapshot) + " (stale)";
        if (_lastSnapshot is null)
        {
            ReplaceIcon(null);
        }
    }

    private void ReplaceIcon(double? remainingPercent)
    {
        var next = TrayIconRenderer.Render(remainingPercent);
        _notifyIcon.Icon = next;
        var previous = _currentIcon;
        _currentIcon = next;
        previous?.Dispose();
    }

    private void ShowUsageNotifications(IReadOnlyList<UsageNotification> notifications)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = notifications.Count == 1
            ? notifications[0].Title
            : "Codex usage updated";
        _notifyIcon.BalloonTipText = string.Join(Environment.NewLine, notifications.Select(item => item.Message));
        _notifyIcon.ShowBalloonTip(8000);

        _notificationForm?.Close();
        var form = new ResetNotificationForm(notifications);
        _notificationForm = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_notificationForm, form))
            {
                _notificationForm = null;
            }
        };
        form.Show();
        form.Activate();
    }

    private void ShowTestNotification() => ShowUsageNotifications([
        new UsageNotification(
            "Codex weekly limit reset",
            "Your weekly Codex window reset. 100% is available.")
    ]);

    private void ShowHoverCard()
    {
        if (_refreshGate.IsPaused || _lastSnapshot is null || _menu.Visible)
        {
            return;
        }

        _lastTrayMouseMove = DateTimeOffset.UtcNow;
        _lastTrayMousePosition = Cursor.Position;
        _hoverForm.UpdateSnapshot(_lastSnapshot);
        if (!_hoverForm.Visible)
        {
            _hoverForm.ShowNear(Cursor.Position);
        }
    }

    private void HideHoverCardWhenPointerLeaves()
    {
        if (_keepHoverOpenForTest || !_hoverForm.Visible || _hoverForm.ContainsCursor || PointerStillOnTrayIcon())
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastTrayMouseMove > HoverGracePeriod)
        {
            _hoverForm.Hide();
        }
    }

    private bool PointerStillOnTrayIcon()
    {
        var cursor = Cursor.Position;
        return Math.Abs(cursor.X - _lastTrayMousePosition.X) <= 28
               && Math.Abs(cursor.Y - _lastTrayMousePosition.Y) <= 28;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        bool? locked = args.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => null
        };
        if (locked is null || _dispatcher.IsDisposed || !_dispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke((Action)(() => SetSessionLocked(locked.Value)));
        }
        catch (InvalidOperationException)
        {
            // The app is already closing.
        }
    }

    private void SetSessionLocked(bool locked)
    {
        var changed = locked ? _refreshGate.Pause() : _refreshGate.Resume();
        if (!changed)
        {
            return;
        }

        if (locked)
        {
            _refreshTimer.Stop();
            _refreshCancellation?.Cancel();
            _refreshItem.Enabled = false;
            _updatedItem.Text = "Refresh paused while Windows is locked";
            _hoverForm.Hide();
            return;
        }

        _refreshTimer.Start();
        _refreshItem.Enabled = true;
        if (_refreshing)
        {
            _refreshAfterCurrent = true;
        }
        else
        {
            _ = RefreshAsync(showFailureBalloon: false);
        }
    }

    private void SyncStartupMenu()
    {
        try
        {
            _startupItem.Checked = StartupRegistration.IsEnabled();
        }
        catch
        {
            _startupItem.Checked = false;
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
            SyncStartupMenu();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Codex Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static ToolStripMenuItem DisabledItem(string text) => new(text) { Enabled = false };

    private static string Shorten(string value, int length) =>
        value.Length <= length ? value : value[..Math.Max(0, length - 3)] + "...";
}
