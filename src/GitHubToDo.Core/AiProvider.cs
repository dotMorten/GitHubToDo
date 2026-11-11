namespace GitHubToDo.Core;

public enum AiProvider { Disabled, Local, Copilot }

public static class AiProviderSelection
{
    public static AiProvider Default(bool localAvailable) => localAvailable ? AiProvider.Local : AiProvider.Disabled;

    public static AiProvider Effective(AiProvider? preference, bool localAvailable, bool copilotAvailable) =>
        (preference ?? Default(localAvailable)) switch
        {
            AiProvider.Local when localAvailable => AiProvider.Local,
            AiProvider.Copilot when copilotAvailable => AiProvider.Copilot,
            _ => AiProvider.Disabled
        };
}
