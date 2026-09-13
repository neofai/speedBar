using Microsoft.Win32;

namespace SpeedBar.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SpeedBar";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (enabled)
            key?.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else
            key?.DeleteValue(AppName, throwOnMissingValue: false);
    }
}
