using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubToDo.Core;
using GitHubToDo.Services;
using Microsoft.UI.Dispatching;
using Windows.Storage;
using Launcher = Windows.System.Launcher;

namespace GitHubToDo.ViewModels;

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly HttpClient _http = new();
    private readonly LocalStore _store;
    private readonly ICredentialStore? _credentials;
    private readonly DeviceFlowAuthenticator _auth;
    private readonly IGitHubClient _client;
    private readonly WorkspaceService _workspace;
    private readonly DispatcherQueueTimer _timer;
    private readonly SemaphoreSlim _tokenLock = new(1);
    private CancellationTokenSource _session = new();
    private CancellationTokenSource? _device;
    private CancellationTokenSource? _summaryCancellation;
    private string _summarySnapshot = "[]";
    private string? _summaryItemId;
    private bool _isEntireSummary;
    private UnreadSummarySource? _unreadSummarySource;
    private HashSet<string> _summaryDiscussionIds = [];
    private OAuthToken? _token;
    private Uri? _verificationUri;
    private bool _isDisposed;
    private bool _isInitialized;
    private AccountSnapshot? _lastProjectedAccount;
    private int _isProjectionQueued;
    private int _activeRefreshes;
    private readonly bool _isDemo = false;
    private readonly WorkspaceRows _rows = new();
    private readonly DiscussionPreview _preview = new();
    public bool IsDemo => _isDemo;
    public ObservableCollection<QueryRow> Queries => _rows.Queries;
    public ObservableCollection<ItemRow> Items => _rows.Items;
    public IReadOnlyList<ItemRow> SelectedItems => _rows.SelectedItems;
    public ObservableCollection<ActivityRow> PreviewActivities => _preview.Activities;
    public bool CanEditSelectedQuery => SelectedQuery?.IsSavedQuery == true;
    public bool HasSelectedItem => SelectedItem is not null;
    public string SelectionStatus => $"{SelectedItems.Count} selected · Right-click or Ctrl+Q / Ctrl+U: all selected rows · Details toolbar: previewed item";
    public bool IsUpdatingItems { get; private set; }
    public string ItemSortKey => _rows.SortKey;
    public bool ItemSortDescending => _rows.SortDescending;

    /// <summary>Re-sorts the item table. Selection, preview and read state are unaffected.</summary>
    public void SortItems(string key, bool descending)
    {
        IsUpdatingItems = true;
        ItemProjectionStarting?.Invoke();
        try { _rows.SetSort(key, descending); }
        finally { IsUpdatingItems = false; }
        OnPropertyChanged(nameof(ItemSortKey));
        OnPropertyChanged(nameof(ItemSortDescending));
    }
    public event Action? QueriesChanged;
    public event Action? ItemProjectionStarting;
    public event Action? ExplicitReadStateChanging;
    public event Action? UnreadRestored;

    [ObservableProperty] public partial string ClientId { get; set; } = Services.BuildInfo.ClientId ?? "";
    /// <summary>True for official builds with the OAuth client ID baked in; the client ID setting is then hidden.</summary>
    public bool HasBuiltInClientId { get; } = Services.BuildInfo.ClientId is not null;
    [ObservableProperty] public partial bool IncludePrivate { get; set; }
    [ObservableProperty] public partial bool IncludeProjects { get; set; }
    private bool _projectColumnsVisible;

    public async Task ConfigureColumnsAsync(bool visible, bool projectsVisible)
    {
        _workspace.ColumnMetadataEnabled = visible;
        _projectColumnsVisible = projectsVisible;
        _workspace.IncludeProjectFields = projectsVisible && IncludeProjects;
        await LoadColumnMetadataAsync();
    }

    private Task LoadColumnMetadataAsync() => IsSignedIn && SelectedQuery is { } query && _workspace.ColumnMetadataEnabled
        ? RunAsync(async () =>
        {
            var failures = await _workspace.RefreshColumnsAsync(query.Id, ct: _session.Token);
            if (failures > 0) Error = "Some optional column values could not be refreshed. Hover a column value for details.";
            UpdateProjection();
        }) : Task.CompletedTask;

    partial void OnIncludeProjectsChanged(bool value) => _workspace.IncludeProjectFields = _projectColumnsVisible && value;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkReadCommand), nameof(MarkUnreadCommand), nameof(MarkPreviewReadCommand), nameof(MarkPreviewUnreadCommand), nameof(FlagSelectionCommand), nameof(OpenBrowserCommand), nameof(ToggleItemFollowUpCommand), nameof(ClearFollowUpsCommand))]
    public partial bool IsSignedIn { get; set; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsDeviceFlow { get; set; }
    [ObservableProperty] public partial string DeviceMessage { get; set; } = "";
    [ObservableProperty] public partial string AccountLabel { get; set; } = "Not signed in";
    [ObservableProperty] public partial string AccountLogin { get; set; } = "";
    [ObservableProperty] public partial string ItemFilter { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = Services.BuildInfo.ClientId is null
        ? "Configure your own GitHub OAuth application to get started."
        : "Sign in with GitHub to get started.";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string IgnoredLogins { get; set; } = "";
    [ObservableProperty] public partial string IgnoredStatus { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelectedQuery))]
    public partial QueryRow? SelectedQuery { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkReadCommand), nameof(MarkUnreadCommand), nameof(MarkPreviewReadCommand), nameof(MarkPreviewUnreadCommand), nameof(FlagSelectionCommand), nameof(OpenBrowserCommand), nameof(ToggleItemFollowUpCommand), nameof(ClearFollowUpsCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    public partial ItemRow? SelectedItem { get; set; }
    [ObservableProperty] public partial string EmptyMessage { get; set; } = "Sign in to load your saved queries.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand), nameof(SummarizeUnreadCommand))]
    public partial bool IsLocalAiReady { get; set; }
    [ObservableProperty] public partial string LocalAiDescription { get; set; } = "Checking the installed local Windows AI model.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand), nameof(SummarizeUnreadCommand))]
    public partial bool IsSummarizing { get; set; }
    [ObservableProperty] public partial bool IsSummaryOpen { get; set; }
    [ObservableProperty] public partial string SummaryHeading { get; set; } = "Discussion summary";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string SummaryStatus { get; set; } = "";
    [ObservableProperty] public partial bool SummaryFailed { get; set; }

    public ShellViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
#if DEBUG
        _isDemo = App.IsDemo;
#endif
        var databaseName = _isDemo ? "githubtodo-demo.db" : "githubtodo.db";
#if DEBUG
        if (_isDemo && App.DemoProfile.Length > 0) databaseName = $"githubtodo-demo-{App.DemoProfile}.db";
#endif
        _store = new LocalStore(Path.Combine(ApplicationData.Current.LocalFolder.Path, databaseName));
        if (!_isDemo) _credentials = new WindowsCredentialStore();
        _auth = new DeviceFlowAuthenticator(_http);
#if DEBUG
        _client = _isDemo ? new DemoGitHubClient() : new GraphQlGitHubClient(_http, GetAccessTokenAsync, _store);
#else
        _client = new GraphQlGitHubClient(_http, GetAccessTokenAsync, _store);
#endif
        _workspace = new WorkspaceService(_store, _client);
        _workspace.Changed += WorkspaceChanged;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(2);
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public ItemReadSnapshot? PreviewReadThrough(int index) => _preview.ReadThrough(index);

    public Task MarkCommentsReadAsync(ItemReadSnapshot snapshot) => RunAsync(async () =>
    {
        await _workspace.MarkCommentsReadAsync(snapshot, _session.Token);
        UpdateProjection();
    });

    public Task SetCommentReadStateAsync(string itemId, string activityId, bool read)
    {
        ExplicitReadStateChanging?.Invoke();
        var snapshot = new ItemReadSnapshot(itemId, [activityId]);
        return RunAsync(async () =>
        {
            if (read) await _workspace.MarkCommentsReadAsync(snapshot, _session.Token);
            else await _workspace.MarkCommentsUnreadAsync(snapshot, _session.Token);
            UpdateProjection();
            if (!read) UnreadRestored?.Invoke();
        });
    }

    public Task SetCommentFollowUpAsync(string itemId, string activityId, bool flagged) => RunAsync(async () =>
    {
        await _workspace.SetFollowUpAsync(itemId, activityId, flagged, _session.Token);
        UpdateProjection();
    });

    [RelayCommand(CanExecute = nameof(CanOpenBrowser))]
    private async Task ToggleItemFollowUpAsync()
    {
        if (SelectedItem is not { } row) return;
        var flagged = !row.IsFlagged;
        await RunAsync(async () =>
        {
            await _workspace.SetFollowUpAsync(row.Id, null, flagged, _session.Token);
            UpdateProjection();
            Status = flagged ? "Item flagged for follow-up on this device." : "Item flag cleared; comment flags are unchanged.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanOpenBrowser))]
    private async Task ClearFollowUpsAsync()
    {
        if (SelectedItem is not { } row) return;
        await RunAsync(async () =>
        {
            await _workspace.ClearFollowUpsAsync(row.Id, _session.Token);
            UpdateProjection();
            Status = "Item and comment follow-up flags cleared. Read state is unchanged.";
        });
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await RunAsync(async () =>
        {
            CheckLocalAi();
            ClientId = Services.BuildInfo.ClientId ?? await _store.GetSettingAsync("oauth-client-id") ?? "";
            IncludePrivate = await _store.GetSettingAsync("oauth-private") == "true";
            IncludeProjects = await _store.GetSettingAsync("oauth-projects") == "true";
#if DEBUG
            if (_isDemo)
            {
                await OpenAccountAsync(DemoGitHubClient.User, _session.Token);
                await PollAsync();
                return;
            }
#endif
            _token = _credentials!.Load();
            if (_token is null) return;
            var id = await _store.GetSettingAsync("active-user-id");
            var login = await _store.GetSettingAsync("active-user-login");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(login))
                await OpenAccountAsync(new GitHubUser(id, login), _session.Token);
            var user = await _client.GetUserAsync(_session.Token);
            await OpenAccountAsync(user, _session.Token);
            await PollAsync();
        });
    }

    private async Task OpenAccountAsync(GitHubUser user, CancellationToken ct)
    {
        await _workspace.OpenAsync(user, ct);
        ct.ThrowIfCancellationRequested();
        await _store.SetSettingAsync("active-user-id", user.Id, ct);
        await _store.SetSettingAsync("active-user-login", user.Login, ct);
        IsSignedIn = true;
        AccountLabel = (_isDemo ? "DEMO · " : "") + $"Signed in as @{user.Login}";
        AccountLogin = user.Login;
        IsSettingsOpen = false;
        IgnoredLogins = string.Join(Environment.NewLine, _workspace.Account!.IgnoredAccounts.Select(a => a.Login));
        UpdateProjection();
        _timer.Start();
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var token = _token ?? throw new GitHubException("Sign in again to continue.", 401);
            if (token.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                if (string.IsNullOrEmpty(token.RefreshToken) ||
                    token.RefreshExpiresAt <= DateTimeOffset.UtcNow)
                    throw new GitHubException("Your session expired. Sign in again.", 401);
                try { token = await _auth.RefreshAsync(ClientId.Trim(), token, ct); }
                catch (GitHubException ex) when (ex.StatusCode is null or 401)
                {
                    throw new GitHubException($"Session renewal failed: {ex.Message} Sign in again.", 401);
                }
                ct.ThrowIfCancellationRequested();
                _credentials!.Save(token);
                _token = token;
            }
            return token.AccessToken;
        }
        finally { _tokenLock.Release(); }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync() => await RunAsync(async () =>
    {
        if (!HasBuiltInClientId) await _store.SetSettingAsync("oauth-client-id", ClientId.Trim());
        await _store.SetSettingAsync("oauth-private", IncludePrivate ? "true" : "false");
        await _store.SetSettingAsync("oauth-projects", IncludeProjects ? "true" : "false");
        Status = "Configuration saved. Scope changes take effect on your next sign-in.";
    });

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsDeviceFlow) return;
        await RunAsync(async () =>
        {
#if DEBUG
            if (_isDemo)
            {
                await OpenAccountAsync(DemoGitHubClient.User, _session.Token);
                await PollAsync();
                return;
            }
#endif
            if (string.IsNullOrWhiteSpace(ClientId))
                throw new ArgumentException("Enter the client ID of your own GitHub OAuth app with device flow enabled. No client secret is required.");
            var clientId = ClientId.Trim();
            if (!HasBuiltInClientId) await _store.SetSettingAsync("oauth-client-id", clientId);
            await _store.SetSettingAsync("oauth-private", IncludePrivate ? "true" : "false");
            await _store.SetSettingAsync("oauth-projects", IncludeProjects ? "true" : "false");
            _device = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
            var ct = _device.Token;
            IsDeviceFlow = true;
            try
            {
                var code = await _auth.RequestCodeAsync(clientId, IncludePrivate, IncludeProjects, ct);
                _verificationUri = code.VerificationUri;
                DeviceMessage = $"Enter code {code.UserCode} at {code.VerificationUri}. Expires in {code.ExpiresIn / 60} minutes. Waiting for authorization…";
                var token = await _auth.PollAsync(clientId, code, ct);
                ct.ThrowIfCancellationRequested();
                _credentials!.Save(token);
                _token = token;
                var user = await _client.GetUserAsync(ct);
                await OpenAccountAsync(user, ct);
                await PollAsync();
            }
            finally
            {
                IsDeviceFlow = false;
                _verificationUri = null;
                _device.Dispose();
                _device = null;
            }
        });
    }

    [RelayCommand] private void CancelSignIn() => _device?.Cancel();
    private bool CanMarkRead() => IsSignedIn && SelectedItems.Count > 0 && SelectedItems.All(row => row.Item.HasBaseline);
    private bool CanOpenBrowser() => IsSignedIn && SelectedItem is not null;
    private bool CanSummarize() => IsSignedIn && IsLocalAiReady && !IsSummarizing &&
        SelectedItem is { Item.HasBaseline: true };
    private bool CanSummarizeUnread() => CanSummarize() && SelectedItem is { UnreadActivities.Count: > 0 };

    private void CheckLocalAi()
    {
        var availability = LocalAiSummarizer.GetAvailability();
        LocalAiDescription = availability.Description;
        IsLocalAiReady = availability.Ready;
    }

    public void SetItemSelection(IEnumerable<ItemRow> selected, ItemRow? primary = null)
    {
        _rows.UpdateSelection(selected);
        SelectedItem = primary is not null && SelectedItems.Contains(primary) ? primary :
            SelectedItem is not null && SelectedItems.Contains(SelectedItem) ? SelectedItem : SelectedItems.FirstOrDefault();
        UpdateSelectionState();
    }

    private UnreadSummarySource[] SummarySources(bool entireDiscussion) =>
        (entireDiscussion ? SelectedItem?.CreateEntireSummarySource() : SelectedItem?.CreateUnreadSummarySource()) is { } source
        ? [source] : [];

    private void UpdateSelectionState()
    {
        OnPropertyChanged(nameof(SelectionStatus));
        OnPropertyChanged(nameof(MarkSelectionReadLabel));
        OnPropertyChanged(nameof(MarkSelectionUnreadLabel));
        OnPropertyChanged(nameof(FlagSelectionLabel));
        MarkReadCommand.NotifyCanExecuteChanged();
        MarkUnreadCommand.NotifyCanExecuteChanged();
        MarkPreviewReadCommand.NotifyCanExecuteChanged();
        MarkPreviewUnreadCommand.NotifyCanExecuteChanged();
        FlagSelectionCommand.NotifyCanExecuteChanged();
        OpenBrowserCommand.NotifyCanExecuteChanged();
        ToggleItemFollowUpCommand.NotifyCanExecuteChanged();
        ClearFollowUpsCommand.NotifyCanExecuteChanged();
        SummarizeCommand.NotifyCanExecuteChanged();
        SummarizeUnreadCommand.NotifyCanExecuteChanged();
        UpdateSummaryState();
    }

    private void UpdateSummaryState()
    {
        if (!IsSummaryOpen) return;
        if (_summaryItemId != SelectedItem?.Id)
        {
            CloseSummary();
            return;
        }
        if (_isEntireSummary)
        {
            var snapshot = UnreadSummary.Snapshot(SummarySources(entireDiscussion: true));
            if (snapshot == _summarySnapshot) return;
            _summarySnapshot = snapshot;
        }
        else
        {
            if (_unreadSummarySource is null ||
                SelectedItem?.IsUnreadSummaryCurrent(_unreadSummarySource, _summaryDiscussionIds) == true)
                return;
            _unreadSummarySource = null;
        }
        _summaryCancellation?.Cancel();
        _summaryCancellation = null;
        IsSummarizing = false;
        Summary = "";
        SummaryFailed = false;
        SummaryStatus = _isEntireSummary
            ? "Discussion changed. Summarize again to update."
            : "Discussion changed. Summarize unread again to update.";
    }

    [RelayCommand(CanExecute = nameof(CanSummarize), AllowConcurrentExecutions = true)]
    private Task SummarizeAsync() => GenerateSummaryAsync(entireDiscussion: true);

    [RelayCommand(CanExecute = nameof(CanSummarizeUnread), AllowConcurrentExecutions = true)]
    private Task SummarizeUnreadAsync() => GenerateSummaryAsync(entireDiscussion: false);

    private async Task GenerateSummaryAsync(bool entireDiscussion)
    {
        if (!(entireDiscussion ? CanSummarize() : CanSummarizeUnread())) return;
        var sources = SummarySources(entireDiscussion);
        _summaryCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        _summaryCancellation = cancellation;
        _summarySnapshot = UnreadSummary.Snapshot(sources);
        _summaryItemId = SelectedItem?.Id;
        _isEntireSummary = entireDiscussion;
        _unreadSummarySource = entireDiscussion ? null : sources.Single();
        _summaryDiscussionIds = SelectedItem?.DiscussionActivities.Select(row => row.Id).ToHashSet() ?? [];
        var ct = cancellation.Token;
        bool IsCurrent() => !_isDisposed && !ct.IsCancellationRequested &&
            ReferenceEquals(_summaryCancellation, cancellation) && IsSummaryOpen;
        IsSummarizing = true;
        SummaryFailed = false;
        Summary = "";
        SummaryHeading = entireDiscussion ? "Discussion summary" : "Unread summary";
        SummaryStatus = "Preparing summary…";
        IsSummaryOpen = true;
        Error = "";
        var progress = new Progress<string>(message =>
        {
            if (IsCurrent() && IsSummarizing)
                SummaryStatus = message;
        });
        var content = new Progress<string>(text =>
        {
            if (IsCurrent() && IsSummarizing) Summary = text;
        });
        try
        {
            var summary = await LocalAiSummarizer.SummarizeAsync(sources, progress, content, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            Summary = summary;
            SummaryStatus = "";
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            if (!_isDisposed && ReferenceEquals(_summaryCancellation, cancellation))
            {
                Summary = "";
                SummaryStatus = "Summarization cancelled.";
            }
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            Summary = "";
            SummaryFailed = true;
            var reason = Services.LocalAiErrors.Describe(ex);
            SummaryStatus = $"{char.ToUpperInvariant(reason[0])}{reason[1..]}";
        }
        finally
        {
            if (ReferenceEquals(_summaryCancellation, cancellation))
            {
                _summaryCancellation = null;
                IsSummarizing = false;
            }
        }
    }

    [RelayCommand]
    private void CloseSummary()
    {
        _summaryCancellation?.Cancel();
        _summaryCancellation = null;
        IsSummarizing = false;
        IsSummaryOpen = false;
        Summary = "";
        SummaryStatus = "";
        SummaryFailed = false;
        _summaryItemId = null;
        _unreadSummarySource = null;
        _summaryDiscussionIds.Clear();
    }

    [RelayCommand]
    private async Task OpenVerificationAsync() => await RunAsync(async () =>
    {
        if (_verificationUri is { } uri) await LaunchAsync(uri);
    });

    [RelayCommand]
    private async Task SignOutAsync()
    {
        CloseSession();
        await RunAsync(async () =>
        {
            _credentials?.Clear();
            await _store.SetSettingAsync("active-user-id", null);
            await _store.SetSettingAsync("active-user-login", null);
            Status = "Signed out. Cached account data is hidden; credentials removed from Windows Credential Locker.";
        });
    }

    private void CloseSession()
    {
        _timer.Stop();
        _session.Cancel();
        _session = new CancellationTokenSource();
        _token = null;
        _workspace.Close();
        _lastProjectedAccount = null;
        IsSignedIn = false;
        AccountLabel = "Not signed in";
        AccountLogin = "";
        SetItemSelection([]);
        SelectedItem = null;
        SelectedQuery = null;
        Items.Clear();
        Queries.Clear();
        IgnoredLogins = "";
        IgnoredStatus = "";
        IsSettingsOpen = true;
        EmptyMessage = "Sign in to load your saved queries.";
        QueriesChanged?.Invoke();
    }

    [RelayCommand]
    private Task RefreshAsync() => SelectedQuery is { } query
        ? RefreshWorkspaceAsync(ct => _workspace.CheckForUpdatesAsync(query.Id, ct))
        : Task.CompletedTask;

    private Task PollAsync() => RefreshWorkspaceAsync(_workspace.PollAsync);

    [RelayCommand]
    private Task FullRefreshAsync() => RefreshWorkspaceAsync(ct => _workspace.RefreshAsync(ct));

    private async Task RefreshWorkspaceAsync(Func<CancellationToken, Task> refresh)
    {
        if (!IsSignedIn) return;
        await RunAsync(async () =>
        {
            CheckLocalAi();
            _activeRefreshes++;
            IsBusy = true;
            Status = "Refresh queued — cached content remains available.";
            try
            {
                await refresh(_session.Token);
                Status = _workspace.Progress;
                UpdateProjection();
            }
            finally { IsBusy = --_activeRefreshes > 0; }
        });
    }

    [RelayCommand(CanExecute = nameof(CanMarkRead))]
    private Task MarkReadAsync() => SetReadAsync(SelectedItems.ToArray(), read: true);

    [RelayCommand(CanExecute = nameof(CanMarkRead))]
    private Task MarkUnreadAsync() => SetReadAsync(SelectedItems.ToArray(), read: false);

    // Details toolbar commands act only on the previewed item, never the whole multi-selection.
    [RelayCommand(CanExecute = nameof(CanMarkPreviewRead))]
    private Task MarkPreviewReadAsync() => SelectedItem is { } row ? SetReadAsync([row], read: true) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanMarkPreviewUnread))]
    private Task MarkPreviewUnreadAsync() => SelectedItem is { } row ? SetReadAsync([row], read: false) : Task.CompletedTask;

    private bool CanMarkPreviewRead() => IsSignedIn && SelectedItem is { CanMarkRead: true };
    private bool CanMarkPreviewUnread() => IsSignedIn && SelectedItem is { CanMarkUnread: true };

    public string MarkSelectionReadLabel => SelectedItems.Count > 1 ? $"Mark {SelectedItems.Count} items read" : "Mark read";
    public string MarkSelectionUnreadLabel => SelectedItems.Count > 1 ? $"Mark {SelectedItems.Count} items unread" : "Mark unread";
    public string FlagSelectionLabel => SelectedItems.Count > 1
        ? (SelectedItems.All(row => row.IsFlagged) ? $"Unflag {SelectedItems.Count} items" : $"Flag {SelectedItems.Count} items")
        : SelectedItem?.ItemFlagLabel ?? "Flag item";

    private async Task SetReadAsync(IReadOnlyList<ItemRow> rows, bool read)
    {
        if (!IsSignedIn || rows.Count == 0 || !rows.All(row => row.Item.HasBaseline)) return;
        ExplicitReadStateChanging?.Invoke();
        // Capture precisely the observed event identities before the first await.
        var snapshots = rows.Select(row =>
            new ItemReadSnapshot(row.Id, row.Item.Activities.Select(activity => activity.Id).ToArray())).ToArray();
        await RunAsync(async () =>
        {
            if (read) await _workspace.MarkReadAsync(snapshots, _session.Token);
            else await _workspace.MarkUnreadAsync(snapshots, _session.Token);
            UpdateProjection();
            if (!read) UnreadRestored?.Invoke();
            Status = read
                ? $"Observed activity marked read for {snapshots.Length} item(s) on this device."
                : $"Observed activity marked unread for {snapshots.Length} item(s). Follow-up flags are unchanged.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanMarkRead))]
    private async Task FlagSelectionAsync()
    {
        var rows = SelectedItems.ToArray();
        var flagged = !rows.All(row => row.IsFlagged);
        await RunAsync(async () =>
        {
            foreach (var row in rows) await _workspace.SetFollowUpAsync(row.Id, null, flagged, _session.Token);
            UpdateProjection();
            Status = flagged ? $"{rows.Length} item(s) flagged for follow-up on this device." : $"Item flags cleared for {rows.Length} item(s).";
        });
    }
    [RelayCommand(CanExecute = nameof(CanOpenBrowser))]
    private async Task OpenBrowserAsync() => await RunAsync(async () =>
    {
        if (SelectedItem is { } selected &&
            Uri.TryCreate(selected.Item.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            await LaunchAsync(uri);
    });

    private static async Task LaunchAsync(Uri uri)
    {
        if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException("Windows could not open your default browser.");
    }

    public Task OpenMarkdownLinkAsync(string link) => RunAsync(async () =>
    {
        // Markdown is untrusted repository content. Only an explicit click reaches
        // this route; do not accept custom schemes, relative links, credentials or ports.
        if (!IsSignedIn || SelectedItem is null) return;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
        {
            Status = "Preview link blocked. Only absolute HTTPS github.com links are allowed; use Open in GitHub to view other content.";
            return;
        }
        await LaunchAsync(uri);
    });

    [RelayCommand]
    private async Task SaveIgnoredAsync() => await RunAsync(async () =>
    {
        var logins = IgnoredLogins.Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimStart('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await _workspace.SetIgnoredAsync(logins, _session.Token);
        UpdateProjection();
        Status = "Ignored accounts saved; all cached badges recalculated.";
    });

    public Task SaveQueryAsync(string? id, string name, string expression) =>
        _workspace.SaveQueryAsync(id, name, expression, _session.Token);

    public async Task<SearchResult> PreviewQueryAsync(string expression, int count, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _session.Token);
        return await _client.PreviewSearchAsync(expression, AccountLogin, count, linked.Token);
    }

    public (bool Ready, string Description) GetLocalAiAvailability() => LocalAiSummarizer.GetAvailability();
    public Task DeleteQueryAsync(string id) => _workspace.DeleteQueryAsync(id, _session.Token);
    public Task MoveQueryAsync(int offset) => SelectedQuery is { } query
        ? RunAsync(() => _workspace.MoveQueryAsync(query.Id, offset, _session.Token)) : Task.CompletedTask;

    public async Task RunAsync(Func<Task> action)
    {
        var sessionToken = _session.Token;
        try { Error = ""; await action(); }
        catch (Exception) when (sessionToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { Status = "Operation cancelled."; }
        catch (GitHubException ex) when (ex.StatusCode == 401)
        {
            CloseSession();
            try { _credentials?.Clear(); } catch { /* Data is already hidden; original auth error takes priority. */ }
            Error = ex.Message + " Sign in again from Settings.";
        }
        catch (HttpRequestException ex)
        {
            Error = ex.Message;
            Status = IsSignedIn
                ? "Offline or GitHub is unreachable. Showing this signed-in account's cached data; refresh when connectivity returns."
                : "GitHub is unreachable. Check your connection, then try sign-in again.";
        }
        catch (Exception ex) { Error = ex.Message; Status = "Operation failed. Last successfully saved data is retained."; }
    }

    private void WorkspaceChanged()
    {
        if (_isDisposed || Interlocked.Exchange(ref _isProjectionQueued, 1) != 0) return;
        if (!_dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _isProjectionQueued, 0);
            if (_isDisposed) return;
            Status = _workspace.Progress;
            UpdateProjection();
        })) Interlocked.Exchange(ref _isProjectionQueued, 0);
    }

    public void UpdateProjection()
    {
        var account = _workspace.Account;
        if (account is null || !IsSignedIn) return;
        // Progress-only events do not change the snapshot or any displayed rows.
        if (ReferenceEquals(account, _lastProjectedAccount)) return;
        _lastProjectedAccount = account;
        var queryId = SelectedQuery?.Id;
        _rows.UpdateQueries(account);
        var selectedQuery = Queries.FirstOrDefault(q => q.Id == queryId) ?? Queries.FirstOrDefault();
        if (ReferenceEquals(SelectedQuery, selectedQuery)) UpdateItems();
        else SelectedQuery = selectedQuery;
        var unresolved = account.IgnoredAccounts.Where(a => a.Id is null).Select(a => a.Login).ToArray();
        IgnoredStatus = unresolved.Length == 0 ? "All accounts resolved to stable GitHub IDs." :
            "Unresolved (offline or unavailable); matching by login: " + string.Join(", ", unresolved);
        QueriesChanged?.Invoke();
    }

    partial void OnSelectedQueryChanged(QueryRow? value)
    {
        _workspace.PreferredQueryId = value?.Id;
        UpdateItems();
        _ = LoadColumnMetadataAsync();
    }
    partial void OnSelectedItemChanged(ItemRow? value)
    {
        _workspace.PreferredItemId = value?.Id;
        _preview.Update(value);
        UpdateSummaryState();
    }
    partial void OnItemFilterChanged(string value) => UpdateItems();
    private void UpdateItems()
    {
        IsUpdatingItems = true;
        ItemProjectionStarting?.Invoke();
        try { UpdateItemProjection(); }
        finally { IsUpdatingItems = false; }
    }

    private string? FilteredEmpty => string.IsNullOrWhiteSpace(ItemFilter) ? null : $"Nothing in this list matches “{ItemFilter.Trim()}”.";

    private void UpdateItemProjection()
    {
        var selectedId = SelectedItem?.Id;
        var account = _workspace.Account;
        _rows.UpdateItems(SelectedQuery?.Query, account, ItemFilter);
        SelectedItem = SelectedItems.FirstOrDefault(i => i.Id == selectedId) ?? SelectedItems.FirstOrDefault();
        _preview.Update(SelectedItem);
        UpdateSelectionState();
        if (SelectedQuery is not { } row || account is null) return;
        if (!row.IsSavedQuery)
        {
            EmptyMessage = Items.Count == 0 ? "No follow-ups. Flag an item or a comment to find it here." : "";
            return;
        }
        EmptyMessage = Items.Count == 0 ? FilteredEmpty ?? "No cached results. Refresh to search GitHub; failed searches keep the previous saved results." : "";
    }

    public void Dispose()
    {
        _isDisposed = true;
        _timer.Stop();
        _workspace.Changed -= WorkspaceChanged;
        _device?.Cancel();
        _summaryCancellation?.Cancel();
        _session.Cancel();
        _http.Dispose();
    }
}
