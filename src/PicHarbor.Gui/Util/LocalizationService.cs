using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PicHarbor.Gui.Controls;

namespace PicHarbor.Gui.Util;

/// <summary>
/// Represents a supported language option in the UI and localization registry.
/// </summary>
public sealed class LanguageOption : ObservableObject
{
    public string Code { get; }
    public string DisplayName { get; }
    public string NativeName { get; }
    public string CultureName { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(ButtonType));
            }
        }
    }

    public PclButtonType ButtonType => IsSelected ? PclButtonType.Hero : PclButtonType.Normal;

    public LanguageOption(string code, string displayName, string nativeName, string cultureName, bool isSelected = false)
    {
        Code = code;
        DisplayName = displayName;
        NativeName = nativeName;
        CultureName = cultureName;
        IsSelected = isSelected;
    }
}

/// <summary>
/// Centralized, extensible localization manager for PicHarbor.
/// Coordinates thread culture, WPF merged dictionaries, fallback hierarchy, and language change events.
/// </summary>
public static class LocalizationService
{
    public const string DefaultLanguageCode = "zh-CN";

    /// <summary>
    /// Metadata registry for all officially supported languages.
    /// Extend this list to add new languages across the entire application.
    /// </summary>
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } = new List<LanguageOption>
    {
        new("zh-CN", "🇨🇳 简体中文", "简体中文", "zh-CN"),
        new("en-US", "🇺🇸 English", "English", "en-US"),
        new("zh-HK", "🇭🇰 粵語", "粵語", "zh-HK")
    }.AsReadOnly();

    public static event Action<string>? LanguageChanged;

    public static string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

    public static LanguageOption CurrentLanguage =>
        SupportedLanguages.FirstOrDefault(l => string.Equals(l.Code, CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
        ?? SupportedLanguages[0];

    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) && SupportedLanguages.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    public static void SwitchLanguage(string cultureCode)
    {
        var targetOption = SupportedLanguages.FirstOrDefault(l => string.Equals(l.Code, cultureCode, StringComparison.OrdinalIgnoreCase))
                           ?? SupportedLanguages[0];

        CurrentLanguageCode = targetOption.Code;

        foreach (var option in SupportedLanguages)
        {
            option.IsSelected = string.Equals(option.Code, targetOption.Code, StringComparison.OrdinalIgnoreCase);
        }

        // 1. Synchronize system and thread culture
        try
        {
            var culture = new CultureInfo(targetOption.CultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set Thread Culture for '{targetOption.CultureName}': {ex.Message}");
        }

        // 2. Synchronize WPF application merged resource dictionaries
        if (Application.Current is App app)
        {
            ApplyResourceDictionary(app, targetOption.Code);
        }

        // 3. Notify listeners
        LanguageChanged?.Invoke(targetOption.Code);
    }

    public static string GetString(string key, string fallback = "")
    {
        if (Application.Current?.TryFindResource(key) is string value)
        {
            return value;
        }
        return fallback;
    }

    private static void ApplyResourceDictionary(Application app, string cultureCode)
    {
        var dictUri = new Uri($"Resources/StringResources.{cultureCode}.xaml", UriKind.Relative);
        ResourceDictionary? targetDict = null;
        try
        {
            targetDict = new ResourceDictionary { Source = dictUri };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load resource dictionary '{dictUri}': {ex.Message}");
            return;
        }

        // Remove any previously merged StringResources dictionaries
        var existingDicts = app.Resources.MergedDictionaries
            .Where(d => d.Source != null && d.Source.OriginalString.Contains("StringResources"))
            .ToList();

        foreach (var dict in existingDicts)
        {
            app.Resources.MergedDictionaries.Remove(dict);
        }

        // Fallback chain: if target culture is not default (zh-CN), load base dictionary first
        // so any untranslated key automatically falls back to zh-CN without missing text
        if (!string.Equals(cultureCode, DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var baseFallbackDict = new ResourceDictionary
                {
                    Source = new Uri($"Resources/StringResources.{DefaultLanguageCode}.xaml", UriKind.Relative)
                };
                app.Resources.MergedDictionaries.Add(baseFallbackDict);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to merge base fallback dictionary: {ex.Message}");
            }
        }

        app.Resources.MergedDictionaries.Add(targetDict);
    }
}
