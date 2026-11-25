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

    /// <summary>The primary key that keeps an item from being two characters'.</summary>
    public const string ItemKey = "pk_character_items";

    /// <summary>The unique index that keeps two items out of one equipment slot.</summary>
    public const string ItemSlotIndex = "ux_character_items_slot";

    /// <summary>Accounts.</summary>
    public DbSet<AccountRow> Accounts => Set<AccountRow>();

    /// <summary>Deletion requests and what came of them (T-703).</summary>
    public DbSet<AccountDeletionRow> AccountDeletions => Set<AccountDeletionRow>();

    /// <summary>Live sign-ins.</summary>
    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    /// <summary>The longest appearance id a column holds (T-705); ids are checked against the content before they are stored.</summary>
    public const int AppearanceIdLength = 128;

    /// <summary>Characters, deleted ones included.</summary>
    public DbSet<CharacterRow> Characters => Set<CharacterRow>();

    /// <summary>Server runs (T-702).</summary>
    public DbSet<ServerRunRow> Runs => Set<ServerRunRow>();

    /// <summary>Saved characters (T-702).</summary>
    public DbSet<CharacterStateRow> CharacterStates => Set<CharacterStateRow>();

    /// <summary>Their items (T-702).</summary>
    public DbSet<ItemRow> Items => Set<ItemRow>();

    /// <summary>The outfits they own (T-708).</summary>
    public DbSet<OutfitRow> Outfits => Set<OutfitRow>();

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
            e.Property(a => a.EraseAfter).HasColumnName("erase_after");
            e.HasIndex(a => a.Username).IsUnique().HasDatabaseName(UsernameIndex);
            e.HasIndex(a => a.EraseAfter).HasFilter("erase_after IS NOT NULL").HasDatabaseName("ix_accounts_erase_after");
        });
        model.Entity<AccountDeletionRow>(e =>
        {
            e.ToTable("account_deletions");
            e.HasKey(d => d.AccountId);
            e.Property(d => d.AccountId).HasColumnName("account_id").ValueGeneratedNever();
            e.Property(d => d.RequestedAt).HasColumnName("requested_at");
            e.Property(d => d.ErasedAt).HasColumnName("erased_at");
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
            e.Property(c => c.Gender).HasColumnName("gender").HasMaxLength(AppearanceIdLength);
            e.Property(c => c.SkinToneId).HasColumnName("skin_tone_id").HasMaxLength(AppearanceIdLength);
            e.Property(c => c.FaceId).HasColumnName("face_id").HasMaxLength(AppearanceIdLength);
            e.Property(c => c.HairstyleId).HasColumnName("hairstyle_id").HasMaxLength(AppearanceIdLength);
            e.Property(c => c.HairColorId).HasColumnName("hair_color_id").HasMaxLength(AppearanceIdLength);
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.Property(c => c.DeletedAt).HasColumnName("deleted_at");
            e.HasIndex(c => c.NameKey).IsUnique().HasDatabaseName(CharacterNameIndex);
            e.HasIndex(c => new { c.AccountId, c.Slot }).IsUnique().HasFilter("deleted_at IS NULL").HasDatabaseName(CharacterSlotIndex);
            // Restrict: an account with characters is removed deliberately (T-703), never by a cascade.
            e.HasOne<AccountRow>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ServerRunRow>(e =>
        {
            e.ToTable("server_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(r => r.StartedAt).HasColumnName("started_at");
            e.Property(r => r.SeenAt).HasColumnName("seen_at");
            e.Property(r => r.EndedAt).HasColumnName("ended_at");
        });
        model.Entity<CharacterStateRow>(e =>
        {
            e.ToTable("character_states", t =>
            {
                t.HasCheckConstraint("ck_character_states_currency", "currency >= 0");
                t.HasCheckConstraint("ck_character_states_counts", "claim >= 1 AND version >= 0 AND turn_in_sequence >= 0");
            });
            e.HasKey(s => s.CharacterId);
            e.Property(s => s.CharacterId).HasColumnName("character_id").ValueGeneratedNever();
            e.Property(s => s.Claim).HasColumnName("claim");
            e.Property(s => s.HeldBy).HasColumnName("held_by");
            e.Property(s => s.Version).HasColumnName("version");
            e.Property(s => s.SavedAt).HasColumnName("saved_at");
            e.Property(s => s.MapId).HasColumnName("map_id").HasMaxLength(128);
            e.Property(s => s.CellX).HasColumnName("cell_x");
            e.Property(s => s.CellY).HasColumnName("cell_y");
            e.Property(s => s.Facing).HasColumnName("facing").HasMaxLength(8);
            e.Property(s => s.Hp).HasColumnName("hp");
            e.Property(s => s.Sp).HasColumnName("sp");
            e.Property(s => s.SaveMapId).HasColumnName("save_map_id").HasMaxLength(128);
            e.Property(s => s.SaveCellX).HasColumnName("save_cell_x");
            e.Property(s => s.SaveCellY).HasColumnName("save_cell_y");
            e.Property(s => s.BaseLevel).HasColumnName("base_level");
            e.Property(s => s.BaseExp).HasColumnName("base_exp");
            e.Property(s => s.JobId).HasColumnName("job_id").HasMaxLength(128);
            e.Property(s => s.JobLevel).HasColumnName("job_level");
            e.Property(s => s.JobExp).HasColumnName("job_exp");
            e.Property(s => s.StatPoints).HasColumnName("stat_points");
            e.Property(s => s.SkillPoints).HasColumnName("skill_points");
            e.Property(s => s.Currency).HasColumnName("currency");
            e.Property(s => s.TurnInSequence).HasColumnName("turn_in_sequence");
            e.Property(s => s.AutopilotUsedMs).HasColumnName("autopilot_used_ms");
            e.Property(s => s.AutopilotDayStartedAt).HasColumnName("autopilot_day_started_at");
            e.Property(s => s.Stats).HasColumnName("stats").HasColumnType("jsonb");
            e.Property(s => s.Skills).HasColumnName("skills").HasColumnType("jsonb");
            e.Property(s => s.Quests).HasColumnName("quests").HasColumnType("jsonb");
            e.Property(s => s.Cooldowns).HasColumnName("cooldowns").HasColumnType("jsonb");
            e.Property(s => s.SetAside).HasColumnName("set_aside").HasColumnType("jsonb");
            e.Property(s => s.OutfitSlots).HasColumnName("outfit_slots").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");   // rows from before T-708 get the default
            e.Property(s => s.OutfitsHidden).HasColumnName("outfits_hidden").HasDefaultValue(false);
            e.HasOne<CharacterRow>().WithOne().HasForeignKey<CharacterStateRow>(s => s.CharacterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ServerRunRow>().WithMany().HasForeignKey(s => s.HeldBy).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => s.HeldBy).HasDatabaseName("ix_character_states_held_by").HasFilter("held_by IS NOT NULL");
        });
        model.Entity<OutfitRow>(e =>
        {
            e.ToTable("character_outfits");
            e.HasKey(o => new { o.CharacterId, o.OutfitId });
            e.Property(o => o.CharacterId).HasColumnName("character_id");
            e.Property(o => o.OutfitId).HasColumnName("outfit_id").HasMaxLength(128);
            e.HasOne<CharacterStateRow>().WithMany().HasForeignKey(o => o.CharacterId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ItemRow>(e =>
        {
            e.ToTable("character_items", t => t.HasCheckConstraint("ck_character_items_quantity", "quantity >= 1 AND refine >= 0"));
            e.HasKey(i => i.Id).HasName(ItemKey);
            e.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(i => i.CharacterId).HasColumnName("character_id");
            e.Property(i => i.ItemId).HasColumnName("item_id").HasMaxLength(128);
            e.Property(i => i.Quantity).HasColumnName("quantity");
            e.Property(i => i.Slot).HasColumnName("slot").HasMaxLength(32);
            e.Property(i => i.Cards).HasColumnName("cards");
            e.Property(i => i.Refine).HasColumnName("refine");
            e.Property(i => i.Aside).HasColumnName("aside").HasMaxLength(64);
            e.HasIndex(i => i.CharacterId).HasDatabaseName("ix_character_items_character_id");
            e.HasIndex(i => new { i.CharacterId, i.Slot }).IsUnique().HasFilter("slot IS NOT NULL").HasDatabaseName(ItemSlotIndex);
            e.HasOne<CharacterStateRow>().WithMany().HasForeignKey(i => i.CharacterId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>For <c>dotnet ef migrations add</c> only: it needs the model, not a database, so there is no address or password here.</summary>
public sealed class GameDbDesignTimeFactory : IDesignTimeDbContextFactory<GameDb>
{
    /// <inheritdoc />
    public GameDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<GameDb>().UseNpgsql().Options);
}
