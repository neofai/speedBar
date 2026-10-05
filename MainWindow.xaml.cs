using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using SpeedBar.Models;
using SpeedBar.Services;

namespace SpeedBar;

public partial class MainWindow : Window
{
    private readonly SystemMetricsService _metrics = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private TaskbarController? _taskbar;
    private SettingsWindow? _settingsDialog;
    private AppSettings _settings;
    private bool _reallyClosing;
    private bool _resourcesReleased;
    private bool _sampling;
    private bool _resetSample = true;
    private int _sampleGeneration;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        ApplySettings();
        using var iconStream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/speedbar.ico"))?.Stream;
        using var sourceIcon = iconStream is null ? null : new System.Drawing.Icon(iconStream);
        _icon = (System.Drawing.Icon)(sourceIcon ?? System.Drawing.SystemIcons.Application).Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        _pauseItem = new Forms.ToolStripMenuItem("暂停显示（游戏模式）", null, (_, _) => TogglePaused());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("重新贴靠任务栏", null, (_, _) =>
        {
            _taskbar?.SetPaused(false);
            _pauseItem.Checked = false;
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => CloseApplication());
        _tray = new Forms.NotifyIcon { Text = "SpeedBar v2", Icon = _icon, Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += OnTrayDoubleClick;
        _timer.Tick += OnMetricsTick;
        Loaded += OnLoaded;
        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseRightButtonUp += OnMouseRightButtonUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_taskbar is not null) return;
        _taskbar = new TaskbarController(this, () => _settings);
        _taskbar.DisplayActiveChanged += OnDisplayActiveChanged;
    }

    private void OnDisplayActiveChanged(bool active)
    {
        _sampleGeneration++;
        if (!active)
        {
            _timer.Stop();
            return;
        }
        _resetSample = true;
        RefreshMetrics();
        _timer.Start();
    }

    private void OnMetricsTick(object? sender, EventArgs e) => RefreshMetrics();

    private async void RefreshMetrics()
    {
        if (_sampling || _reallyClosing || _taskbar?.IsDisplayActive != true) return;
        _sampling = true;
        var generation = _sampleGeneration;
        var reset = _resetSample;
        _resetSample = false;
        try
        {
            // Network adapters can block in a driver; keep this off the dispatcher.
            var value = await Task.Run(() =>
            {
                if (reset) _metrics.Reset();
                return _metrics.Sample();
            });
            if (_reallyClosing || generation != _sampleGeneration || _taskbar?.IsDisplayActive != true) return;
            SetText(UploadText, $"↑ {FormatSpeed(value.UploadBytesPerSecond)}");
            SetText(DownloadText, $"↓ {FormatSpeed(value.DownloadBytesPerSecond)}");
            SetText(CpuText, $"CPU {value.CpuPercent:0}%");
            SetText(MemoryText, $"RAM {value.MemoryPercent:0}%");
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        finally { _sampling = false; }
    }

    private static void SetText(System.Windows.Controls.TextBlock text, string value)
    {
        if (text.Text != value) text.Text = value;
    }

    private static string FormatSpeed(double bytes)
    {
        string[] units = ["B/s", "K/s", "M/s", "G/s"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes >= 100 || unit == 0 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.0} {units[unit]}";
    }

    private void ApplySettings()
    {
        _settings.Normalize();
        RootBorder.Background = _settings.TransparentBackground
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0))
            : Brush(_settings.BackgroundColor);
        UploadText.Foreground = Brush(_settings.UploadColor);
        DownloadText.Foreground = Brush(_settings.DownloadColor);
        CpuText.Foreground = Brush(_settings.CpuColor);
        MemoryText.Foreground = Brush(_settings.MemoryColor);
        foreach (var text in new[] { UploadText, DownloadText, CpuText, MemoryText }) text.FontSize = _settings.FontSize;
        NetworkColumn.Width = new GridLength(86 * _settings.FontSize / 12);
        SystemColumn.Width = new GridLength(64 * _settings.FontSize / 12);
        _timer.Interval = TimeSpan.FromMilliseconds(_settings.RefreshMilliseconds);
    }

    private static System.Windows.Media.Brush Brush(string color) =>
        new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    private void OnTrayDoubleClick(object? sender, EventArgs e) => OpenSettings();
    private void OpenSettings()
    {
        if (_reallyClosing) return;
        if (_settingsDialog is not null) { _settingsDialog.Activate(); return; }
        try
        {
            _settingsDialog = new SettingsWindow(_settings);
            if (_settingsDialog.ShowDialog() != true) return;
            var settings = _settingsDialog.Result;
            settings.Save();
            _settings = settings;
            ApplySettings();
            _taskbar?.InvalidateLayout();
            StartupService.SetEnabled(settings.StartWithWindows);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show($"无法完整应用设置：{exception.Message}", "SpeedBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _settingsDialog = null; }
    }

    private void TogglePaused()
    {
        if (_taskbar is null) return;
        _taskbar.SetPaused(!_taskbar.IsPaused);
        _pauseItem.Checked = _taskbar.IsPaused;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) OpenSettings();
        e.Handled = true;
    }
    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _tray.ContextMenuStrip?.Show(Forms.Cursor.Position);
        e.Handled = true;
    }

    private void CloseApplication()
    {
        _reallyClosing = true;
        _settingsDialog?.Close();
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            _taskbar?.SetPaused(true);
            _pauseItem.Checked = true;
            return;
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        ReleaseResources();
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }
    internal void ReleaseResources()
    {
        if (_resourcesReleased) return;
        _resourcesReleased = true;
        _reallyClosing = true;
        _sampleGeneration++;
        _timer.Stop();
        _timer.Tick -= OnMetricsTick;
        if (_taskbar is not null)
        {
            _taskbar.DisplayActiveChanged -= OnDisplayActiveChanged;
            _taskbar.Dispose();
        }
        _tray.Visible = false;
        _tray.DoubleClick -= OnTrayDoubleClick;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _icon.Dispose();
    }
}
