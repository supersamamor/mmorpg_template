using Game.Server.Accounts;
using Game.Server.Persistence;
using Game.Shared.Accounts;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Server;
using Game.Tests.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Tests.Appearance;

/// <summary>T-705 against a real PostgreSQL: the looks are stored with the character, old characters get the defaults, and content that loses an id never costs the character its row.</summary>
[Trait("Category", "Database")]
public sealed class AppearanceDatabaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly AppearanceRecord WideLook = new(Genders.Female, "skin.deep", "face.female_sly", "hair.female_curls", "haircolor.ember");

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12];

    private static string UniqueName() => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>("abcdefghijklmnopqrstuvwxyz", 12));

    private static AccountService ServiceOver(PostgresAccountStore store, AppearanceDef def, ListLog log) =>
        new(store, new FakeHasher(), HashQueue.Default(), new AccountLimits(), TestGrids.SampleContent.Balance, def, new ManualClock(), new FakeTime(), log);

    [DatabaseFact]
    public async Task The_looks_are_stored_with_the_character_and_read_back_through_the_list_and_the_login()
    {
        var store = new PostgresAccountStore(fixture.Database);
        var service = ServiceOver(store, TestGrids.SampleContent.Appearance, new ListLog());
        var token = (await service.RegisterAsync(Unique("u"), ServiceRig.Password, "a", default)).Value!.Token;
        var made = (await service.CreateAsync(token, UniqueName(), new AppearanceChoice(Genders.Female, "skin.deep", "face.female_sly", "hair.female_curls", "haircolor.ember"), default)).Value!;
        var plain = (await service.CreateAsync(token, UniqueName(), default)).Value!;

        var list = (await service.ListAsync(token, default)).Value!.Characters;
        list.Single(c => c.Id == made.Id).Appearance.ShouldBe(WideLook);
        list.Single(c => c.Id == plain.Id).Appearance!.Gender.ShouldBe(Genders.Male);
        (await service.ResolveAsync(token, made.Id, "a", default)).Value!.Appearance.ShouldBe(WideLook);

        await using var db = fixture.Database.Open();
        var row = await db.Characters.SingleAsync(c => c.Id == Guid.Parse(made.Id));
        (row.Gender, row.SkinToneId, row.FaceId, row.HairstyleId, row.HairColorId).ShouldBe((Genders.Female, "skin.deep", "face.female_sly", "hair.female_curls", "haircolor.ember"));
    }

    [DatabaseFact]
    public async Task A_refused_choice_creates_no_row()
    {
        var store = new PostgresAccountStore(fixture.Database);
        var service = ServiceOver(store, TestGrids.SampleContent.Appearance, new ListLog());
        var token = (await service.RegisterAsync(Unique("u"), ServiceRig.Password, "a", default)).Value!.Token;
        var name = UniqueName();
        (await service.CreateAsync(token, name, new AppearanceChoice(Face: "face.nope"), default)).Error.ShouldBe(AccountErrors.InvalidAppearance);
        await using var db = fixture.Database.Open();
        (await db.Characters.AnyAsync(c => c.Name == name)).ShouldBeFalse();
        (await service.CreateAsync(token, name, default)).Ok.ShouldBeTrue();
    }

    [DatabaseFact]
    public async Task A_character_made_before_the_migration_gets_the_defaults_and_nothing_is_written_back()
    {
        await using var database = new GameDatabase(fixture.NewConnectionString());
        await using (var db = database.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("Characters");   // the schema of T-702: no appearance columns
            var account = Guid.NewGuid();
            var character = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"INSERT INTO accounts (id, username, password_hash, created_at, password_changed_at) VALUES ({account}, {Unique("old")}, 'x', now(), now())");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO characters (id, account_id, slot, name, name_key, job_id, created_at) VALUES ({character}, {account}, 0, 'Elder', 'elder', 'job.wanderer', now())
                """);
            await db.Database.MigrateAsync();   // the rest: T-705
            var store = new PostgresAccountStore(database);
            var found = (await store.FindCharacterAsync(account, character, default))!;
            found.Appearance.ShouldBe(new AppearanceChoice());
            var service = ServiceOver(store, TestGrids.SampleContent.Appearance, new ListLog());
            var token = SessionTokens.New();
            (await store.TryAddSessionAsync(account, "x", null, SessionTokens.HashOf(token)!, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), 5, default)).ShouldBeTrue();
            (await service.ListAsync(token, default)).Value!.Characters.Single().Appearance.ShouldBe(service.Looks.Defaults);
            (await service.ResolveAsync(token, character.ToString("D"), "a", default)).Value!.Appearance.ShouldBe(service.Looks.Defaults);
            var row = await db.Characters.AsNoTracking().SingleAsync(c => c.Id == character);
            (row.Gender, row.SkinToneId, row.FaceId, row.HairstyleId, row.HairColorId).ShouldBe((null, null, null, null, null), "the defaults are shown, not stored");
        }
    }

    [DatabaseFact]
    public async Task An_id_removed_from_the_content_is_shown_as_the_default_is_not_overwritten_and_comes_back_with_the_option()
    {
        var store = new PostgresAccountStore(fixture.Database);
        var def = TestGrids.SampleContent.Appearance;
        var log = new ListLog();
        var service = ServiceOver(store, def, log);
        var token = (await service.RegisterAsync(Unique("u"), ServiceRig.Password, "a", default)).Value!.Token;
        var id = (await service.CreateAsync(token, UniqueName(), new AppearanceChoice(Genders.Female, "skin.deep", "face.female_sly", "hair.female_curls", "haircolor.ember"), default)).Value!.Id;

        var cut = def with
        {
            Genders = def.Genders.ToDictionary(g => g.Key, g => g.Value with
            {
                Faces = g.Value.Faces.Where(f => f.Id != "face.female_sly").ToList(),
                Hairstyles = g.Value.Hairstyles.Where(h => h.Id != "hair.female_curls").ToList(),
            }),
            HairColors = def.HairColors.Where(c => c.Id != "haircolor.ember").ToList(),
        };
        var without = ServiceOver(store, cut, log);
        var shown = (await without.ResolveAsync(token, id, "a", default)).Value!.Appearance;
        shown.ShouldBe(new AppearanceRecord(Genders.Female, "skin.deep", "face.female_calm", "hair.female_bob", def.DefaultHairColorId));
        (await without.ListAsync(token, default)).Value!.Characters.Single().Appearance.ShouldBe(shown);
        log.Lines.Count(l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains(id)).ShouldBe(1, "once per character");

        await using var db = fixture.Database.Open();
        var row = await db.Characters.AsNoTracking().SingleAsync(c => c.Id == Guid.Parse(id));
        (row.FaceId, row.HairstyleId, row.HairColorId).ShouldBe(("face.female_sly", "hair.female_curls", "haircolor.ember"), "still stored as chosen");

        (await service.ResolveAsync(token, id, "a", default)).Value!.Appearance.ShouldBe(WideLook);
    }
}
