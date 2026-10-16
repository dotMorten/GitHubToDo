using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace GitHubToDo.Services;

/// <summary>Persists and applies the app's Light/Dark/System appearance on this device.</summary>
internal static class ThemeService
{
    private const string SettingKey = "AppTheme";

    public static ElementTheme Load()
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[SettingKey] is int value && Enum.IsDefined(typeof(ElementTheme), value)
                ? (ElementTheme)value
                : ElementTheme.Default;
        }
        catch (InvalidOperationException) { return ElementTheme.Default; }
    }

    public static void Save(ElementTheme theme) => ApplicationData.Current.LocalSettings.Values[SettingKey] = (int)theme;

    public static void Apply(Window window, ElementTheme theme)
    {
        if (window.Content is FrameworkElement root) root.RequestedTheme = theme;
        // The caption buttons are drawn by the system and need to be told separately.
        window.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }
}
