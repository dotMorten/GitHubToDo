using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;

namespace GitHubToDo.Core;

public sealed class LocalStore
{
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1);

    public LocalStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            if (version > 2)
                throw new InvalidDataException("This data was created by a newer GitHubToDo version. Update the app before opening it.");
            if (version == 2) return connection;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS SavedQueries (Account TEXT NOT NULL, Id TEXT NOT NULL,
                    Position INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(Account,Id));
                CREATE TABLE IF NOT EXISTS Items (Account TEXT NOT NULL, Id TEXT NOT NULL,
                    Payload TEXT NOT NULL, PRIMARY KEY(Account,Id));
                CREATE TABLE IF NOT EXISTS IgnoredAccounts (Account TEXT NOT NULL, Login TEXT NOT NULL,
                    Payload TEXT NOT NULL, PRIMARY KEY(Account,Login));
                CREATE TABLE IF NOT EXISTS Accounts (Id TEXT PRIMARY KEY, Login TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS HttpCache (Key TEXT PRIMARY KEY, Payload TEXT NOT NULL);
                PRAGMA user_version = 2;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM Settings WHERE Key=$key";
            command.Parameters.AddWithValue("$key", key);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        finally { gate.Release(); }
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = value is null ? "DELETE FROM Settings WHERE Key=$key" :
                "INSERT INTO Settings VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            command.Parameters.AddWithValue("$key", key);
            if (value is not null)
                command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    private static string AccountKey(GitHubUser user) => $"github.com:{user.Id}";

    public async Task<AccountSnapshot> LoadAsync(GitHubUser user, CancellationToken cancellationToken = default)
    {
        AccountSnapshot? loaded = null;
        await foreach (var snapshot in LoadQueriesAsync(user, cancellationToken: cancellationToken))
            loaded = snapshot;
        return loaded!;
    }

    public async IAsyncEnumerable<AccountSnapshot> LoadQueriesAsync(GitHubUser user, string? preferredQueryId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await Task.Run(() => OpenAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var header = await Task.Run(async () =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM Accounts WHERE Id=$account)";
                command.Parameters.AddWithValue("$account", AccountKey(user));
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
                    return null;
                var account = new AccountSnapshot { User = user, Queries = [] };
                command.CommandText = "SELECT Payload FROM SavedQueries WHERE Account=$account ORDER BY Position";
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        account.Queries.Add(Deserialize(reader.GetString(0), CoreJsonContext.Default.SavedQuery));
                    }
                }
                command.CommandText = "SELECT Payload FROM IgnoredAccounts WHERE Account=$account";
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                        account.IgnoredAccounts.Add(Deserialize(reader.GetString(0), CoreJsonContext.Default.IgnoredAccount));
                }
                return account;
            }, cancellationToken).ConfigureAwait(false);
            if (header is null)
            {
                yield return new() { User = user, NeedsInitialSave = true };
                yield break;
            }

            header.IsLoading = true;
            yield return header;

            var loadedQueries = new HashSet<string>();
            var items = new Dictionary<string, TrackedItem>();
            foreach (var query in header.Queries.OrderBy(query => query.Id == preferredQueryId ? 0 : 1))
            {
                var previousItems = items;
                items = await Task.Run(async () =>
                {
                    var loaded = new Dictionary<string, TrackedItem>(previousItems);
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Payload FROM Items WHERE Account=$account AND Id=$id";
                    command.Parameters.AddWithValue("$account", AccountKey(user));
                    var id = command.Parameters.Add("$id", SqliteType.Text);
                    foreach (var itemId in query.ItemIds.Distinct())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (loaded.ContainsKey(itemId)) continue;
                        id.Value = itemId;
                        if (await command.ExecuteScalarAsync(cancellationToken) is string payload)
                        {
                            var item = Deserialize(payload, CoreJsonContext.Default.TrackedItem);
                            loaded.Add(item.Id, item);
                        }
                    }
                    return loaded;
                }, cancellationToken).ConfigureAwait(false);
                loadedQueries.Add(query.Id);
                yield return new()
                {
                    IsLoading = true,
                    LoadedQueryIds = new(loadedQueries),
                    User = user,
                    Queries = header.Queries,
                    Items = items,
                    IgnoredAccounts = header.IgnoredAccounts
                };
            }

            // Items outside saved queries can still carry follow-ups or read acknowledgements.
            var queryItems = items;
            var allItems = await Task.Run(async () =>
            {
                var loaded = new Dictionary<string, TrackedItem>(queryItems);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Payload FROM Items WHERE Account=$account";
                command.Parameters.AddWithValue("$account", AccountKey(user));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (loaded.ContainsKey(reader.GetString(0))) continue;
                    var item = Deserialize(reader.GetString(1), CoreJsonContext.Default.TrackedItem);
                    loaded.Add(item.Id, item);
                }
                return loaded;
            }, cancellationToken).ConfigureAwait(false);
            yield return new()
            {
                User = user,
                Queries = header.Queries,
                Items = allItems,
                IgnoredAccounts = header.IgnoredAccounts
            };
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AccountSnapshot account, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var key = AccountKey(account.User);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Accounts VALUES($account,$login) ON CONFLICT(Id) DO UPDATE SET Login=excluded.Login;
                DELETE FROM SavedQueries WHERE Account=$account;
                DELETE FROM IgnoredAccounts WHERE Account=$account;
                """;
            command.Parameters.AddWithValue("$account", key);
            command.Parameters.AddWithValue("$login", account.User.Login);
            await command.ExecuteNonQueryAsync(cancellationToken);
            for (var i = 0; i < account.Queries.Count; i++)
                await InsertAsync(connection, transaction,
                    "INSERT INTO SavedQueries VALUES($account,$id,$position,$payload)",
                    key, account.Queries[i].Id, account.Queries[i], CoreJsonContext.Default.SavedQuery, i, cancellationToken);
            foreach (var item in account.Items.Values)
                await InsertAsync(connection, transaction, """
                    INSERT INTO Items VALUES($account,$id,$payload)
                    ON CONFLICT(Account,Id) DO UPDATE SET Payload=excluded.Payload WHERE Items.Payload<>excluded.Payload
                    """,
                    key, item.Id, item, CoreJsonContext.Default.TrackedItem, null, cancellationToken);
            foreach (var ignored in account.IgnoredAccounts)
                await InsertAsync(connection, transaction, "INSERT INTO IgnoredAccounts VALUES($account,$id,$payload)",
                    key, ignored.Login.ToLowerInvariant(), ignored, CoreJsonContext.Default.IgnoredAccount, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    private static async Task InsertAsync<T>(SqliteConnection connection, SqliteTransaction transaction,
        string sql, string account, string id, T value, JsonTypeInfo<T> typeInfo, int? position, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, typeInfo));
        if (position.HasValue)
            command.Parameters.AddWithValue("$position", position.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HttpCacheEntry?> GetCacheAsync(string key, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM HttpCache WHERE Key=$key";
            command.Parameters.AddWithValue("$key", key);
            return await command.ExecuteScalarAsync(cancellationToken) is string json
                ? Deserialize(json, CoreJsonContext.Default.HttpCacheEntry) : null;
        }
        finally { gate.Release(); }
    }

    public async Task SetCacheAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO HttpCache VALUES($key,$payload) ON CONFLICT(Key) DO UPDATE SET Payload=excluded.Payload";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry, CoreJsonContext.Default.HttpCacheEntry));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    internal static AccountSnapshot Clone(AccountSnapshot value) =>
        Deserialize(JsonSerializer.Serialize(value, CoreJsonContext.Default.AccountSnapshot), CoreJsonContext.Default.AccountSnapshot);
    private static T Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo) ?? throw new InvalidDataException($"Invalid stored {typeof(T).Name}.");
}
