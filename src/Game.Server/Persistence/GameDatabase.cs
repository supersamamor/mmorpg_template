using Game.Server.Hosting;
using Game.Server.World;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Game.Server.Persistence;

/// <summary>How the database's schema relates to what this build knows.</summary>
public enum SchemaState
{
    /// <summary>Exactly this build's migrations are applied.</summary>
    UpToDate,

    /// <summary>The database does not exist, or no migration is applied.</summary>
    Missing,

    /// <summary>Some of this build's migrations are not applied.</summary>
    Behind,

    /// <summary>A migration this build does not know is applied: a newer server has used this database.</summary>
    Newer,
}

/// <summary>The database cannot be used; the message is safe to print (never the connection string, its password, or row data).</summary>
public sealed class DatabaseException(string message) : Exception(message);

/// <summary>
/// The server's one door to PostgreSQL: the connection pool, contexts (<see cref="Open"/>) and the schema check. The connection string comes from the
/// environment variable <see cref="ConnectionVariable"/> and is never logged.
/// </summary>
public sealed class GameDatabase : IAsyncDisposable
{
    /// <summary>The environment variable with the connection string. Not a command-line option: those show up in process listings.</summary>
    public const string ConnectionVariable = "GAME_DB_CONNECTION";

    /// <summary>Longest a single statement may take.</summary>
    public const int CommandTimeoutSeconds = 5;

    private readonly NpgsqlDataSource _source;
    private readonly DbContextOptions<GameDb> _options;

    /// <summary>Prepares the pool; connects to nothing yet. Throws <see cref="DatabaseException"/> when the connection string cannot be read.</summary>
    public GameDatabase(string connectionString)
    {
        try
        {
            // Error details would put row values (a username, a character name) into exception texts, and so into logs.
            var parsed = new NpgsqlConnectionStringBuilder(connectionString) { IncludeErrorDetail = false, LogParameters = false };
            if (string.IsNullOrWhiteSpace(parsed.Host)) throw new ArgumentException("no host");
            _source = new NpgsqlDataSourceBuilder(parsed.ConnectionString).Build();
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or InvalidCastException)
        {
            // Not the inner exception: its text may quote part of the string.
            throw new DatabaseException($"The connection string in {ConnectionVariable} cannot be read (expected: Host=...;Database=...;Username=...;Password=...).");
        }
        // Warnings about logging parameters and the like never arise: EF gets no logger at all.
        _options = new DbContextOptionsBuilder<GameDb>().UseNpgsql(_source, o => o.CommandTimeout(CommandTimeoutSeconds)).Options;
    }

    /// <summary>A new context, for one operation. Refuses on a tick thread (ARCHITECTURE §3).</summary>
    public GameDb Open()
    {
        TickThread.Forbid("Database work");
        return new GameDb(_options);
    }

    /// <summary>Compares the applied migrations with this build's. Throws <see cref="DatabaseException"/> when the database cannot be reached.</summary>
    public async Task<SchemaState> CheckSchemaAsync(CancellationToken cancellation = default)
    {
        await using var db = Open();
        List<string> applied;
        try
        {
            applied = (await db.Database.GetAppliedMigrationsAsync(cancellation).ConfigureAwait(false)).ToList();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return SchemaState.Missing;   // no such database
        }
        catch (Exception e) when (CauseOf(e) is { } cause)
        {
            throw Unusable(cause);
        }
        return Compare(db.Database.GetMigrations().ToList(), applied);
    }

    /// <summary>The schema's state from the migrations this build knows and the ones applied.</summary>
    public static SchemaState Compare(IReadOnlyCollection<string> known, IReadOnlyCollection<string> applied)
    {
        if (applied.Except(known, StringComparer.Ordinal).Any()) return SchemaState.Newer;
        if (applied.Count == 0) return SchemaState.Missing;
        return applied.Count < known.Count ? SchemaState.Behind : SchemaState.UpToDate;
    }

    /// <summary>Applies this build's missing migrations, creating the database if needed. Throws <see cref="DatabaseException"/> on failure.</summary>
    public async Task MigrateAsync(CancellationToken cancellation = default)
    {
        await using var db = Open();
        try
        {
            await db.Database.MigrateAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (CauseOf(e) is { } cause)
        {
            throw Unusable(cause);
        }
    }

    /// <summary>
    /// Start-up (<paramref name="migrate"/> false) or the migrate command (true): returns with an up-to-date schema, or throws <see cref="DatabaseException"/>
    /// saying what to do. Migrations are applied in development or by the command; a newer schema is always refused.
    /// </summary>
    public async Task PrepareAsync(bool development, bool migrate, IServerLog log, CancellationToken cancellation = default)
    {
        var state = await CheckSchemaAsync(cancellation).ConfigureAwait(false);
        if (state == SchemaState.Newer)
            throw new DatabaseException("The database schema is newer than this server: a newer build has migrated it. Run that build (or a newer one), or restore a backup.");
        if (state == SchemaState.UpToDate)
        {
            log.Info("Database: the schema is up to date.");
            return;
        }
        if (!development && !migrate)
            throw new DatabaseException((state == SchemaState.Missing ? "The database has no schema yet." : "The database schema is older than this server.") +
                                        " Apply the migrations first: run the server once with --migrate (same environment), then start it.");
        log.Info($"Database: the schema is {(state == SchemaState.Missing ? "missing" : "behind")}; applying migrations.");
        await MigrateAsync(cancellation).ConfigureAwait(false);
        log.Info("Database: migrations applied.");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _source.DisposeAsync();

    /// <summary>The database failure behind an exception (EF wraps some), or null when it is something else.</summary>
    internal static Exception? CauseOf(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
            if (current is NpgsqlException or TimeoutException) return current;
        return null;
    }

    private static DatabaseException Unusable(Exception cause) =>
        new("The database cannot be used: " + (cause is PostgresException p ? $"{p.SqlState} {p.MessageText}" : cause.Message));
}
