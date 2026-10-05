using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using SpeedBar.Models;
using SpeedBar.Services;

namespace SpeedBar;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings.Clone();
        SetButton(UploadColorButton, Result.UploadColor);
        SetButton(DownloadColorButton, Result.DownloadColor);
        SetButton(CpuColorButton, Result.CpuColor);
        SetButton(MemoryColorButton, Result.MemoryColor);
        SetButton(BackgroundColorButton, Result.BackgroundColor);
        TransparentBackgroundCheck.IsChecked = Result.TransparentBackground;
        BackgroundColorButton.IsEnabled = !Result.TransparentBackground;
        FontSizeSlider.Value = Result.FontSize;
        StartupCheck.IsChecked = Result.StartWithWindows;
        var taskbarLeftAligned = TaskbarService.IsTaskbarLeftAligned();
        if (taskbarLeftAligned) Result.DockLeft = false;
        DockSideCombo.SelectedIndex = Result.DockLeft ? 0 : 1;
        DockSideCombo.IsEnabled = !taskbarLeftAligned;
        DockSideHint.Text = taskbarLeftAligned
            ? "系统任务栏靠左时固定靠右"
            : "系统任务栏居中时可自由选择";
        foreach (ComboBoxItem item in RefreshCombo.Items)
            if ((string)item.Tag == Result.RefreshMilliseconds.ToString()) item.IsSelected = true;
        if (RefreshCombo.SelectedIndex < 0)
        {
            // Preserve supported custom intervals from existing configuration files.
            var customInterval = new ComboBoxItem
            {
                Tag = Result.RefreshMilliseconds.ToString(),
                Content = $"{Result.RefreshMilliseconds} 毫秒（自定义）"
            };
            RefreshCombo.Items.Add(customInterval);
            RefreshCombo.SelectedItem = customInterval;
        }
    }

    private void ChooseColor(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        using var dialog = new Forms.ColorDialog { FullOpen = true };
        if (button.Tag is string current)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(current);
            dialog.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var selected = dialog.Color;
        SetButton(button, $"#{selected.A:X2}{selected.R:X2}{selected.G:X2}{selected.B:X2}");
    }

    private static void SetButton(System.Windows.Controls.Button button, string color)
    {
        button.Tag = color;
        button.Content = color;
        button.Background = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void TransparentBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (BackgroundColorButton is not null)
            BackgroundColorButton.IsEnabled = TransparentBackgroundCheck.IsChecked != true;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        Result.UploadColor = UploadColorButton.Tag as string ?? Result.UploadColor;
        Result.DownloadColor = DownloadColorButton.Tag as string ?? Result.DownloadColor;
        Result.CpuColor = CpuColorButton.Tag as string ?? Result.CpuColor;
        Result.MemoryColor = MemoryColorButton.Tag as string ?? Result.MemoryColor;
        Result.BackgroundColor = BackgroundColorButton.Tag as string ?? Result.BackgroundColor;
        Result.TransparentBackground = TransparentBackgroundCheck.IsChecked == true;
        Result.FontSize = FontSizeSlider.Value;
        if (RefreshCombo.SelectedItem is ComboBoxItem selected && int.TryParse(selected.Tag as string, out var interval))
            Result.RefreshMilliseconds = interval;
        Result.DockLeft = DockSideCombo.SelectedIndex == 0 && !TaskbarService.IsTaskbarLeftAligned();
        Result.StartWithWindows = StartupCheck.IsChecked == true;
        Result.Normalize();
        DialogResult = true;
    }
}
