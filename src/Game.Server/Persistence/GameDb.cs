using Game.Shared.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Game.Server.Persistence;

/// <summary>
/// The player-state database (PostgreSQL; content is never in it). Create contexts with <see cref="GameDatabase.Open"/>, use one per operation, and never on a
/// tick thread. Table and column names are written out, so they do not change when a C# name does.
/// </summary>
public sealed class GameDb(DbContextOptions<GameDb> options) : DbContext(options)
{
    /// <summary>The unique index on an account's normalised username.</summary>
    public const string UsernameIndex = "ux_accounts_username";

    /// <summary>The unique index that decides who gets a character name.</summary>
    public const string CharacterNameIndex = "ux_characters_name_key";

    /// <summary>The unique index that keeps two live characters out of one slot.</summary>
    public const string CharacterSlotIndex = "ux_characters_account_slot";

    /// <summary>Accounts.</summary>
    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    /// <summary>Live sign-ins.</summary>
    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    /// <summary>Characters, deleted ones included.</summary>
    public DbSet<CharacterRow> Characters => Set<CharacterRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AccountRow>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(a => a.Username).HasColumnName("username").HasMaxLength(AccountRules.MaxUsernameLength);
            e.Property(a => a.PasswordHash).HasColumnName("password_hash").HasMaxLength(256);
            e.Property(a => a.CreatedAt).HasColumnName("created_at");
            e.Property(a => a.PasswordChangedAt).HasColumnName("password_changed_at");
            e.HasIndex(a => a.Username).IsUnique().HasDatabaseName(UsernameIndex);
        });
        model.Entity<SessionRow>(e =>
        {
            e.ToTable("account_sessions");
            e.HasKey(s => s.TokenHash);
            e.Property(s => s.TokenHash).HasColumnName("token_hash").HasMaxLength(32);
            e.Property(s => s.AccountId).HasColumnName("account_id");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            e.HasIndex(s => s.AccountId).HasDatabaseName("ix_account_sessions_account_id");
            e.HasOne<AccountRow>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<CharacterRow>(e =>
        {
            e.ToTable("characters", t => t.HasCheckConstraint("ck_characters_slot", "slot >= 0"));
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(c => c.AccountId).HasColumnName("account_id");
            e.Property(c => c.Slot).HasColumnName("slot");
            e.Property(c => c.Name).HasColumnName("name").HasMaxLength(CharacterNameRules.LongestAllowed);
            e.Property(c => c.NameKey).HasColumnName("name_key").HasMaxLength(CharacterNameRules.LongestAllowed);
            e.Property(c => c.JobId).HasColumnName("job_id").HasMaxLength(128);
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.Property(c => c.DeletedAt).HasColumnName("deleted_at");
            e.HasIndex(c => c.NameKey).IsUnique().HasDatabaseName(CharacterNameIndex);
            e.HasIndex(c => new { c.AccountId, c.Slot }).IsUnique().HasFilter("deleted_at IS NULL").HasDatabaseName(CharacterSlotIndex);
            // Restrict: an account with characters is removed deliberately (T-703), never by a cascade.
            e.HasOne<AccountRow>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>For <c>dotnet ef migrations add</c> only: it needs the model, not a database, so there is no address or password here.</summary>
public sealed class GameDbDesignTimeFactory : IDesignTimeDbContextFactory<GameDb>
{
    /// <inheritdoc />
    public GameDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<GameDb>().UseNpgsql().Options);
}
