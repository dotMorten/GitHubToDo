using System.Xml.Linq;
using GitHubToDo.Services;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class AppUpdateNotificationTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("0.1.4.0")]
    [DataRow("0.1.5.0")]
    public void FirstRunSameVersionAndDowngradesDoNotClaimAnUpdate(string? previous)
    {
        Assert.IsNull(AppUpdateNotification.Installed(new Version(0, 1, 4, 0), previous));
    }

    [TestMethod]
    public void UpgradeReportsTheInstalledPackageVersion()
    {
        var notification = AppUpdateNotification.Installed(new Version(0, 1, 4, 0), "0.1.3.0");
        Assert.IsNotNull(notification);
        Assert.AreEqual("GitHubToDo updated", notification.Title);
        Assert.AreEqual("GitHubToDo has been updated to version 0.1.4.0.", notification.Message);
        Assert.AreEqual("installed", notification.Tag);
    }

    [TestMethod]
    [DataRow(false, "GitHubToDo update available")]
    [DataRow(true, "GitHubToDo update required")]
    public void AvailableAndRequiredUpdatesExplainWindowsManagedInstallation(bool required, string title)
    {
        var notification = AppUpdateNotification.Available(required);
        Assert.AreEqual(title, notification.Title);
        Assert.Contains("Close and reopen GitHubToDo", notification.Message);
        Assert.Contains("Windows App Installer", notification.Message);
        Assert.AreEqual("available", notification.Tag, "Repeated checks replace, rather than stack, availability notifications.");
        var toast = XDocument.Parse(notification.ToXml()).Root!;
        Assert.AreEqual("protocol", (string?)toast.Attribute("activationType"));
        Assert.AreEqual("https://github.com/dotMorten/GitHubToDo/releases/latest", (string?)toast.Attribute("launch"));
        CollectionAssert.AreEqual(new[] { title, notification.Message },
            toast.Descendants("text").Select(element => element.Value).ToArray());
        Assert.AreEqual("ToastGeneric", (string?)toast.Descendants("binding").Single().Attribute("template"));
    }

    [TestMethod]
    public void InvalidPersistedVersionIsNotSilentlyAccepted()
    {
        Assert.Throws<InvalidDataException>(() => AppUpdateNotification.Installed(new Version(0, 1, 4, 0), "not-a-version"));
    }

    [TestMethod]
    public void NotificationTextIsXmlEscaped()
    {
        var content = new AppUpdateNotification("Update <ready>", "A & B", "test");
        var toast = XDocument.Parse(content.ToXml());
        CollectionAssert.AreEqual(new[] { content.Title, content.Message },
            toast.Descendants("text").Select(element => element.Value).ToArray());
    }
}
