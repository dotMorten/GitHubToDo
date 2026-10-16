namespace GitHubToDo.Core;

public sealed class WorkspaceService(LocalStore store, IGitHubClient client, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SearchOverlap = TimeSpan.FromMinutes(5);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim mutationGate = new(1);
    private readonly object refreshGate = new();
    private Task? refreshTask;
    private int refreshGeneration = -1;
    private bool refreshIsFull;
    private bool fullRefreshRequested;
    private int generation;
    public AccountSnapshot? Account { get; private set; }
    public string Progress { get; private set; } = "Sign in to start monitoring.";
    public string? PreferredQueryId { get; set; }
    public string? PreferredItemId { get; set; }
    public event Action? Changed;

    public async Task OpenAsync(GitHubUser user, CancellationToken ct = default)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            var loaded = await store.LoadAsync(user, ct);
            await store.SaveAsync(loaded, ct);
            generation++;
            Account = loaded;
            Progress = "Cached data loaded. Refresh to check for new activity.";
        }
        finally { mutationGate.Release(); }
        Changed?.Invoke();
    }

    public void Close()
    {
        generation++;
        Account = null;
        PreferredQueryId = null;
        PreferredItemId = null;
        Progress = "Signed out.";
        Changed?.Invoke();
    }

    private async Task MutateAsync(Action<AccountSnapshot> change, CancellationToken ct, int? expectedGeneration = null)
    {
        await mutationGate.WaitAsync(ct);
        try
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != generation)
                return;
            var account = LocalStore.Clone(Account ?? throw new InvalidOperationException("Sign in first."));
            var startGeneration = generation;
            change(account);
            await store.SaveAsync(account, ct);
            if (startGeneration == generation)
                Account = account;
        }
        finally { mutationGate.Release(); }
        Changed?.Invoke();
    }

    public Task SaveQueryAsync(string? id, string name, string expression, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Enter a query name.");
        expression = QueryParser.ExtractExpression(expression);
        QueryParser.Parse(expression, Account?.User.Login ?? "viewer");
        return MutateAsync(account =>
        {
            var query = id is null ? new SavedQuery() :
                account.Queries.FirstOrDefault(query => query.Id == id) ??
                throw new ArgumentException("The query no longer exists.");
            if (query.Expression != expression)
            {
                query.ItemIds.Clear();
                query.LastRefreshedAt = null;
                query.LastFullRefreshedAt = null;
                query.Error = null;
                query.TotalCount = 0;
                query.IsIncomplete = false;
            }
            query.Name = name.Trim();
            query.Expression = expression;
            if (id is null)
                account.Queries.Add(query);
        }, ct);
    }

    public Task DeleteQueryAsync(string id, CancellationToken ct = default) =>
        MutateAsync(account => account.Queries.RemoveAll(query => query.Id == id), ct);

    public Task MoveQueryAsync(string id, int offset, CancellationToken ct = default) =>
        MutateAsync(account =>
        {
            var index = account.Queries.FindIndex(query => query.Id == id);
            if (index < 0)
                throw new ArgumentException("The query no longer exists.");
            var destination = Math.Clamp(index + offset, 0, account.Queries.Count - 1);
            var query = account.Queries[index];
            account.Queries.RemoveAt(index);
            account.Queries.Insert(destination, query);
        }, ct);

    public Task MarkReadAsync(string itemId, IEnumerable<string> activityIds, CancellationToken ct = default) =>
        MarkReadAsync([new ItemReadSnapshot(itemId, activityIds.ToArray())], ct);

    public Task MarkReadAsync(IEnumerable<ItemReadSnapshot> items, CancellationToken ct = default) =>
        ChangeReadStateAsync(items, read: true, entireItem: true, ct);

    public Task MarkUnreadAsync(IEnumerable<ItemReadSnapshot> items, CancellationToken ct = default) =>
        ChangeReadStateAsync(items, read: false, entireItem: true, ct);

    public Task MarkCommentsReadAsync(ItemReadSnapshot snapshot, CancellationToken ct = default) =>
        ChangeReadStateAsync([snapshot], read: true, entireItem: false, ct);

    public Task MarkCommentsUnreadAsync(ItemReadSnapshot snapshot, CancellationToken ct = default) =>
        ChangeReadStateAsync([snapshot], read: false, entireItem: false, ct);

    private Task ChangeReadStateAsync(IEnumerable<ItemReadSnapshot> items, bool read, bool entireItem, CancellationToken ct)
    {
        var snapshots = items.Select(item => new ItemReadSnapshot(item.ItemId, item.ActivityIds.ToArray())).ToArray();
        if (snapshots.Length == 0) throw new ArgumentException("Select at least one item.", nameof(items));
        if (!entireItem && snapshots.Any(snapshot => snapshot.ActivityIds.Count == 0))
            throw new ArgumentException("Select at least one comment or review.", nameof(items));
        var expectedGeneration = generation;
        return MutateAsync(account =>
        {
            if (generation != expectedGeneration)
                throw new OperationCanceledException("The signed-in account changed before read state could be saved.");
            foreach (var snapshot in snapshots)
            {
                if (!account.Items.TryGetValue(snapshot.ItemId, out var item))
                    throw new ArgumentException("A selected item no longer exists.");
                if (!item.HasBaseline)
                    throw new InvalidOperationException("Wait for all selected items' activity to load before changing read state.");
                var known = item.Activities.Select(activity => activity.Id).ToHashSet();
                var observed = snapshot.ActivityIds.Where(known.Contains).ToArray();
                if (!entireItem && (observed.Length != snapshot.ActivityIds.Count ||
                    item.Activities.Any(activity => observed.Contains(activity.Id) && activity.Kind == ActivityKind.InitialItem)))
                    throw new ArgumentException("A selected comment or review is no longer available.");
                if (read)
                {
                    item.AcknowledgedIds.UnionWith(observed);
                    item.ManuallyUnreadIds.ExceptWith(observed);
                    if (!entireItem)
                    {
                        var discussion = ReadStatePolicy.DiscussionActivity(item, account.IgnoredAccounts);
                        if (discussion.Count > 0 && discussion.All(activity => item.AcknowledgedIds.Contains(activity.Id)))
                        {
                            item.IsMarkedUnread = false;
                            item.AcknowledgedIds.UnionWith(item.Activities.Where(activity => activity.Kind == ActivityKind.InitialItem).Select(activity => activity.Id));
                        }
                    }
                }
                else
                {
                    item.AcknowledgedIds.ExceptWith(observed);
                    if (!entireItem) item.ManuallyUnreadIds.UnionWith(observed);
                }
                if (entireItem) item.IsMarkedUnread = !read;
            }
        }, ct);
    }

    public Task SetFollowUpAsync(string itemId, string? activityId, bool flagged, CancellationToken ct = default)
    {
        var expectedGeneration = generation;
        return MutateAsync(account =>
        {
            if (generation != expectedGeneration)
                throw new OperationCanceledException("The signed-in account changed before the follow-up could be saved.");
            if (!account.Items.TryGetValue(itemId, out var item))
                throw new ArgumentException("The item no longer exists.");
            if (activityId is null) item.IsFlagged = flagged;
            else
            {
                if (flagged && !item.Activities.Any(activity => activity.Id == activityId && activity.Kind != ActivityKind.InitialItem))
                    throw new ArgumentException("That comment or review is no longer available.");
                if (flagged) item.FlaggedActivityIds.Add(activityId);
                else item.FlaggedActivityIds.Remove(activityId);
            }
        }, ct);
    }

    public Task ClearFollowUpsAsync(string itemId, CancellationToken ct = default)
    {
        var expectedGeneration = generation;
        return MutateAsync(account =>
        {
            if (generation != expectedGeneration)
                throw new OperationCanceledException("The signed-in account changed before follow-ups could be cleared.");
            if (!account.Items.TryGetValue(itemId, out var item))
                throw new ArgumentException("The item no longer exists.");
            item.IsFlagged = false;
            item.FlaggedActivityIds.Clear();
        }, ct);
    }

    public async Task SetIgnoredAsync(IEnumerable<string> logins, CancellationToken ct = default)
    {
        var current = Account ?? throw new InvalidOperationException("Sign in first.");
        var startGeneration = generation;
        var ignored = new List<IgnoredAccount>();
        var unresolved = new List<string>();
        foreach (var login in logins.Select(login => login.Trim()).Where(login => login.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (login.Contains('/') || login.Any(char.IsWhiteSpace))
                throw new ArgumentException($"Enter an account login, not a URL or display name: {login}");
            var known = current.IgnoredAccounts.FirstOrDefault(account =>
                account.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
            var author = current.Items.Values.SelectMany(item => item.Activities).FirstOrDefault(activity =>
                string.Equals(activity.AuthorLogin, login, StringComparison.OrdinalIgnoreCase));
            if (known?.Id is not null || author?.AuthorId is not null)
                ignored.Add(new(login, known?.Id ?? author?.AuthorId));
            else
            {
                try
                {
                    var user = await client.GetUserAsync(login, ct);
                    ignored.Add(new(user.Login, user.Id));
                }
                catch (HttpRequestException)
                {
                    ignored.Add(new(login));
                    unresolved.Add(login);
                }
            }
        }
        await MutateAsync(account => account.IgnoredAccounts = ignored, ct, startGeneration);
        if (unresolved.Count > 0 && generation == startGeneration)
        {
            Progress = $"Saved login-based ignores offline; account IDs could not be resolved for {string.Join(", ", unresolved)}.";
            Changed?.Invoke();
        }
    }

    public Task PollAsync(CancellationToken ct = default) => RefreshAsync(ct, full: false);

    public Task CheckForUpdatesAsync(CancellationToken ct = default) =>
        RefreshAsync(ct, full: false, reconcileDue: false);

    public Task RefreshAsync(CancellationToken ct = default, bool full = true, bool reconcileDue = true)
    {
        lock (refreshGate)
        {
            if (refreshTask is { IsCompleted: false } && refreshGeneration == generation)
            {
                if (full && !refreshIsFull) fullRefreshRequested = true;
                return refreshTask;
            }
            refreshGeneration = generation;
            refreshIsFull = full;
            fullRefreshRequested = full;
            return refreshTask = RunRefreshAsync(ct, generation, reconcileDue);
        }
    }

    private void Report(string message, int expectedGeneration)
    {
        if (generation != expectedGeneration)
            return;
        Progress = message;
        Changed?.Invoke();
    }

    private async Task RunRefreshAsync(CancellationToken ct, int expectedGeneration, bool reconcileDue)
    {
        await Task.Yield();
        while (generation == expectedGeneration)
        {
            bool full;
            lock (refreshGate)
            {
                full = fullRefreshRequested;
                fullRefreshRequested = false;
                refreshIsFull = full;
            }
            await RunRefreshPassAsync(ct, full, reconcileDue);
            lock (refreshGate)
            {
                if (generation != expectedGeneration) return;
                if (fullRefreshRequested) continue;
                refreshTask = null;
                return;
            }
        }
    }

    private async Task RunRefreshPassAsync(CancellationToken ct, bool full, bool reconcileDue)
    {
        var current = Account ?? throw new InvalidOperationException("Sign in first.");
        var startGeneration = generation;
        var startedAt = clock.GetUtcNow();
        var failures = 0;
        var originals = current.Queries.ToDictionary(query => query.Id);
        var fullQueryIds = current.Queries.Where(query => full || query.LastRefreshedAt is null ||
                query.Error is not null || query.LastRefreshedAt > startedAt ||
                reconcileDue && ((query.LastFullRefreshedAt ?? query.LastRefreshedAt) > startedAt ||
                    startedAt - (query.LastFullRefreshedAt ?? query.LastRefreshedAt) >= FullRefreshInterval))
            .Select(query => query.Id).ToHashSet();
        HashSet<string> activityIds = full || reconcileDue
            ? fullQueryIds.SelectMany(id => originals[id].ItemIds).ToHashSet()
            : [];
        var queries = current.Queries.OrderByDescending(query => query.Id == PreferredQueryId).Select(query =>
            new SavedQuery
            {
                Id = query.Id,
                Expression = fullQueryIds.Contains(query.Id) ? query.Expression :
                    $"({QueryParser.Parse(query.Expression, current.User.Login).Search}) " +
                    $"updated:>={(query.LastRefreshedAt!.Value - SearchOverlap).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ} sort:updated-desc"
            }).ToArray();
        foreach (var batch in queries.Chunk(GitHubBatching.SearchesPerRequest))
        {
            ct.ThrowIfCancellationRequested();
            if (generation != startGeneration)
                return;
            var fullCount = batch.Count(query => fullQueryIds.Contains(query.Id));
            Report($"Checking {batch.Length} saved queries together ({fullCount} full searches)...", startGeneration);
            IReadOnlyDictionary<string, BatchResult<SearchResult>> results;
            try
            {
                results = await client.SearchBatchAsync(batch, current.User.Login, ct);
            }
            catch (Exception exception) when (BatchErrors.IsRecoverable(exception, ct))
            {
                results = batch.ToDictionary(query => query.Id, _ => new BatchResult<SearchResult>(null, exception));
            }
            await MutateAsync(account =>
            {
                foreach (var query in batch)
                {
                    var original = originals[query.Id];
                    var target = account.Queries.FirstOrDefault(value => value.Id == query.Id && value.Expression == original.Expression);
                    if (target is null) continue;
                    var response = results.TryGetValue(query.Id, out var fetched) ? fetched :
                        throw new InvalidDataException("The query batch did not return a result for every saved query.");
                    if (response.Error is { } error)
                    {
                        failures++;
                        target.Error = error.Message;
                        continue;
                    }
                    var result = response.Value ?? throw new InvalidDataException("The query batch returned an empty result.");
                    var fullQuery = fullQueryIds.Contains(query.Id);
                    var incomplete = result.IsIncomplete || !fullQuery && result.TotalCount > 1000;
                    foreach (var incoming in result.Items)
                    {
                        if (account.Items.TryGetValue(incoming.Id, out var existing))
                        {
                            if (fullQuery && (full || reconcileDue) || incoming.UpdatedAt != existing.UpdatedAt ||
                                existing.ActivityCheckedAt is null || incoming.UpdatedAt >= existing.ActivityCheckedAt)
                                activityIds.Add(incoming.Id);
                            incoming.Activities = existing.Activities;
                            incoming.AcknowledgedIds = existing.AcknowledgedIds;
                            incoming.IsMarkedUnread = existing.IsMarkedUnread;
                            incoming.ManuallyUnreadIds = existing.ManuallyUnreadIds;
                            incoming.IsFlagged = existing.IsFlagged;
                            incoming.FlaggedActivityIds = existing.FlaggedActivityIds;
                            incoming.HasBaseline = existing.HasBaseline;
                            incoming.ActivityCheckedAt = existing.ActivityCheckedAt;
                            incoming.ActivityError = existing.ActivityError;
                        }
                        else activityIds.Add(incoming.Id);
                        account.Items[incoming.Id] = incoming;
                    }
                    if (fullQuery && !result.IsIncomplete)
                        target.ItemIds = result.Items.Select(item => item.Id).Distinct().ToList();
                    else
                    {
                        var merged = result.Items.Select(item => item.Id).Concat(target.ItemIds).Distinct()
                            .OrderByDescending(id => account.Items.GetValueOrDefault(id)?.UpdatedAt ?? DateTimeOffset.MinValue).ToArray();
                        target.ItemIds = merged.Take(1000).ToList();
                        target.IsIncomplete |= merged.Length > 1000;
                    }
                    if (fullQuery)
                    {
                        target.TotalCount = result.TotalCount;
                        target.IsIncomplete = result.IsIncomplete || result.TotalCount > 1000;
                    }
                    if (!incomplete)
                    {
                        target.LastFullRefreshedAt = fullQuery ? startedAt : target.LastFullRefreshedAt ?? original.LastRefreshedAt;
                        target.LastRefreshedAt = startedAt;
                    }
                    else failures++;
                    target.IsIncomplete |= incomplete;
                    target.Error = incomplete ? "Search was incomplete or exceeded its recent-update limit; previous results and refresh watermark retained." : null;
                }
            }, ct, startGeneration);
            if (PauseForBatchErrors(results.Values.Select(result => result.Error).ToArray(), startGeneration))
                return;
        }
        if (generation != startGeneration || Account is null)
            return;
        var itemIds = Account.Queries.OrderByDescending(query => query.Id == PreferredQueryId)
            .SelectMany(query => query.ItemIds)
            .Concat(Account.Items.Values.Where(item => item.HasFollowUps).Select(item => item.Id)).Distinct()
            .Where(id => Account.Items.TryGetValue(id, out var item) &&
                (full || activityIds.Contains(id) ||
                 !item.HasBaseline || item.ActivityError is not null || item.ActivityCheckedAt is null ||
                 item.UpdatedAt >= item.ActivityCheckedAt || item.ActivityCheckedAt > startedAt ||
                 reconcileDue && startedAt - item.ActivityCheckedAt >= FullRefreshInterval))
            .OrderByDescending(id => id == PreferredItemId).ToArray();
        var reconciliation = full || reconcileDue && (fullQueryIds.Count > 0 ||
            itemIds.Any(id => startedAt - Account.Items[id].ActivityCheckedAt >= FullRefreshInterval));
        for (var index = 0; index < itemIds.Length; index += GitHubBatching.ItemsPerRequest)
        {
            ct.ThrowIfCancellationRequested();
            if (generation != startGeneration || Account is null)
                return;
            var batch = itemIds.Skip(index).Take(GitHubBatching.ItemsPerRequest)
                .Where(Account.Items.ContainsKey).Select(id => Account.Items[id]).ToArray();
            Report($"{(reconciliation ? "Full reconciliation" : "New/updated items")}: " +
                $"checking activity {index + 1}-{index + batch.Length} of {itemIds.Length} together...", startGeneration);
            IReadOnlyDictionary<string, BatchResult<IReadOnlyList<Activity>>> results;
            try
            {
                results = await client.GetActivityBatchAsync(batch, ct);
            }
            catch (Exception exception) when (BatchErrors.IsRecoverable(exception, ct))
            {
                results = batch.ToDictionary(item => item.Id, _ => new BatchResult<IReadOnlyList<Activity>>(null, exception));
            }
            await MutateAsync(account =>
            {
                foreach (var item in batch)
                {
                    if (!account.Items.TryGetValue(item.Id, out var target)) continue;
                    var response = results.TryGetValue(item.Id, out var fetched) ? fetched :
                        throw new InvalidDataException("The activity batch did not return a result for every item.");
                    if (response.Error is { } error)
                    {
                        failures++;
                        target.ActivityError = error.Message;
                    }
                    else
                        ReadStatePolicy.ApplyActivity(target, response.Value ??
                            throw new InvalidDataException("The activity batch returned an empty result."), clock.GetUtcNow());
                }
            }, ct, startGeneration);
            if (PauseForBatchErrors(results.Values.Select(result => result.Error).ToArray(), startGeneration))
                return;
        }
        Report(failures == 0 ? $"Checked at {clock.GetLocalNow():t}. Refresh/F5 checks recent changes; full reconciliation runs every 30 minutes." :
            $"Refresh finished with {failures} error(s). Some cached results are stale.", startGeneration);
    }

    private bool PauseForBatchErrors(IReadOnlyList<Exception?> errors, int expectedGeneration)
    {
        if (generation != expectedGeneration) return true;
        var authentication = errors.OfType<GitHubException>().FirstOrDefault(error => error.StatusCode == 401);
        if (authentication is not null)
        {
            Report($"Refresh paused: {authentication.Message}", expectedGeneration);
            throw authentication;
        }
        var blocked = errors.OfType<GitHubException>().FirstOrDefault(error =>
            error.StatusCode == 429 || error.StatusCode == 403 && errors.All(value => value is not null));
        if (blocked is null) return false;
        Report($"Refresh paused: {blocked.Message}", expectedGeneration);
        return true;
    }
}
