using System.IO;
using System.Text.Json;
using Compositor.Core.Localization;

namespace Compositor.App.Services;

/// <summary>The person's preferences, kept in %APPDATA%\Compositor\settings.json.</summary>
public sealed class Settings
{
    /// <summary>The values <see cref="Language"/> takes, as stored in the file.</summary>
    public const string SystemLanguage = "system", ChineseLanguage = "zh-CN", EnglishLanguage = "en";

    /// <summary>
    /// The interface language: <see cref="SystemLanguage"/> (Windows' own), <see cref="ChineseLanguage"/> or
    /// <see cref="EnglishLanguage"/>. Read once at start-up, so a change takes effect the next time Compositor starts.
    /// </summary>
    public string Language { get; set; } = SystemLanguage;

    public List<string> RecentProjects { get; set; } = new();
    public double LayersPanelWidth { get; set; } = 260;
    public bool ShowsRulers { get; set; }
    public bool ShowsGrid { get; set; }
    public bool ShowsGuides { get; set; } = true;
    public bool ShowsPixelGrid { get; set; } = true;
    public bool SnapEnabled { get; set; } = true;
    public bool SnapToGuides { get; set; } = true;
    public bool SnapToGrid { get; set; }
    public bool SnapToLayers { get; set; } = true;
    public bool SnapToDocumentBounds { get; set; } = true;
    public int GridSpacing { get; set; } = 64;
    public int GridSubdivisions { get; set; } = 8;
    public bool AutoSelect { get; set; }
    public bool ShowsTransformControls { get; set; } = true;
    public int JpegQuality { get; set; } = 90;
    /// <summary>Keyboard shortcuts changed from their defaults: definition id → "Key|Modifiers" (an empty string removes one).</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    public bool ShowsNavigator { get; set; } = true;
    public double WindowWidth { get; set; } = 1320;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; } = true;
    public int NewWidth { get; set; } = 1920;
    public int NewHeight { get; set; } = 1080;
    public double NewResolution { get; set; } = 72;
    public string NewBackground { get; set; } = "white";

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Compositor", "settings.json");

    public static Settings Current { get; } = Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<Settings>(text) ?? new Settings();
                // A settings file from before the interface had languages: that person has been using it in Chinese,
                // so it stays Chinese (even on an English Windows) until they choose otherwise in Preferences. Only a
                // first start follows Windows.
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind == JsonValueKind.Object && !json.RootElement.TryGetProperty(nameof(Language), out _))
                    settings.Language = ChineseLanguage;
                return settings;
            }
        }
        catch { /* start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* preferences are best effort */ }
    }

    /// <summary>
    /// A <see cref="Language"/> value as one of the three it can be: an unknown or missing one (an older settings file,
    /// or one edited by hand) means following Windows.
    /// </summary>
    public static string NormalizeLanguage(string? language) => language?.Trim() switch
    {
        { } value when value.Equals(ChineseLanguage, StringComparison.OrdinalIgnoreCase) => ChineseLanguage,
        { } value when value.Equals(EnglishLanguage, StringComparison.OrdinalIgnoreCase) => EnglishLanguage,
        _ => SystemLanguage,
    };

    /// <summary>The language a <see cref="Language"/> value gives on this computer.</summary>
    public static AppLanguage ResolveLanguage(string? language) => NormalizeLanguage(language) switch
    {
        ChineseLanguage => AppLanguage.Chinese,
        EnglishLanguage => AppLanguage.English,
        _ => L.FromSystem(),
    };

    public void AddRecent(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 12) RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
        Save();
    }
}
