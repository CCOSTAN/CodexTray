using CodexTray.Core;
using Microsoft.Win32;

namespace CodexTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HoverGracePeriod = TimeSpan.FromSeconds(5);
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _profileItem = new("Profile & usage ↗");
    private readonly ToolStripMenuItem _sessionItem = DisabledItem("Short window: loading...");
    private readonly ToolStripMenuItem _weeklyItem = DisabledItem("Weekly: loading...");
    private readonly ToolStripMenuItem _creditsItem = DisabledItem("Reset credits: loading...");
    private readonly ToolStripMenuItem _usageItem = DisabledItem("Token usage: loading...");
    private readonly ToolStripMenuItem _spendItem = new("Monthly API spend");
    private readonly SpendStore _spendStore = new();
    private readonly HttpClient _billingHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(20) };
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
    private CancellationTokenSource? _spendCancellation;
    private MonthlySpend? _lastSpend;
    private Icon? _currentIcon;
    private DateTimeOffset? _hoverLeftAt;
    private Point _lastTrayMousePosition = Point.Empty;
    private bool _refreshing;
    private bool _refreshAfterCurrent;
    private bool _shownFailure;
    private bool _keepHoverOpenForTest;

    public TrayApplicationContext(bool showTestNotification = false, bool showTestHover = false)
    {
        _notificationState = _notificationStateStore.Load();
        _lastSpend = _spendStore.LoadReport();
        ShowSpend(_spendStore.HasKey ? "Refreshing…" : "Connect via API spend settings");
        _menu.Items.AddRange([
            _profileItem,
            new ToolStripSeparator(),
            _sessionItem,
            _weeklyItem,
            _creditsItem,
            _usageItem,
            _spendItem,
            new ToolStripMenuItem("API spend settings…", null, (_, _) => ConfigureSpend()),
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
        _spendItem.Click += (_, _) => Brand.OpenUrl(Brand.SpendUrl);
        _refreshItem.Click += async (_, _) => await RefreshAsync(showFailureBalloon: true);
        _startupItem.Click += (_, _) => ToggleStartup();

        _currentIcon = TrayIconRenderer.Render(null);
        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
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
            if (showTestHover)
            {
                _keepHoverOpenForTest = true;
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
        _spendCancellation?.Cancel();
        _billingHttp.Dispose();
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
        _ = RefreshSpendAsync();
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
        _hoverForm.UpdateSnapshot(snapshot);
        ReplaceIcon(snapshot.DisplayWindow);
    }

    private void ConfigureSpend()
    {
        using var form = new SpendSettingsForm(_spendStore.HasKey);
        var result = form.ShowDialog();
        if (result is not (DialogResult.OK or DialogResult.No))
        {
            return;
        }
        _spendCancellation?.Cancel();
        try
        {
            _spendStore.SetKey(result == DialogResult.OK ? form.AdminKey : null);
            _lastSpend = null;
            ShowSpend(result == DialogResult.OK ? "Refreshing…" : "Connect via API spend settings");
            if (!_refreshGate.IsPaused)
            {
                _ = RefreshSpendAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show("Windows could not save the billing settings.", "Codex Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RefreshSpendAsync()
    {
        _spendCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _spendCancellation = cancellation;
        try
        {
            if (!_spendStore.HasKey)
            {
                ShowSpend("Connect via API spend settings");
                return;
            }

            ShowSpend("Refreshing…");
            var client = new OrganizationCostsClient(_billingHttp);
            var report = await client.FetchAsync(_spendStore.ReadKey(), DateTimeOffset.UtcNow, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _lastSpend = report;
            _spendStore.Save(report);
            ShowSpend(null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            var status = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                    => "Access denied. Check your organization admin key in API spend settings.",
                System.Security.Cryptography.CryptographicException => "Re-enter the key in API spend settings.",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                    => "Rate limited. Retrying on the next refresh.",
                _ => "Couldn’t refresh API spend. Use Refresh now to retry."
            };
            ShowSpend(status);
        }
        finally
        {
            if (ReferenceEquals(_spendCancellation, cancellation))
            {
                _spendCancellation = null;
            }
        }
    }

    private void ShowSpend(string? status)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastSpend is not null && !_lastSpend.IsCurrentMonth(now))
        {
            _lastSpend = null;
        }
        var heading = $"{now:MMMM} API spend";
        if (_lastSpend is { } report)
        {
            heading += $"  {MonthlySpend.Dollars(report.Total)}";
            var source = $"{report.Source} · {report.AsOf.ToLocalTime():MMM d, h:mm tt}";
            status = status is null ? source : $"{source} · {status}";
        }
        _spendItem.Text = heading + " ↗";
        _spendItem.ToolTipText = status;
        _hoverForm.UpdateSpend(heading, _lastSpend, status ?? "");
    }

    private void ApplyError(string message)
    {
        _updatedItem.Text = $"Refresh failed: {Shorten(message, 90)}";
        if (_lastSnapshot is null)
        {
            ReplaceIcon(null);
        }
    }

    private void ReplaceIcon(LimitWindow? window)
    {
        var next = TrayIconRenderer.Render(window);
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
        if (_refreshGate.IsPaused || _menu.Visible)
        {
            return;
        }

        _hoverLeftAt = null;
        _lastTrayMousePosition = Cursor.Position;
        if (_lastSnapshot is not null)
        {
            _hoverForm.UpdateSnapshot(_lastSnapshot);
        }
        if (!_hoverForm.Visible)
        {
            _hoverForm.ShowNear(Cursor.Position);
        }
    }

    private void HideHoverCardWhenPointerLeaves()
    {
        if (_keepHoverOpenForTest || !_hoverForm.Visible || _hoverForm.ContainsCursor || PointerStillOnTrayIcon())
        {
            _hoverLeftAt = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _hoverLeftAt ??= now;
        if (now - _hoverLeftAt.Value >= HoverGracePeriod)
        {
            _hoverForm.Hide();
            _hoverLeftAt = null;
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
            _spendCancellation?.Cancel();
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
