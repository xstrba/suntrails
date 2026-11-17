using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace World;

internal static class UiText
{
    public const string English = "en";
    public const string Czech = "cs";
    const string Preferences = "user://language.cfg";
    static readonly Dictionary<string, string> Translations = new();

    public static string Language { get; private set; } = English;
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public static void LoadLanguage()
    {
        using var config = new ConfigFile();
        string preferred = OS.GetLocaleLanguage() == Czech ? Czech : English;
        if (config.Load(Preferences) == Error.Ok)
        {
            string saved = (string)config.GetValue("interface", "language", preferred);
            if (saved is Czech or English) preferred = saved;
        }
        ApplyLanguage(preferred);
    }

    public static void ChangeLanguage(string language)
    {
        ApplyLanguage(language);
        using var config = new ConfigFile();
        config.SetValue("interface", "language", Language);
        if (config.Save(Preferences) != Error.Ok)
            GD.PushWarning("Couldn't save the interface language preference.");
    }

    static void ApplyLanguage(string language)
    {
        Language = language;
        Culture = CultureInfo.GetCultureInfo(language == Czech ? "cs-CZ" : "en-US");
        Translations.Clear();
        TranslationServer.SetLocale(language);
    }

    public static string Get(string message)
    {
        if (Translations.TryGetValue(message, out string text)) return text;
        using var key = new StringName(message);
        using var translated = TranslationServer.Translate(key);
        text = translated.ToString();
        Translations.Add(message, text);
        return text;
    }

    public static string Format(string message, params object[] values) =>
        string.Format(Culture, Get(message), values);

    public static string Count(string singular, string plural, int count) =>
        string.Format(Culture, TranslationServer.TranslatePlural(singular, plural, count).ToString(), count);
}
