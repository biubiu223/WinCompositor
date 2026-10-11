using System.Globalization;

namespace Compositor.Core.Localization;

/// <summary>The languages the interface comes in.</summary>
public enum AppLanguage
{
    Chinese,
    English,
}

/// <summary>
/// The interface language. Every piece of interface text is written with both versions side by side —
/// <c>L.T("新建…", "New…")</c> — so a screen's wording is read and changed in one place, and nothing has to be kept in
/// step across files. The language is chosen once at start-up (from the settings, or Windows' own language);
/// changing it takes effect the next time Compositor starts, as in Photoshop.
/// </summary>
public static class L
{
    /// <summary>The language in use. Chinese until start-up says otherwise (the tests rely on that).</summary>
    public static AppLanguage Current { get; set; } = AppLanguage.Chinese;

    public static bool IsEnglish => Current == AppLanguage.English;

    /// <summary>The text in the current language.</summary>
    public static string T(string chinese, string english) => Current == AppLanguage.English ? english : chinese;

    /// <summary>Windows' display language: Chinese for any Chinese, English for everything else.</summary>
    public static AppLanguage FromSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Chinese
            : AppLanguage.English;
}
