using System.Text;
using Game.Server.Accounts;
using Game.Server.World;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Appearance;

/// <summary>T-705: what a creation request may ask for, what a stored look turns into when the content changes, and the shape of the request body.</summary>
public class AppearanceRulesTests
{
    private static AppearanceDef Def => TestGrids.SampleContent.Appearance;

    private static AppearanceRules Rules(AppearanceDef? def = null, IServerLog? log = null) => new(def ?? Def, log);

    private static readonly AppearanceRecord Maleness = new(Genders.Male, "skin.light", "face.male_calm", "hair.male_crop", "haircolor.chestnut");

    private static List<string> Warnings(ListLog log) => log.Lines.Where(l => l.StartsWith("WARN", StringComparison.Ordinal)).ToList();

    // ---- creation: defaults ----

    [Fact]
    public void A_request_with_nothing_gets_the_first_gender_and_its_defaults()
    {
        Rules().DefaultGender.ShouldBe(Genders.All.Ordered[0]);
        Rules().Choose(null).ShouldBe(Maleness);
        Rules().Choose(new AppearanceChoice()).ShouldBe(Maleness);
        Rules().Defaults.ShouldBe(Maleness);
    }

    [Fact]
    public void A_gender_alone_gets_that_genders_own_face_and_hairstyle()
    {
        Rules().Choose(new AppearanceChoice(Gender: Genders.Female)).ShouldBe(new AppearanceRecord(Genders.Female, "skin.light", "face.female_calm", "hair.female_bob", "haircolor.chestnut"));
    }

    [Fact]
    public void A_part_left_out_is_the_default_and_a_part_given_is_kept()
    {
        Rules().Choose(new AppearanceChoice(Skin: "skin.deep", HairColor: "haircolor.ember", Hair: "hair.male_long"))
            .ShouldBe(new AppearanceRecord(Genders.Male, "skin.deep", "face.male_calm", "hair.male_long", "haircolor.ember"));
    }

    // ---- creation: every refusal ----

    public static TheoryData<AppearanceChoice> Refused() => new()
    {
        new AppearanceChoice(Gender: "other"),
        new AppearanceChoice(Gender: ""),
        new AppearanceChoice(Gender: "MALE"),
        new AppearanceChoice(Skin: "skin.nope"),
        new AppearanceChoice(Skin: "haircolor.raven"),          // an id of the other palette
        new AppearanceChoice(Face: "face.nope"),
        new AppearanceChoice(Face: "hair.male_crop"),
        new AppearanceChoice(Hair: "hair.nope"),
        new AppearanceChoice(Hair: "face.male_calm"),
        new AppearanceChoice(HairColor: "haircolor.nope"),
        new AppearanceChoice(HairColor: "skin.fair"),
        new AppearanceChoice(Face: "face.female_calm"),          // a face of the other gender (the gender is the default, male)
        new AppearanceChoice(Hair: "hair.female_bob"),
        new AppearanceChoice(Gender: Genders.Female, Face: "face.male_calm"),
        new AppearanceChoice(Gender: Genders.Female, Hair: "hair.male_crop"),
        new AppearanceChoice(Skin: new string('x', 100_000)),
        new AppearanceChoice(Face: "face.male_calm\0"),
        new AppearanceChoice(Face: " face.male_calm"),
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Anything_the_file_does_not_offer_is_refused(AppearanceChoice choice) => Rules().Choose(choice).ShouldBeNull();

    // ---- content changes ----

    private static AppearanceDef Without(string faceId, string? hairId = null, string? skinId = null, string? colorId = null, string? gender = null)
    {
        var genders = Def.Genders.ToDictionary(g => g.Key, g => g.Value with
        {
            Faces = g.Value.Faces.Where(f => f.Id != faceId).ToList(),
            Hairstyles = g.Value.Hairstyles.Where(h => h.Id != hairId).ToList(),
        });
        if (gender is not null) genders.Remove(gender);
        return Def with
        {
            Genders = genders,
            SkinTones = Def.SkinTones.Where(s => s.Id != skinId).ToList(),
            HairColors = Def.HairColors.Where(c => c.Id != colorId).ToList(),
        };
    }

    [Fact]
    public void A_removed_id_falls_back_to_the_genders_default_is_logged_once_and_comes_back_with_the_option()
    {
        var id = Guid.NewGuid();
        var stored = new AppearanceChoice(Genders.Male, "skin.deep", "face.male_wry", "hair.male_long", "haircolor.ember");
        var log = new ListLog();

        var cut = Rules(Without("face.male_wry", "hair.male_long", "skin.deep", "haircolor.ember"), log);
        cut.Resolve(id, stored).ShouldBe(Maleness);
        cut.Resolve(id, stored).ShouldBe(Maleness);
        Warnings(log).Count(w => w.Contains(id.ToString())).ShouldBe(1, "once per character");
        Warnings(log).Single().ShouldContain("face.male_wry");

        // The stored record is untouched, so the option back restores the look.
        Rules().Resolve(id, stored).ShouldBe(new AppearanceRecord(Genders.Male, "skin.deep", "face.male_wry", "hair.male_long", "haircolor.ember"));
    }

    [Fact]
    public void A_removed_gender_takes_the_default_gender_and_its_looks()
    {
        var stored = new AppearanceChoice(Genders.Female, "skin.fair", "face.female_sly", "hair.female_curls", "haircolor.silver");
        var log = new ListLog();
        var shown = Rules(Without("none", gender: Genders.Female), log).Resolve(Guid.NewGuid(), stored);
        shown.Gender.ShouldBe(Genders.Male);
        shown.Face.ShouldBe("face.male_calm");
        shown.Hair.ShouldBe("hair.male_crop");
        shown.Skin.ShouldBe("skin.fair");        // the palettes are shared, so what is still offered stays
        shown.HairColor.ShouldBe("haircolor.silver");
        Warnings(log).Count.ShouldBe(1);
    }

    [Fact]
    public void A_character_from_before_has_nothing_stored_and_gets_the_defaults_without_a_log_line()
    {
        var log = new ListLog();
        Rules(log: log).Resolve(Guid.NewGuid(), null).ShouldBe(Maleness);
        Rules(log: log).Resolve(Guid.NewGuid(), new AppearanceChoice()).ShouldBe(Maleness);
        Rules(log: log).Resolve(Guid.NewGuid(), new AppearanceChoice(Genders.Female))
            .ShouldBe(new AppearanceRecord(Genders.Female, "skin.light", "face.female_calm", "hair.female_bob", "haircolor.chestnut"));
        Warnings(log).ShouldBeEmpty();
    }

    // ---- the request body (a hostile client's JSON) ----

    private static CreateCharacterRequest? Parse(string json) => AccountEndpoints.ParseCreate(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void The_body_may_leave_the_appearance_out_or_null_or_partial()
    {
        Parse("""{"name":"Ada"}""").ShouldBe(new CreateCharacterRequest("Ada"));
        Parse("""{"name":"Ada","appearance":null}""").ShouldBe(new CreateCharacterRequest("Ada"));
        Parse("""{"name":"Ada","appearance":{}}""").ShouldBe(new CreateCharacterRequest("Ada", new AppearanceChoice()));
        Parse("""{"appearance":{"skin":"skin.tan","hair_color":null},"name":"Ada","extra":[1,2]}""").ShouldBe(new CreateCharacterRequest("Ada", new AppearanceChoice(Skin: "skin.tan")));
        Parse("""{"name":"Ada","appearance":{"gender":"female","skin":"s","face":"f","hair":"h","hair_color":"c"}}""")
            .ShouldBe(new CreateCharacterRequest("Ada", new AppearanceChoice("female", "s", "f", "h", "c")));
    }

    [Theory]
    [InlineData("""{"name":"Ada","appearance":"male"}""")]
    [InlineData("""{"name":"Ada","appearance":[]}""")]
    [InlineData("""{"name":"Ada","appearance":5}""")]
    [InlineData("""{"name":"Ada","appearance":{"gender":1}}""")]
    [InlineData("""{"name":"Ada","appearance":{"face":["a"]}}""")]
    [InlineData("""{"name":"Ada","appearance":{"face":{"x":1}}}""")]
    [InlineData("""{"name":"Ada","appearance":{"face":true}}""")]
    [InlineData("""{"name":"Ada","appearance":{"colour":"skin.tan"}}""")]
    [InlineData("""{"name":"Ada","appearance":{"face":"a","face":"b"}}""")]
    [InlineData("""{"name":"Ada","appearance":{},"appearance":{}}""")]
    [InlineData("""{"name":"Ada","appearance":{"a":{"b":{"c":{}}}}}""")]
    [InlineData("""{"name":"Ada","appearance":{"gender":"male"}""")]
    [InlineData("""{"appearance":{"gender":"male"}}""")]
    [InlineData("""{"name":5}""")]
    [InlineData("""[]""")]
    public void A_body_of_the_wrong_shape_is_no_request(string json) => Parse(json).ShouldBeNull();
}
