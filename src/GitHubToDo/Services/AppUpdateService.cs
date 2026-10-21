using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace GitHubToDo.Services;

internal static class AppUpdateService
{
    private const string LastRunVersionKey = "AppUpdateLastRunVersion";

    public static async Task CheckAsync(CancellationToken cancellationToken)
    {
#if DEBUG
        if (App.IsDemo) return;
#endif
        var package = Package.Current;
        // Loose development packages and installs without an update feed cannot check for MSIX updates.
        if (package.IsDevelopmentMode || package.GetAppInstallerInfo() is null) return;

        var version = package.Id.Version;
        var current = new Version(version.Major, version.Minor, version.Build, version.Revision);
        var settings = ApplicationData.Current.LocalSettings.Values;
        var installed = AppUpdateNotification.Installed(current, settings[LastRunVersionKey] as string);
        cancellationToken.ThrowIfCancellationRequested();
        if (installed is not null) Show(installed);
        settings[LastRunVersionKey] = current.ToString();

        var result = await package.CheckUpdateAvailabilityAsync().AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        switch (result.Availability)
        {
            case PackageUpdateAvailability.Available:
            case PackageUpdateAvailability.Required:
                Show(AppUpdateNotification.Available(result.Availability == PackageUpdateAvailability.Required));
                break;
            case PackageUpdateAvailability.Error:
                throw new IOException("Windows could not check for GitHubToDo updates.", result.ExtendedError);
            case PackageUpdateAvailability.Unknown:
                throw new IOException("Windows could not determine whether a GitHubToDo update is available.", result.ExtendedError);
            case PackageUpdateAvailability.NoUpdates:
                break;
            default:
                throw new InvalidDataException($"Unexpected MSIX update availability: {result.Availability}.");
        }
    }

    private static void Show(AppUpdateNotification content)
    {
        var xml = new XmlDocument();
        xml.LoadXml(content.ToXml());
        var notification = new ToastNotification(xml)
        {
            Tag = content.Tag,
            Group = "app-updates",
            ExpirationTime = DateTimeOffset.Now.AddDays(1),
        };
        // OS toasts avoid the Windows App SDK singleton dependency in self-contained releases.
        ToastNotificationManager.CreateToastNotifier($"{Package.Current.Id.FamilyName}!App").Show(notification);
    }
}
