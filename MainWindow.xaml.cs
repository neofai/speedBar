using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using SpeedBar.Models;
using SpeedBar.Services;

namespace SpeedBar;

public partial class MainWindow : Window
{
    private readonly SystemMetricsService _metrics = new();
    private readonly TaskbarLayoutService _taskbarLayout = new();
    private readonly TaskbarHitTarget _hitTarget;
    private readonly DispatcherTimer _timer = new();
    private readonly Forms.NotifyIcon _tray;
    private IReadOnlyList<HorizontalRange>? _occupiedRanges;
    private AppSettings _settings;
    private bool _reallyClosing;
    private DateTime _nextEmbedAttempt = DateTime.MinValue;
    private DateTime _nextLayoutScan = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        _hitTarget = new TaskbarHitTarget
        {
            LeftButtonDown = clickCount =>
            {
                if (clickCount >= 2) OpenSettings();
            },
            RightButtonUp = ShowContextMenu
        };
        _settings = AppSettings.Load();
        ApplySettings();

        var iconStream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/speedbar.ico"))?.Stream;
        _tray = new Forms.NotifyIcon
        {
            Text = "SpeedBar",
            Icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream),
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _timer.Tick += (_, _) => RefreshMetrics();
        _timer.Start();
        Loaded += OnLoaded;
        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseRightButtonUp += OnMouseRightButtonUp;
        SizeChanged += (_, _) =>
        {
            if (TaskbarService.IsEmbedded(this))
                Dispatcher.BeginInvoke(() =>
                {
                    TaskbarService.PlaceEmbedded(
                        this,
                        _settings.OffsetX,
                        _settings.DockLeft,
                        _occupiedRanges);
                    _hitTarget.UpdateFromWindow(this);
                });
        };
        SystemEvents.DisplaySettingsChanged += OnSystemDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("重新贴靠任务栏", null, (_, _) => Dispatcher.Invoke(ResetPosition));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(CloseApplication));
        menu.Opened += (_, _) => SetWindowPos(
            menu.Handle,
            new IntPtr(-1), // HWND_TOPMOST: Explorer's taskbar must not cover the menu
            0,
            0,
            0,
            0,
            0x0001 | 0x0002 | 0x0010); // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        return menu;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TaskbarService.PrepareWindow(this);
        PlaceWindow();
        RefreshMetrics();
    }

    private void RefreshMetrics()
    {
        var value = _metrics.Sample();
        UploadText.Text = $"↑ {FormatSpeed(value.UploadBytesPerSecond)}";
        DownloadText.Text = $"↓ {FormatSpeed(value.DownloadBytesPerSecond)}";
        CpuText.Text = $"CPU {value.CpuPercent:0}%";
        MemoryText.Text = $"RAM {value.MemoryPercent:0}%";
        if (!TaskbarService.IsEmbedded(this) && DateTime.UtcNow >= _nextEmbedAttempt)
        {
            _nextEmbedAttempt = DateTime.UtcNow.AddSeconds(5);
            PlaceWindow();
        }
        UpdateAutomaticPosition();
    }

    private static string FormatSpeed(double bytes)
    {
        string[] units = ["B/s", "K/s", "M/s", "G/s"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return bytes >= 100 || unit == 0 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.0} {units[unit]}";
    }

    private void ApplySettings()
    {
        RootBorder.Background = _settings.TransparentBackground
            ? System.Windows.Media.Brushes.Transparent
            : Brush(_settings.BackgroundColor);
        UploadText.Foreground = Brush(_settings.UploadColor);
        DownloadText.Foreground = Brush(_settings.DownloadColor);
        CpuText.Foreground = Brush(_settings.CpuColor);
        MemoryText.Foreground = Brush(_settings.MemoryColor);
        foreach (var text in new[] { UploadText, DownloadText, CpuText, MemoryText })
            text.FontSize = _settings.FontSize;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.RefreshMilliseconds, 250, 5000));
    }

    private static System.Windows.Media.Brush Brush(string color)
    {
        try
        {
            return new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }
        catch { return System.Windows.Media.Brushes.White; }
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings);
        if (dialog.ShowDialog() != true) return;
        _settings = dialog.Result;
        _settings.Save();
        StartupService.SetEnabled(_settings.StartWithWindows);
        ApplySettings();
        PlaceWindow();
    }

    private void PlaceWindow()
    {
        if (TaskbarService.TryEmbedIntoTaskbar(
                this,
                _settings.OffsetX,
                _settings.DockLeft,
                _occupiedRanges))
        {
            _hitTarget.UpdateFromWindow(this);
            return;
        }

        _hitTarget.Hide();
        TaskbarService.UseOwnedOverlay(this);
        TaskbarService.PlaceNearTaskbar(
            this,
            _settings.OffsetX,
            _settings.OffsetY,
            _settings.DockLeft);
    }

    private void ResetPosition()
    {
        _occupiedRanges = null;
        _nextLayoutScan = DateTime.MinValue;
        PlaceWindow();
    }

    private async void UpdateAutomaticPosition()
    {
        if (!TaskbarService.IsEmbedded(this) ||
            DateTime.UtcNow < _nextLayoutScan)
            return;

        _nextLayoutScan = DateTime.UtcNow.AddMilliseconds(500);
        var ranges = await _taskbarLayout.ScanOccupiedRangesAsync();
        if (ranges is null || _reallyClosing) return;
        _occupiedRanges = ranges;
        TaskbarService.PlaceEmbedded(
            this,
            _settings.OffsetX,
            _settings.DockLeft,
            _occupiedRanges);
        _hitTarget.UpdateFromWindow(this);
    }

    private void OnSystemDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ResetPosition);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(ResetPosition);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) OpenSettings();
        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowContextMenu();
        e.Handled = true;
    }

    private void ShowContextMenu() =>
        _tray.ContextMenuStrip?.Show(Forms.Cursor.Position);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private void CloseApplication()
    {
        _reallyClosing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _timer.Stop();
        _hitTarget.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _tray.Visible = false;
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
        base.OnClosing(e);
    }
}
