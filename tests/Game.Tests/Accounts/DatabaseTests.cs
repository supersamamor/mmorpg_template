using System.Diagnostics;
using Game.Server.Accounts;
using Game.Server.Net;
using Game.Server.Persistence;
using Game.Shared.Accounts;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Network;
using Game.Tests.Server;
using Game.Tests.World;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Tests.Accounts;

/// <summary>
/// A test that needs a real PostgreSQL (category <c>Database</c>). Without one it is skipped and says why; with <see cref="RequiredVariable"/> set (CI) it runs, and
/// fails, instead. The tests make databases of their own on that server and drop them, and never touch the one the connection string names.
/// </summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    /// <summary>The environment variable with the server's connection string.</summary>
    public const string ConnectionVariable = "GAME_TEST_DB_CONNECTION";

    /// <summary>Set to 1 where a missing database must be a failure, not a skip.</summary>
    public const string RequiredVariable = "GAME_TEST_DB_REQUIRED";

    public DatabaseFactAttribute()
    {
        if (Connection is null && Environment.GetEnvironmentVariable(RequiredVariable) != "1")
            Skip = $"No PostgreSQL configured: set {ConnectionVariable} to a server's connection string (docker compose up -d postgres).";
    }

    public static string? Connection => Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } text ? text : null;
}

/// <summary>A database of its own for a test class: created by the migrations, dropped at the end.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly List<string> _names = new();

    public GameDatabase Database { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>A connection string for a new, not yet existing database.</summary>
    public string NewConnectionString()
    {
        var server = DatabaseFactAttribute.Connection ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set, and " + DatabaseFactAttribute.RequiredVariable + " says it must be.");
        var name = "mmo_test_" + Guid.NewGuid().ToString("N");
        lock (_names) _names.Add(name);
        return new NpgsqlConnectionStringBuilder(server) { Database = name, MaxPoolSize = 40 }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        if (DatabaseFactAttribute.Connection is null) return; // the tests are skipped, or fail in NewConnectionString
        ConnectionString = NewConnectionString();
        Database = new GameDatabase(ConnectionString);
        await Database.MigrateAsync();
    }

    /// <summary>
    /// Drops the class's databases. T-808: the pool is closed first, so no connection of this class is still busy, and a drop may take up to <see cref="DropTimeoutSeconds"/>:
    /// a drop waits for a checkpoint, and on a loaded machine with other classes writing that overran Npgsql's 30 s default (a class clean-up error).
    /// </summary>
    public async Task DisposeAsync()
    {
        if (Database is not null) await Database.DisposeAsync();   // the data source: its connections close (ClearAllPools does not reach a data source's pool)
        if (DatabaseFactAttribute.Connection is not { } server) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false, CommandTimeout = DropTimeoutSeconds }.ConnectionString);
        await admin.OpenAsync();
        foreach (var name in _names)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Longest one database drop may take.</summary>
    public const int DropTimeoutSeconds = 300;
}

/// <summary>T-701 against a real PostgreSQL: the migration, the schema checks, what the unique indexes decide under concurrency, and the sessions' transactions.</summary>
[Trait("Category", "Database")]
public sealed class DatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private PostgresAccountStore Store => new(fixture.Database ?? throw new InvalidOperationException(DatabaseFactAttribute.ConnectionVariable + " is not set."));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A unique character name (letters only).</summary>
    private static string UniqueName() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 12));

    private async Task<Guid> AccountAsync(string hash = "hash")
    {
        var id = Guid.NewGuid();
        (await Store.TryCreateAccountAsync(id, Unique("u"), hash, Now, default)).ShouldBeTrue();
        return id;
    }

    private Task<(CreateCharacterOutcome Outcome, CharacterEntry? Character)> CreateAsync(Guid account, string name, int slots = 3) =>
        Store.CreateCharacterAsync(account, Guid.NewGuid(), name, CharacterNameRules.Key(name), "job.x", slots, null, Now, default);

    private static byte[] Token() => SessionTokens.HashOf(SessionTokens.New())!;

    // ---- migration and schema ----

    [DatabaseFact]
    public async Task The_migration_makes_the_schema_and_matches_the_model()
    {
        (await fixture.Database.CheckSchemaAsync()).ShouldBe(SchemaState.UpToDate);
        await using var db = fixture.Database.Open();
        db.Database.HasPendingModelChanges().ShouldBeFalse("the model changed without a migration");
        var tables = await db.Database.SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name").ToListAsync();
        tables.ShouldBe(["__EFMigrationsHistory", "account_deletions", "account_sanctions", "account_sessions", "accounts", "admin_audit", "admin_items", "admin_staff", "character_items", "character_outfits",
            "character_states", "characters", "chat_blocks", "chat_reports", "guild_members", "guild_ranks", "guild_storage_holds", "guild_storage_items", "guild_storage_log", "guilds", "server_runs", "support_requests"]);
        var indexes = await db.Database.SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexdef LIKE 'CREATE UNIQUE%'").ToListAsync();
        indexes.ShouldContain(GameDb.UsernameIndex);
        indexes.ShouldContain(GameDb.CharacterNameIndex);
        indexes.ShouldContain(GameDb.CharacterSlotIndex);
        indexes.ShouldContain(GameDb.ItemKey);
        indexes.ShouldContain(GameDb.ItemSlotIndex);
        indexes.ShouldContain(GameDb.GuildNameIndex);   // T-904
        indexes.ShouldContain(GameDb.GuildMemberIndex);
    }

    [DatabaseFact]
    public async Task A_missing_schema_is_refused_outside_development_and_made_in_it()
    {
        await using var database = new GameDatabase(fixture.NewConnectionString());
        (await database.CheckSchemaAsync()).ShouldBe(SchemaState.Missing);
        var log = new ListLog();
        (await Should.ThrowAsync<DatabaseException>(() => database.PrepareAsync(development: false, migrate: false, log))).Message.ShouldContain("--migrate");
        (await database.CheckSchemaAsync()).ShouldBe(SchemaState.Missing, "nothing was applied");
        await database.PrepareAsync(development: true, migrate: false, log);
        (await database.CheckSchemaAsync()).ShouldBe(SchemaState.UpToDate);
        await database.PrepareAsync(development: false, migrate: false, log);
    }

    [DatabaseFact]
    public async Task The_migrate_command_applies_it_outside_development()
    {
        await using var database = new GameDatabase(fixture.NewConnectionString());
        await database.PrepareAsync(development: false, migrate: true, new ListLog());
        (await database.CheckSchemaAsync()).ShouldBe(SchemaState.UpToDate);
    }

    [DatabaseFact]
    public async Task A_newer_schema_is_refused_everywhere()
    {
        await using var database = new GameDatabase(fixture.NewConnectionString());
        await database.MigrateAsync();
        await using (var db = database.Open())
            await db.Database.ExecuteSqlAsync($"INSERT INTO \"__EFMigrationsHistory\" VALUES ('99990101000000_Later', '99.0.0')");
        (await database.CheckSchemaAsync()).ShouldBe(SchemaState.Newer);
        foreach (var (development, migrate) in new[] { (true, false), (false, false), (false, true), (true, true) })
            (await Should.ThrowAsync<DatabaseException>(() => database.PrepareAsync(development, migrate, new ListLog()))).Message.ShouldContain("newer");
    }

    [DatabaseFact]
    public async Task A_wrong_password_is_reported_without_the_connection_string()
    {
        var text = new NpgsqlConnectionStringBuilder(fixture.NewConnectionString()) { Password = "hunter2-wrong" }.ConnectionString;
        await using var database = new GameDatabase(text);
        var error = await Record.ExceptionAsync(() => database.CheckSchemaAsync());
        if (error is null) return; // a server with trust authentication (CI) accepts any password
        error.ShouldBeOfType<DatabaseException>().ToString().ShouldNotContain("hunter2");
    }

    [DatabaseFact]
    public async Task The_server_refuses_to_start_on_a_missing_schema_migrates_on_command_and_then_gets_past_it()
    {
        var connection = fixture.NewConnectionString();
        var password = new NpgsqlConnectionStringBuilder(connection).Password;
        var (code, output) = await RunServerAsync(connection, "--environment", "Production");
        code.ShouldBe(1);
        output.ShouldContain("no schema yet");
        (code, output) = await RunServerAsync(connection, "--environment", "Production", "--migrate");
        code.ShouldBe(0, output);
        output.ShouldContain("migrations applied");
        (code, output) = await RunServerAsync(connection, "--environment", "Production");
        code.ShouldBe(1);
        output.ShouldContain("schema is up to date");
        output.ShouldContain("needs a TLS certificate"); // the next check: there is none here
        (code, output) = await RunServerAsync(connection, "--environment", "Development", "--net-port", "0", "--duration", "1");
        code.ShouldBe(0, output);
        output.ShouldContain("account endpoints");
        if (password is { Length: > 0 }) output.ShouldNotContain(password);
        output.ShouldNotContain(connection);
    }

    internal static async Task<(int Code, string Output)> RunServerAsync(string? connection, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepoRoot() };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Game.Server.dll"));
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment[GameDatabase.ConnectionVariable] = connection ?? string.Empty;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        return (process.ExitCode, await output + await error);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found");
    }

    // ---- accounts ----

    [DatabaseFact]
    public async Task A_username_has_one_account_also_when_many_register_at_once()
    {
        var name = Unique("race");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Store.TryCreateAccountAsync(Guid.NewGuid(), name, "hash", Now, default)));
        results.Count(r => r).ShouldBe(1);
        (await Store.FindAccountAsync(name, default)).ShouldNotBeNull();
        (await Store.FindAccountAsync(name + "x", default)).ShouldBeNull();
    }

    // ---- sessions ----

    [DatabaseFact]
    public async Task A_session_lives_until_it_expires_or_is_deleted()
    {
        var account = await AccountAsync();
        var token = Token();
        (await Store.TryAddSessionAsync(account, "hash", null, token, Now, Now.AddHours(1), 10, default)).ShouldNotBeNull();
        (await Store.FindSessionAsync(token, Now.AddMinutes(59), default)).ShouldBe(account);
        (await Store.FindSessionAsync(token, Now.AddHours(1), default)).ShouldBeNull();
        (await Store.FindSessionAsync(Token(), Now, default)).ShouldBeNull();
        (await Store.DeleteSessionAsync(token, default)).ShouldBeTrue();
        (await Store.DeleteSessionAsync(token, default)).ShouldBeFalse();
        (await Store.FindSessionAsync(token, Now, default)).ShouldBeNull();
    }

    [DatabaseFact]
    public async Task Only_the_hash_of_a_token_is_stored()
    {
        var time = new FakeTime();
        var service = new AccountService(Store, new FakeHasher(), HashQueue.Default(), new AccountLimits(), TestGrids.SampleContent.Balance, TestGrids.SampleContent.Appearance, new ManualClock(), time, new ListLog());
        var token = (await service.RegisterAsync(Unique("u"), ServiceRig.Password, "a", default)).Value!.Token;
        await using var db = fixture.Database.Open();
        var row = await db.Sessions.SingleAsync(s => s.TokenHash == SessionTokens.HashOf(token));
        row.TokenHash.Length.ShouldBe(32);
        row.ExpiresAt.ShouldBe(time.Now.UtcDateTime.AddHours(24));
        (await db.Accounts.SingleAsync(a => a.Id == row.AccountId)).PasswordHash.ShouldNotContain(ServiceRig.Password);
    }

    [DatabaseFact]
    public async Task Expired_and_surplus_sessions_go_at_the_next_sign_in()
    {
        var account = await AccountAsync();
        var other = await AccountAsync();
        var tokens = Enumerable.Range(0, 6).Select(_ => Token()).ToList();
        (await Store.TryAddSessionAsync(other, "hash", null, tokens[5], Now, Now.AddHours(9), 2, default)).ShouldBeEmpty();
        await Store.TryAddSessionAsync(account, "hash", null, tokens[0], Now, Now.AddMinutes(1), 2, default);
        var pushedOut = new List<byte[]>();
        for (var i = 1; i <= 4; i++) pushedOut.AddRange((await Store.TryAddSessionAsync(account, "hash", null, tokens[i], Now.AddMinutes(i), Now.AddHours(9), 2, default))!);
        pushedOut.Select(Convert.ToHexString).ShouldBe([Convert.ToHexString(tokens[1]), Convert.ToHexString(tokens[2])], "the live ones pushed out, not the expired one"); // T-710
        await using var db = fixture.Database.Open();
        (await db.Sessions.CountAsync(s => s.AccountId == account)).ShouldBe(2);
        (await Store.FindSessionAsync(tokens[3], Now.AddMinutes(5), default)).ShouldBe(account);
        (await Store.FindSessionAsync(tokens[4], Now.AddMinutes(5), default)).ShouldBe(account);
        (await Store.FindSessionAsync(tokens[5], Now.AddMinutes(5), default)).ShouldBe(other);
    }

    [DatabaseFact]
    public async Task A_session_is_not_added_once_the_password_has_changed_and_a_rehash_is_stored()
    {
        var account = await AccountAsync("old");
        (await Store.TryAddSessionAsync(account, "other", null, Token(), Now, Now.AddHours(1), 10, default)).ShouldBeNull();
        var token = Token();
        (await Store.TryAddSessionAsync(account, "old", "rehashed", token, Now, Now.AddHours(1), 10, default)).ShouldBeEmpty();
        (await Store.FindAccountAsync(account, default))!.PasswordHash.ShouldBe("rehashed");
        (await Store.TryChangePasswordAsync(account, "old", "new", Now, default)).ShouldBeNull();
        (await Store.TryChangePasswordAsync(account, "rehashed", "new", Now, default))!.Single().ShouldBe(token);
        (await Store.FindSessionAsync(token, Now, default)).ShouldBeNull("a password change ends every session");
        (await Store.TryAddSessionAsync(account, "rehashed", null, Token(), Now, Now.AddHours(1), 10, default)).ShouldBeNull();
    }

    [DatabaseFact]
    public async Task No_session_survives_a_password_change_that_races_its_sign_in()
    {
        for (var round = 0; round < 20; round++)
        {
            var account = await AccountAsync("old");
            await RacingSignIns.CheckAsync(fixture.Database, account,
                token => Store.TryAddSessionAsync(account, "old", null, token, Now, Now.AddHours(1), 100, default),
                () => Store.TryChangePasswordAsync(account, "old", "new", Now, default),
                async () => (await Store.FindAccountAsync(account, default))!.PasswordHash == "new", $"round {round}");
        }
    }

    // ---- characters ----

    [DatabaseFact]
    public async Task The_database_decides_who_gets_a_name_when_many_ask_at_once()
    {
        var accounts = new List<Guid>();
        for (var i = 0; i < 20; i++) accounts.Add(await AccountAsync());
        var name = UniqueName()[..8];
        var results = await Task.WhenAll(accounts.Select((a, i) => CreateAsync(a, i % 2 == 0 ? name.ToUpperInvariant() : name.ToLowerInvariant())));
        results.Count(r => r.Outcome == CreateCharacterOutcome.Created).ShouldBe(1);
        results.Count(r => r.Outcome == CreateCharacterOutcome.NameTaken).ShouldBe(19);
    }

    [DatabaseFact]
    public async Task An_account_never_gets_more_characters_than_slots_when_it_creates_many_at_once()
    {
        var account = await AccountAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => CreateAsync(account, UniqueName())));
        results.Count(r => r.Outcome == CreateCharacterOutcome.Created).ShouldBe(3);
        results.Count(r => r.Outcome == CreateCharacterOutcome.NoFreeSlot).ShouldBe(9);
        (await Store.ListCharactersAsync(account, default)).Select(c => c.Slot).ShouldBe([0, 1, 2]);
    }

    [DatabaseFact]
    public async Task A_deleted_character_frees_its_slot_keeps_its_row_and_its_name()
    {
        var account = await AccountAsync();
        var other = await AccountAsync();
        var name = UniqueName()[..8];
        var first = (await CreateAsync(account, name)).Character!;
        (await CreateAsync(account, UniqueName())).Character!.Slot.ShouldBe(1);
        (await Store.FindCharacterAsync(other, first.Id, default)).ShouldBeNull();
        (await Store.DeleteCharacterAsync(other, first.Id, Now, default)).ShouldBe(DeleteCharacterOutcome.Unknown);
        (await Store.DeleteCharacterAsync(account, first.Id, Now, default)).ShouldBe(DeleteCharacterOutcome.Deleted);
        (await Store.DeleteCharacterAsync(account, first.Id, Now, default)).ShouldBe(DeleteCharacterOutcome.Unknown);
        (await Store.FindCharacterAsync(account, first.Id, default)).ShouldBeNull();
        (await CreateAsync(other, name)).Outcome.ShouldBe(CreateCharacterOutcome.NameTaken);
        (await CreateAsync(account, UniqueName())).Character!.Slot.ShouldBe(0);
        await using var db = fixture.Database.Open();
        (await db.Characters.SingleAsync(c => c.Id == first.Id)).DeletedAt.ShouldBe(Now);
    }

    [DatabaseFact]
    public async Task An_account_with_characters_is_not_deleted_by_accident()
    {
        var account = await AccountAsync();
        await CreateAsync(account, UniqueName());
        await using var db = fixture.Database.Open();
        await Should.ThrowAsync<PostgresException>(() => db.Accounts.Where(a => a.Id == account).ExecuteDeleteAsync());
    }

    // ---- the whole stack ----

    [DatabaseFact]
    public async Task Register_create_play_log_out_and_sign_in_again_on_the_real_stack()
    {
        var user = Unique("stack");
        await using var rig = await GatewayRig.StartAsync(accounts: new AccountRigOptions(Store, new Argon2idHasher(), new AccountLimits(), TimeProvider.System));
        using var http = new AccountHttp(rig.Endpoint);
        var token = await http.RegisterAsync(user);
        var id = await http.CreateAsync(token, UniqueName());
        var client = await rig.PlayAsync(token, id);
        await client.WaitForAsync<SnapshotMessage>();
        await client.SendAsync(new LogoutMessage());
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Logout);
        (await http.PostAsync(AccountPaths.Logout, token: token)).Status.ShouldBe(204);
        (await (await rig.PlayAsync(token, id)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused);
        var (status, body, _) = await http.PostAsync(AccountPaths.Login, new { username = user.ToUpperInvariant(), password = ServiceRig.Password });
        status.ShouldBe(200);
        token = body.GetProperty("token").GetString()!;
        (await http.SendAsync(HttpMethod.Get, AccountPaths.Characters, token: token)).Body.GetProperty("characters")[0].GetProperty("id").GetString().ShouldBe(id);
        var again = await rig.PlayAsync(token, id);
        await again.WaitForAsync<SnapshotMessage>();
    }

    [DatabaseFact]
    public async Task A_database_that_goes_away_is_unavailable_and_comes_back()
    {
        var text = fixture.NewConnectionString();
        await using var database = new GameDatabase(text);
        await database.MigrateAsync();
        var service = new AccountService(new PostgresAccountStore(database), new FakeHasher(), HashQueue.Default(), new AccountLimits(), TestGrids.SampleContent.Balance, TestGrids.SampleContent.Appearance, new ManualClock(), new FakeTime(), new ListLog());
        var user = Unique("u");
        (await service.RegisterAsync(user, ServiceRig.Password, "a", default)).Ok.ShouldBeTrue();
        var name = new NpgsqlConnectionStringBuilder(text).Database;
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(text) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        async Task Run(string sql)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            await command.ExecuteNonQueryAsync();
        }
        await Run($"ALTER DATABASE \"{name}\" RENAME TO \"{name}_away\"".Replace("ALTER", "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '" + name + "'; ALTER"));
        (await service.LoginAsync(user, ServiceRig.Password, "a", default)).Error.ShouldBe(AccountErrors.Unavailable);
        await Run($"ALTER DATABASE \"{name}_away\" RENAME TO \"{name}\"");
        AccountResult<NewSession> back = default;
        for (var i = 0; i < 5 && !(back = await service.LoginAsync(user, ServiceRig.Password, "a", default)).Ok; i++) await Task.Delay(200);
        back.Ok.ShouldBeTrue();
    }

    // ---- T-710 ----

    [DatabaseFact]
    public async Task Two_password_changes_at_once_one_wins_and_the_loser_learns_its_session_is_gone()
    {
        for (var round = 0; round < 10; round++)
        {
            var service = new AccountService(Store, new FakeHasher(), HashQueue.Default(), new AccountLimits(), TestGrids.SampleContent.Balance, TestGrids.SampleContent.Appearance, new ManualClock(), new FakeTime(), new ListLog());
            var user = Unique("u");
            var token = (await service.RegisterAsync(user, ServiceRig.Password, "a", default)).Value!.Token;
            var changes = await Task.WhenAll(
                Task.Run(() => service.ChangePasswordAsync(token, ServiceRig.Password, "the first new password", "a", default)),
                Task.Run(() => service.ChangePasswordAsync(token, ServiceRig.Password, "the second new password", "a", default)));
            changes.Count(c => c.Ok).ShouldBe(1, $"round {round}: {changes[0].Error}, {changes[1].Error}");
            changes.Single(c => !c.Ok).Error.ShouldBe(AccountErrors.InvalidSession);
            (await service.ListAsync(changes.Single(c => c.Ok).Value!.Token, default)).Ok.ShouldBeTrue();
            (await service.ListAsync(token, default)).Error.ShouldBe(AccountErrors.InvalidSession);
            var winner = changes[0].Ok ? "the first new password" : "the second new password";
            (await service.LoginAsync(user, winner, "a", default)).Ok.ShouldBeTrue();
        }
    }

    [DatabaseFact]
    public async Task Only_migrations_may_take_long()
    {
        await using var play = fixture.Database.Open();
        await using var migrate = fixture.Database.OpenForMigrations();
        play.Database.GetCommandTimeout().ShouldBe(GameDatabase.CommandTimeoutSeconds);
        migrate.Database.GetCommandTimeout().ShouldBe(GameDatabase.MigrationTimeoutSeconds);
    }
}

/// <summary>
/// Sign-ins racing a change that ends every session of the account (a password change, an erasure request), and what must hold afterwards. T-808: the eight sign-ins and the change
/// queue on the account row's lock, and on a loaded machine the last in line waited longer than the store's statement limit (<see cref="GameDatabase.CommandTimeoutSeconds"/>), so the
/// call threw <see cref="StoreUnavailableException"/> (the player sees "try again"). Nothing was left behind then, so that is not a session surviving. Here such a sign-in counts as
/// refused, and an unanswered change is tried again, as the player would. A timed-out commit may still have landed: a sign-in's session is then ended too, and a change's retry
/// names only the sessions left by then, so the list of ended sessions is checked only when the first try answered, and its exact size only when every sign-in was answered too.
/// </summary>
internal static class RacingSignIns
{
    public static async Task CheckAsync(GameDatabase database, Guid account, Func<byte[], Task<IReadOnlyList<byte[]>?>> signIn, Func<Task<IReadOnlyList<byte[]>?>> change,
        Func<Task<bool>> changed, string round)
    {
        var tokens = Enumerable.Range(0, 8).Select(_ => SessionTokens.HashOf(SessionTokens.New())!).ToList();
        var signIns = tokens.Select(t => AnsweredAsync(signIn(t))).ToList();
        var ending = AnsweredAsync(change());
        var answers = await Task.WhenAll(signIns);
        var (changeAnswered, ended) = await ending;
        if (!changeAnswered)
        {
            await change();   // the try again; it names only the sessions there are now, so the list is not checked
            ended = null;
        }
        (await changed()).ShouldBeTrue($"{round}: the change landed");

        var accepted = tokens.Where((_, i) => answers[i] is (true, not null)).Select(Convert.ToHexString).ToList();
        var unanswered = answers.Count(a => !a.Answered);
        if (ended is not null)
        {
            var names = ended.Select(Convert.ToHexString).ToList();
            names.ShouldBeSubsetOf(tokens.Select(Convert.ToHexString), $"{round}: only this round's sessions are ended");
            accepted.ShouldBeSubsetOf(names, $"{round}: every session that was accepted first is named as ended");   // T-710
            if (unanswered == 0) names.Count.ShouldBe(accepted.Count, $"{round}: and no other");
        }
        await using var db = database.Open();
        (await db.Sessions.CountAsync(s => s.AccountId == account)).ShouldBe(0, $"{round}: {accepted.Count} of 8 sign-ins were accepted first, {unanswered} not answered");
    }

    private static async Task<(bool Answered, IReadOnlyList<byte[]>? Value)> AnsweredAsync(Task<IReadOnlyList<byte[]>?> call)
    {
        try
        {
            return (true, await call);
        }
        catch (StoreUnavailableException)
        {
            return (false, null);
        }
    }
}
