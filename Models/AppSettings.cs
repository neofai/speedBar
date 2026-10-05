using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeedBar.Models;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

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
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
                settings.Normalize();
                return settings;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return new();
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Normalize();
        return copy;
    }

    public void Normalize()
    {
        UploadColor = NormalizeColor(UploadColor, "#FF81C784");
        DownloadColor = NormalizeColor(DownloadColor, "#FF64B5F6");
        CpuColor = NormalizeColor(CpuColor, "#FFFFB74D");
        MemoryColor = NormalizeColor(MemoryColor, "#FFCE93D8");
        BackgroundColor = NormalizeColor(BackgroundColor, "#CC202020");
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 10, 20) : 12;
        RefreshMilliseconds = Math.Clamp(RefreshMilliseconds, 500, 5000);
        OffsetX = double.IsFinite(OffsetX) ? Math.Clamp(OffsetX, -512, 512) : 8;
        OffsetY = double.IsFinite(OffsetY) ? Math.Clamp(OffsetY, -512, 512) : 4;
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(FolderPath);
        var temporaryPath = Path.Combine(FolderPath, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, this, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // Rename in the same directory replaces the complete file atomically.
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        // Persist only byte-based colors. Avoid loading color-profile files from
        // arbitrary ContextColor strings in a hand-edited configuration.
        if (value[0] == '#')
        {
            if (value.Length is not (4 or 5 or 7 or 9) || !value.Skip(1).All(Uri.IsHexDigit))
                return fallback;
        }
        else if (!value.All(char.IsLetter))
        {
            return fallback;
        }
        try
        {
            return System.Windows.Media.ColorConverter.ConvertFromString(value) is System.Windows.Media.Color color
                ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
                : fallback;
        }
        catch (FormatException) { return fallback; }
        catch (ArgumentException) { return fallback; }
        catch (NotSupportedException) { return fallback; }
    }
}
