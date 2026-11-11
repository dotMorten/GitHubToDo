using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class AiProviderTests
{
    [TestMethod]
    [DataRow(false, false, AiProvider.Disabled)]
    [DataRow(false, true, AiProvider.Disabled)]
    [DataRow(true, false, AiProvider.Local)]
    [DataRow(true, true, AiProvider.Local)]
    public void NoPreferenceDefaultsToLocalOnlyWhenAvailable(bool local, bool copilot, AiProvider expected)
    {
        Assert.AreEqual(expected, AiProviderSelection.Effective(null, local, copilot));
    }

    [TestMethod]
    [DataRow(AiProvider.Disabled, true, true, AiProvider.Disabled)]
    [DataRow(AiProvider.Local, true, true, AiProvider.Local)]
    [DataRow(AiProvider.Copilot, true, true, AiProvider.Copilot)]
    [DataRow(AiProvider.Copilot, false, true, AiProvider.Copilot)]
    [DataRow(AiProvider.Local, false, true, AiProvider.Disabled)]
    [DataRow(AiProvider.Copilot, true, false, AiProvider.Disabled)]
    public void ExplicitChoiceIsHonoredWithoutFallingBackToAnotherProvider(
        AiProvider preference, bool local, bool copilot, AiProvider expected)
    {
        Assert.AreEqual(expected, AiProviderSelection.Effective(preference, local, copilot));
    }

    [TestMethod]
    public void PreferredProviderRecoversAfterTemporaryUnavailabilityWithoutChangingPreference()
    {
        var preference = AiProvider.Copilot;
        Assert.AreEqual(AiProvider.Disabled, AiProviderSelection.Effective(preference, true, false));
        Assert.AreEqual(AiProvider.Copilot, AiProviderSelection.Effective(preference, true, true));
    }
}
