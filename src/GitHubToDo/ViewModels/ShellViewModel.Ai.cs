using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubToDo.Core;
using GitHubToDo.Services;

namespace GitHubToDo.ViewModels;

public partial class ShellViewModel
{
    private const string AiProviderSetting = "ai-provider";
    private AiProvider? _aiPreference;
    private AiProvider _activeAiProvider;
    private CancellationTokenSource? _aiAvailabilityCancellation;
    private bool _aiSettingsLoaded;

    public event Action? AiProviderChanged;
    public AiProvider ActiveAiProvider => _activeAiProvider;
    public bool IsAiEnabled => ActiveAiProvider != AiProvider.Disabled;
    public int AiProviderIndex => (int)(_aiPreference ?? AiProviderSelection.Default(IsLocalAiAvailable));
    public bool CanChooseAiProvider => _aiSettingsLoaded;
    public string AiDescription => ActiveAiProvider switch
    {
        AiProvider.Local => "Uses the on-device Windows AI model. Prompts stay on this device.",
        AiProvider.Copilot => "Uses GitHub Copilot's cloud model. Discussion text and query descriptions are sent to Copilot using your account's usage allowance.",
        _ => "AI is disabled or the selected provider is unavailable. Summaries and describe-to-query are hidden."
    };

    [ObservableProperty] public partial bool IsLocalAiAvailable { get; set; }
    [ObservableProperty] public partial bool IsCopilotAiAvailable { get; set; }
    [ObservableProperty] public partial string LocalAiAvailability { get; set; } = "Checking the on-device Windows AI model...";
    [ObservableProperty] public partial string CopilotAiAvailability { get; set; } = "Sign in to GitHub to check Copilot access.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshAiAvailabilityCommand))]
    public partial bool IsCheckingAiAvailability { get; set; }

    private async Task LoadAiSettingsAsync()
    {
        var saved = await _store.GetSettingAsync(AiProviderSetting, _session.Token);
        if (saved is not null)
        {
            if (Enum.TryParse<AiProvider>(saved, out var provider) && Enum.IsDefined(provider))
                _aiPreference = provider;
            else
                throw new InvalidOperationException("The saved AI provider is invalid.");
        }
        _aiSettingsLoaded = true;
        OnPropertyChanged(nameof(CanChooseAiProvider));
        UpdateAiState();
    }

    public Task SetAiProviderAsync(int index) => RunAsync(async () =>
    {
        if (!_aiSettingsLoaded || !Enum.IsDefined((AiProvider)index))
            throw new ArgumentException("Choose a valid AI provider.");
        var provider = (AiProvider)index;
        if (provider == AiProvider.Local && !IsLocalAiAvailable ||
            provider == AiProvider.Copilot && !IsCopilotAiAvailable)
            throw new InvalidOperationException("The selected AI provider is unavailable.");
        await _store.SetSettingAsync(AiProviderSetting, provider.ToString(), _session.Token);
        _aiPreference = provider;
        UpdateAiState();
        if (provider != AiProvider.Local) await LocalAi.ReleaseAsync();
    });

    private void UpdateAiState()
    {
        var provider = AiProviderSelection.Effective(_aiPreference, IsLocalAiAvailable, IsCopilotAiAvailable);
        if (_activeAiProvider != provider)
        {
            CloseSummary();
            _activeAiProvider = provider;
        }
        OnPropertyChanged(nameof(ActiveAiProvider));
        OnPropertyChanged(nameof(IsAiEnabled));
        OnPropertyChanged(nameof(AiProviderIndex));
        OnPropertyChanged(nameof(AiDescription));
        UpdateSummaryCommands();
        AiProviderChanged?.Invoke();
    }

    private bool CanRefreshAiAvailability() => !IsCheckingAiAvailability;

    [RelayCommand(CanExecute = nameof(CanRefreshAiAvailability))]
    private Task RefreshAiAvailabilityAsync() => RunAsync(CheckAiAvailabilityAsync);

    private async Task CheckAiAvailabilityAsync()
    {
        _aiAvailabilityCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        _aiAvailabilityCancellation = cancellation;
        var ct = cancellation.Token;
        IsCheckingAiAvailability = true;
        try
        {
            var localTask = Task.Run(LocalAiSummarizer.GetAvailability, ct);
            var login = AccountLogin;
            var copilotTask = Task.Run(() => CopilotAi.GetAvailabilityAsync(login, ct), ct);
            var local = await localTask;
            ct.ThrowIfCancellationRequested();
            IsLocalAiAvailable = local.Available;
            LocalAiAvailability = local.Description;
            UpdateAiState();
            var copilot = await copilotTask;
            ct.ThrowIfCancellationRequested();
            IsCopilotAiAvailable = copilot.Available;
            CopilotAiAvailability = copilot.Description;
            UpdateAiState();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_aiAvailabilityCancellation, cancellation))
            {
                _aiAvailabilityCancellation = null;
                IsCheckingAiAvailability = false;
            }
        }
    }

    partial void OnAccountLoginChanged(string value)
    {
        IsCopilotAiAvailable = false;
        CopilotAiAvailability = value.Length == 0 ? "Sign in to GitHub to check Copilot access." : "Checking Copilot access...";
        UpdateAiState();
        if (_aiSettingsLoaded) _ = RefreshAiAvailabilityAsync();
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (value && _aiSettingsLoaded && !IsCheckingAiAvailability)
            _ = RefreshAiAvailabilityAsync();
    }

    public Task<GeneratedQuery> GenerateQueryWithAiAsync(string description,
        Func<string, CancellationToken, Task<QueryCheck>> check, IProgress<QueryAttempt> progress,
        IProgress<string> status, CancellationToken ct, bool allowModelDownload) => ActiveAiProvider switch
    {
        AiProvider.Local => LocalAiQueryWriter.GenerateAsync(description, check, progress, status, ct, allowModelDownload),
        AiProvider.Copilot => CopilotQueryWriter.GenerateAsync(description, AccountLogin, check, progress, status, ct),
        _ => throw new InvalidOperationException("Choose an available AI provider in Settings.")
    };
}
