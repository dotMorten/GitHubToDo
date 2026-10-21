using System.Xml.Linq;

namespace GitHubToDo.Services;

internal sealed record AppUpdateNotification(string Title, string Message, string Tag)
{
    public static AppUpdateNotification Available(bool required) => new(
        required ? "GitHubToDo update required" : "GitHubToDo update available",
        "Close and reopen GitHubToDo to let Windows App Installer apply the update. Installation timing depends on Windows update settings.",
        "available");

    public static AppUpdateNotification? Installed(Version current, string? previous)
    {
        if (previous is null) return null;
        if (!Version.TryParse(previous, out var lastVersion))
            throw new InvalidDataException("The saved GitHubToDo version is invalid.");
        return current > lastVersion
            ? new("GitHubToDo updated", $"GitHubToDo has been updated to version {current}.", "installed")
            : null;
    }

    public string ToXml() => new XElement("toast",
        new XAttribute("activationType", "protocol"),
        new XAttribute("launch", "https://github.com/dotMorten/GitHubToDo/releases/latest"),
        new XElement("visual",
            new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", Title), new XElement("text", Message)))).ToString(SaveOptions.DisableFormatting);
}
