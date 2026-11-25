using System.Text;
using System.Text.Json;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.Vocabulary;

namespace Game.Tests.Client;

/// <summary>T-706, the creation screen's view model and its place in the account flow: defaults, switching gender, random, what is sent, refusals, and what Back keeps.</summary>
public class CharacterCreatorTests
{
    private static readonly ClientAppearance Options = new(
        [
            new ClientGender("male", "Male", "model.m", [new("face.m1", "One", "model.x"), new("face.m2", "Two", "model.x"), new("face.both", "Both", "model.x")],
                [new("hair.m1", "One", "model.x"), new("hair.m2", "Two", "model.x")], "face.m2", "hair.m1"),
            new ClientGender("female", "Female", "model.f", [new("face.f1", "One", "model.x"), new("face.both", "Both", "model.x")],
                [new("hair.f1", "One", "model.x"), new("hair.f2", "Two", "model.x")], "face.f1", "hair.f2"),
        ],
        [new("skin.a", "A", "#804020"), new("skin.b", "B", "#FFE0C0")], "skin.b", [new("haircolor.a", "A", "#202020"), new("haircolor.b", "B", "#D0A040"), new("haircolor.c", "C", "#FF0000")], "haircolor.a", "job.start");

    /// <summary>Gives the numbers it was told, in order.</summary>
    private sealed class Scripted : IRandom
    {
        private readonly Queue<int> _numbers;

        public Scripted(params int[] numbers) => _numbers = new Queue<int>(numbers);

        public List<int> Bounds { get; } = new();

        public uint NextUInt() => throw new NotSupportedException();

        public double NextDouble() => throw new NotSupportedException();

        public int Next(int maxExclusive)
        {
            Bounds.Add(maxExclusive);
            return _numbers.Dequeue();
        }
    }

    private static CharacterCreator Create(IRandom? random = null) => new(Options, random ?? new Scripted());

    // ---- defaults ----

    [Fact]
    public void It_starts_with_the_first_genders_defaults_and_the_default_colours()
    {
        var creator = Create();
        creator.Look.ShouldBe(new AppearanceRecord("male", "skin.b", "face.m2", "hair.m1", "haircolor.a"));
        creator.Choice.ShouldBe(new AppearanceChoice("male", "skin.b", "face.m2", "hair.m1", "haircolor.a"));
        (creator.Name, creator.StartJob).ShouldBe((string.Empty, "job.start"));
        creator.Faces.Count.ShouldBe(3);
    }

    [Fact]
    public void Without_options_only_the_name_is_chosen_and_the_server_uses_its_defaults()
    {
        var creator = new CharacterCreator(null, new Scripted());
        creator.Randomize();
        creator.SelectGender("male").ShouldBeFalse();
        creator.SetName("Ada");
        (creator.Look, creator.Choice, creator.Name, creator.Genders.Count).ShouldBe((null, null, "Ada", 0));
    }

    [Fact]
    public void A_default_the_options_do_not_have_is_the_first_option()
    {
        var odd = new CharacterCreator(Options with { DefaultSkinToneId = "skin.gone", Genders = [Options.Genders[0] with { DefaultFaceId = "face.gone" }] }, new Scripted());
        (odd.Skin, odd.Face).ShouldBe(("skin.a", "face.m1"));
    }

    // ---- choices ----

    [Fact]
    public void Only_what_is_offered_can_be_picked_and_each_change_counts_once()
    {
        var creator = Create();
        var version = creator.Version;
        creator.SelectFace("face.f1").ShouldBeFalse("the other gender's");
        creator.SelectHair("hair.f1").ShouldBeFalse();
        creator.SelectSkin("haircolor.a").ShouldBeFalse();
        creator.SelectHairColor("skin.a").ShouldBeFalse();
        creator.SelectGender("other").ShouldBeFalse();
        creator.SelectFace("face.m2").ShouldBeTrue("already chosen");
        creator.Version.ShouldBe(version);
        creator.SelectFace("face.m1").ShouldBeTrue();
        creator.Version.ShouldBe(version + 1);
    }

    [Fact]
    public void Switching_gender_keeps_what_fits_and_switching_back_finds_the_rest_again()
    {
        var creator = Create();
        creator.SelectSkin("skin.a");
        creator.SelectHairColor("haircolor.c");
        creator.SelectHair("hair.m2");

        creator.SelectGender("female").ShouldBeTrue();
        creator.Look.ShouldBe(new AppearanceRecord("female", "skin.a", "face.f1", "hair.f2", "haircolor.c"), "the colours stay, the face and hair are her defaults");
        creator.SelectHair("hair.f1");
        creator.SelectGender("male");
        creator.Look.ShouldBe(new AppearanceRecord("male", "skin.a", "face.m2", "hair.m2", "haircolor.c"), "as it was");
        creator.SelectGender("female");
        creator.Hair.ShouldBe("hair.f1", "and hers too");
    }

    [Fact]
    public void A_face_both_genders_have_stays_through_the_switch()
    {
        var creator = Create();
        creator.SelectFace("face.both");
        creator.SelectGender("female");
        creator.Face.ShouldBe("face.both");
    }

    // ---- random ----

    [Fact]
    public void Random_picks_every_part_with_the_injected_numbers_and_keeps_the_name()
    {
        var random = new Scripted(1, 0, 1, 0, 2);
        var creator = Create(random);
        creator.SetName("Ada");
        creator.Randomize();
        creator.Look.ShouldBe(new AppearanceRecord("female", "skin.a", "face.f1", "hair.f2", "haircolor.c"));
        creator.Name.ShouldBe("Ada");
        random.Bounds.ShouldBe([2, 2, 2, 2, 3], "every option can come up");
    }

    [Fact]
    public void Random_looks_are_always_ones_that_can_be_picked()
    {
        var creator = Create(new Pcg32Random(706));
        var seen = new HashSet<AppearanceRecord>();
        for (var i = 0; i < 400; i++)
        {
            creator.Randomize();
            creator.Faces.ShouldContain(f => f.Id == creator.Face);
            creator.Hairstyles.ShouldContain(h => h.Id == creator.Hair);
            seen.Add(creator.Look!);
        }
        seen.Count.ShouldBeGreaterThan(50, "of the 60 there are");
    }

    // ---- the flow ----

    private sealed class FakeTransport : IAccountTransport
    {
        private AccountHttpResponse? _answer;

        public List<AccountHttpRequest> Sent { get; } = new();

        public void Send(AccountHttpRequest request) => Sent.Add(request);

        public bool TryDequeue(out AccountHttpResponse response)
        {
            response = _answer ?? default;
            if (_answer is null) return false;
            _answer = null;
            return true;
        }

        public void Answer(int status, string body) => _answer = new AccountHttpResponse(status, Encoding.UTF8.GetBytes(body));
    }

    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Session = "{\"token\":\"" + Token + "\",\"expires_in\":86400}";
    private const string Empty = "{\"slots\":3,\"name_min\":3,\"name_max\":16,\"characters\":[]}";

    private static (AccountFlow Flow, FakeTransport Http) Creating()
    {
        var http = new FakeTransport();
        var flow = new AccountFlow(new ClientConfig { ServerUrl = "ws://127.0.0.1:7780/ws", AllowCleartext = true }, http, Create());
        flow.SignIn("ada", "secret-password");
        http.Answer(200, Session);
        flow.Update();
        http.Answer(200, Empty);
        flow.Update();
        flow.OpenCreation();
        flow.Screen.ShouldBe(AccountScreen.Creation);
        return (flow, http);
    }

    [Fact]
    public void The_chosen_look_is_sent_with_the_name()
    {
        var (flow, http) = Creating();
        flow.Creator!.SelectGender("female");
        flow.Creator.SelectSkin("skin.a");
        flow.Create("Ada", flow.Creator.Choice);
        var body = JsonDocument.Parse(http.Sent[^1].Body!).RootElement;
        body.GetProperty("name").GetString().ShouldBe("Ada");
        var look = body.GetProperty("appearance");
        (look.GetProperty("gender").GetString(), look.GetProperty("skin").GetString(), look.GetProperty("face").GetString(), look.GetProperty("hair").GetString(), look.GetProperty("hair_color").GetString())
            .ShouldBe(("female", "skin.a", "face.f1", "hair.f2", "haircolor.a"));
    }

    [Theory]
    [InlineData(AccountErrors.InvalidName)]
    [InlineData(AccountErrors.InvalidAppearance)]
    [InlineData(AccountErrors.NameTaken)]
    [InlineData(AccountErrors.NoFreeSlot)]
    [InlineData(AccountErrors.RateLimited)]
    [InlineData(AccountErrors.Unavailable)]
    public void A_refusal_stays_on_the_screen_says_why_and_keeps_the_choices(string error)
    {
        var (flow, http) = Creating();
        flow.Creator!.SelectGender("female");
        flow.Creator.SetName("Ada");
        var chosen = flow.Creator.Look;
        flow.Create("Ada", flow.Creator.Choice);
        flow.Busy.ShouldBeTrue();
        http.Answer(error == AccountErrors.RateLimited ? 429 : 400, "{\"error\":\"" + error + "\"}");
        flow.Update();
        (flow.Screen, flow.Notice, flow.Error, flow.Busy).ShouldBe((AccountScreen.Creation, AccountNotice.Refused, error, false));
        UiText.Account(flow.Notice, flow.Error, flow.RetryAfterSeconds).ShouldNotBeNullOrWhiteSpace();
        (flow.Creator.Look, flow.Creator.Name).ShouldBe((chosen, "Ada"));
    }

    [Fact]
    public void An_unreachable_server_is_said_too_and_the_next_try_clears_it()
    {
        var (flow, http) = Creating();
        flow.Create("Ada", flow.Creator!.Choice);
        http.Answer(0, string.Empty);
        flow.Update();
        (flow.Screen, flow.Notice).ShouldBe((AccountScreen.Creation, AccountNotice.Unreachable));
        flow.Create("Ada", flow.Creator.Choice);
        flow.Notice.ShouldBe(AccountNotice.None);
    }

    [Fact]
    public void Back_and_again_keeps_what_was_chosen_and_forgets_the_refusal()
    {
        var (flow, http) = Creating();
        flow.Creator!.SelectHair("hair.m2");
        flow.Creator.SetName("Ada");
        flow.Create("Ada", flow.Creator.Choice);
        http.Answer(400, "{\"error\":\"name_taken\"}");
        flow.Update();
        flow.CloseCreation();
        (flow.Screen, flow.Notice).ShouldBe((AccountScreen.Characters, AccountNotice.None));
        flow.OpenCreation();
        (flow.Screen, flow.Creator.Hair, flow.Creator.Name).ShouldBe((AccountScreen.Creation, "hair.m2", "Ada"));
    }

    [Fact]
    public void While_a_request_runs_the_screen_stays()
    {
        var (flow, _) = Creating();
        flow.Create("Ada", flow.Creator!.Choice);
        flow.CloseCreation();
        flow.Screen.ShouldBe(AccountScreen.Creation);
    }

    [Fact]
    public void A_created_character_leads_back_to_the_list_and_the_next_one_starts_from_the_defaults()
    {
        var (flow, http) = Creating();
        flow.Creator!.SelectGender("female");
        flow.Creator.SetName("Ada");
        flow.Create("Ada", flow.Creator.Choice);
        http.Answer(201, "{\"id\":\"c1\"}");
        flow.Update();
        http.Answer(200, "{\"slots\":3,\"name_min\":3,\"name_max\":16,\"characters\":[{\"id\":\"c1\",\"slot\":0,\"name\":\"Ada\",\"job\":\"job.x\",\"appearance\":"
            + "{\"gender\":\"female\",\"skin\":\"skin.b\",\"face\":\"face.f1\",\"hair\":\"hair.f2\",\"hair_color\":\"haircolor.a\"}}]}");
        flow.Update();
        flow.Screen.ShouldBe(AccountScreen.Characters);
        flow.Characters[0].Appearance.ShouldBe(new AppearanceRecord("female", "skin.b", "face.f1", "hair.f2", "haircolor.a"));
        (flow.Creator.Gender, flow.Creator.Name).ShouldBe(("male", string.Empty));
    }

    [Fact]
    public void A_session_that_ends_keeps_the_choices_and_signing_out_drops_them()
    {
        var (flow, http) = Creating();
        flow.Creator!.SetName("Ada");
        flow.Create("Ada", flow.Creator.Choice);
        http.Answer(401, "{\"error\":\"invalid_session\"}");
        flow.Update();
        (flow.Screen, flow.Notice, flow.Creator.Name).ShouldBe((AccountScreen.SignIn, AccountNotice.SessionEnded, "Ada"));

        (flow, _) = Creating();
        flow.Creator!.SetName("Ada");
        flow.CloseCreation();
        flow.SignOut();
        flow.Creator.Name.ShouldBeEmpty();
    }

    [Fact]
    public void The_screen_opens_only_from_the_character_list()
    {
        var flow = new AccountFlow(new ClientConfig(), null, Create());
        flow.OpenCreation();
        flow.Screen.ShouldBe(AccountScreen.SignIn);
    }

    [Fact]
    public void The_chosen_option_is_marked_in_words_as_well()
    {
        UiText.Chosen("Fair").ShouldContain("Fair");
        UiText.Chosen("Fair").ShouldNotBe("Fair");
    }
}
