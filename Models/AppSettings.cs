using System.IO;
using System.Text.Json;

namespace SpeedBar.Models;

public sealed class AppSettings
{
    public string UploadColor { get; set; } = "#FF81C784";
    public string DownloadColor { get; set; } = "#FF64B5F6";
    public string CpuColor { get; set; } = "#FFFFB74D";
    public string MemoryColor { get; set; } = "#FFCE93D8";
    public string BackgroundColor { get; set; } = "#CC202020";
    public bool TransparentBackground { get; set; }
    public double FontSize { get; set; } = 12;
    public int RefreshMilliseconds { get; set; } = 1000;
    public double OffsetX { get; set; } = 8;
    public double OffsetY { get; set; } = 4;
    public bool DockLeft { get; set; }
    public bool StartWithWindows { get; set; }

    private static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedBar");
    private static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(FolderPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
