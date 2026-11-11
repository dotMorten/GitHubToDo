using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GitHubToDo;

public static class ViewHelpers
{
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> s_avatars = new(StringComparer.OrdinalIgnoreCase);

    public static bool Not(bool value) => !value;
    public static Windows.UI.Text.FontWeight UnreadWeight(bool unread) =>
        unread ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    public static bool CanConfigure(bool signedIn, bool deviceFlow) => !signedIn && !deviceFlow;
    public static string OAuthHeader(bool builtIn) => builtIn ? "GitHub sign-in" : "OAuth app";
    public static string OAuthDescription(bool builtIn) => builtIn
        ? "This release signs in through the official GitHub OAuth app. Sign out before changing this configuration."
        : "Register your own GitHub OAuth app with Device Flow enabled and paste its public client ID. Never enter a client secret. Sign out before changing this configuration.";
    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility CollapsedWhen(bool value) => Visible(!value);
    public static Symbol ReadActionIcon(bool unread) => unread ? Symbol.Accept : Symbol.Mail;
    public static Visibility TextVisible(string? value) => Visible(HasText(value));
    public static Visibility WorkspaceVisible(bool settings, bool signedIn) => Visible(!settings && signedIn);
    public static string CountLabel(int count, string noun) => $"{count:N0} {noun}";
    public static Brush TimeBrush(bool unread) => (Brush)Application.Current.Resources[
        unread ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
    public static string SummaryTitle(bool failed, string heading) => failed ? "Couldn't summarize" : heading;
    public static Brush SummaryAccentBrush(bool failed) => (Brush)Application.Current.Resources[
        failed ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush"];
    public static Brush SummaryStatusBrush(bool failed) => (Brush)Application.Current.Resources[
        failed ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];

    public static ImageSource? Avatar(string? login)
    {
#if DEBUG
        if (App.IsDemo) return null;
#endif
        if (string.IsNullOrWhiteSpace(login)) return null;
        var name = login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ? login[..^5] : login;
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        if (!s_avatars.TryGetValue(name, out var image))
        {
            image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri($"https://github.com/{name}.png?size=64"))
            {
                DecodePixelWidth = 64, DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical
            };
            s_avatars[name] = image;
        }
        return image;
    }
}
