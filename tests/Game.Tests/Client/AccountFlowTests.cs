using System.Text;
using System.Text.Json;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Client;

/// <summary>T-701, the client: the sign-in and character-select flow, what it sends where, and what it keeps.</summary>
public class AccountFlowTests
{
    private sealed class FakeTransport : IAccountTransport
    {
        private AccountHttpResponse? _answer;

        public List<AccountHttpRequest> Sent { get; } = new();

        public AccountHttpRequest Last => Sent[^1];

        public void Send(AccountHttpRequest request) => Sent.Add(request);

        public bool TryDequeue(out AccountHttpResponse response)
        {
            response = _answer ?? default;
            if (_answer is null) return false;
            _answer = null;
            return true;
        }

        public void Answer(int status, string body = "") => _answer = new AccountHttpResponse(status, Encoding.UTF8.GetBytes(body));
    }

    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Session = "{\"token\":\"" + Token + "\",\"expires_in\":86400}";
    private const string Empty = "{\"slots\":3,\"name_min\":3,\"name_max\":16,\"characters\":[]}";
    private const string One = "{\"slots\":3,\"name_min\":3,\"name_max\":16,\"characters\":[{\"id\":\"c1\",\"slot\":0,\"name\":\"Lyra\",\"job\":\"job.x\"}]}";

    private static (AccountFlow Flow, FakeTransport Http) Create(string url = "ws://127.0.0.1:7780/ws", bool cleartext = true)
    {
        var http = new FakeTransport();
        return (new AccountFlow(new ClientConfig { ServerUrl = url, AllowCleartext = cleartext }, http), http);
    }

    private static (AccountFlow Flow, FakeTransport Http) SignedIn(string list = One)
    {
        var (flow, http) = Create();
        flow.SignIn("ada", "secret-password");
        http.Answer(200, Session);
        flow.Update();
        http.Answer(200, list);
        flow.Update();
        flow.Screen.ShouldBe(AccountScreen.Characters);
        return (flow, http);
    }

    [Fact]
    public void Signing_in_sends_the_credentials_once_then_lists_the_characters()
    {
        var (flow, http) = Create();
        flow.SignIn("Ada", "secret-password");
        flow.Busy.ShouldBeTrue();
        http.Last.Method.ShouldBe("POST");
        http.Last.Url.ShouldBe("http://127.0.0.1:7780/account/login");
        http.Last.Token.ShouldBeNull();
        var body = JsonDocument.Parse(http.Last.Body!).RootElement;
        body.GetProperty("username").GetString().ShouldBe("Ada");
        body.GetProperty("password").GetString().ShouldBe("secret-password");
        http.Answer(200, Session);
        flow.Update();
        flow.SignedIn.ShouldBeTrue();
        (http.Last.Method, http.Last.Url, http.Last.Token).ShouldBe(("GET", "http://127.0.0.1:7780/account/characters", Token));
        http.Answer(200, One);
        flow.Update();
        flow.Busy.ShouldBeFalse();
        flow.Screen.ShouldBe(AccountScreen.Characters);
        flow.Characters.Single().ShouldBe(new CharacterRecord("c1", 0, "Lyra", "job.x"));
        (flow.Slots, flow.NameMin, flow.NameMax).ShouldBe((3, 3, 16));
    }

    [Fact]
    public void Registering_uses_its_own_endpoint()
    {
        var (flow, http) = Create();
        flow.Register("ada", "secret-password");
        http.Last.Url.ShouldBe("http://127.0.0.1:7780/account/register");
        http.Answer(201, Session);
        flow.Update();
        flow.SignedIn.ShouldBeTrue();
    }

    [Fact]
    public void The_flow_keeps_no_password()
    {
        var (flow, _) = SignedIn();
        // Every field of the flow (and what its fields reach by string) is searched: only the token may remain.
        foreach (var field in typeof(AccountFlow).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            (field.GetValue(flow) as string ?? string.Empty).ShouldNotContain("secret-password");
    }

    [Theory]
    [InlineData(401, "invalid_credentials")]
    [InlineData(409, "username_taken")]
    [InlineData(400, "invalid_password")]
    [InlineData(503, "unavailable")]
    public void A_refusal_is_shown_and_the_screen_stays(int status, string error)
    {
        var (flow, http) = Create();
        flow.SignIn("ada", "secret-password");
        http.Answer(status, $"{{\"error\":\"{error}\"}}");
        flow.Update();
        (flow.Notice, flow.Error, flow.Screen, flow.Busy).ShouldBe((AccountNotice.Refused, error, AccountScreen.SignIn, false));
        UiText.Account(flow.Notice, flow.Error, 0).Length.ShouldBeGreaterThan(5);
        flow.SignIn("ada", "secret-password");
        flow.Notice.ShouldBe(AccountNotice.None, "a new try clears the old refusal");
    }

    [Fact]
    public void Rate_limits_say_how_long()
    {
        var (flow, http) = Create();
        flow.SignIn("ada", "secret-password");
        http.Answer(429, "{\"error\":\"rate_limited\",\"retry_after\":42}");
        flow.Update();
        flow.RetryAfterSeconds.ShouldBe(42);
        UiText.Account(flow.Notice, flow.Error, flow.RetryAfterSeconds).ShouldContain("42");
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(500, "<html>")]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    [InlineData(200, "[]")]
    [InlineData(302, "")]
    [InlineData(404, "{\"error\":7}")]
    public void Anything_that_is_no_answer_is_unreachable(int status, string body)
    {
        var (flow, http) = Create();
        flow.SignIn("ada", "secret-password");
        http.Answer(status, body);
        flow.Update();
        flow.Notice.ShouldBe(AccountNotice.Unreachable);
        flow.SignedIn.ShouldBeFalse();
        flow.Busy.ShouldBeFalse();
    }

    [Fact]
    public void One_request_at_a_time()
    {
        var (flow, http) = Create();
        flow.SignIn("ada", "secret-password");
        flow.SignIn("ada", "secret-password");
        flow.Register("ada", "secret-password");
        http.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Creating_sends_the_name_and_lists_again()
    {
        var (flow, http) = SignedIn(Empty);
        flow.Create("Lyra");
        (http.Last.Method, http.Last.Url, http.Last.Token).ShouldBe(("POST", "http://127.0.0.1:7780/account/characters", Token));
        JsonDocument.Parse(http.Last.Body!).RootElement.GetProperty("name").GetString().ShouldBe("Lyra");
        http.Answer(201, "{\"id\":\"c1\",\"slot\":0,\"name\":\"Lyra\",\"job\":\"job.x\"}");
        flow.Update();
        http.Last.Method.ShouldBe("GET");
        http.Answer(200, One);
        flow.Update();
        flow.Characters.Count.ShouldBe(1);
    }

    [Fact]
    public void Deleting_takes_a_second_press()
    {
        var (flow, http) = SignedIn();
        var sent = http.Sent.Count;
        flow.Delete("c1");
        (flow.Deleting, http.Sent.Count).ShouldBe(("c1", sent));
        flow.KeepCharacter();
        flow.Deleting.ShouldBeNull();
        flow.Delete("c1");
        flow.Delete("c1");
        (http.Last.Method, http.Last.Url).ShouldBe(("DELETE", "http://127.0.0.1:7780/account/characters/c1"));
        http.Answer(409, "{\"error\":\"character_in_use\"}");
        flow.Update();
        flow.Error.ShouldBe(AccountErrors.CharacterInUse);
        flow.Screen.ShouldBe(AccountScreen.Characters);
    }

    [Fact]
    public void An_ended_session_leads_back_to_sign_in_and_drops_the_token()
    {
        var (flow, http) = SignedIn();
        flow.Refresh();
        http.Answer(401, "{\"error\":\"invalid_session\"}");
        flow.Update();
        (flow.Screen, flow.SignedIn, flow.SessionToken, flow.Notice).ShouldBe((AccountScreen.SignIn, false, null, AccountNotice.SessionEnded));
        flow.Characters.ShouldBeEmpty();
        flow.Refresh();
        flow.Busy.ShouldBeFalse("nothing to ask without a session");
    }

    [Fact]
    public void Signing_out_drops_the_token_at_once_whatever_the_server_says()
    {
        var (flow, http) = SignedIn();
        flow.SignOut();
        (http.Last.Url, http.Last.Token).ShouldBe(("http://127.0.0.1:7780/account/logout", Token));
        (flow.SignedIn, flow.Screen).ShouldBe((false, AccountScreen.SignIn));
        http.Answer(0);
        flow.Update();
        (flow.Busy, flow.Notice).ShouldBe((false, AccountNotice.None));
    }

    // ---- transport policy ----

    [Theory]
    [InlineData("wss://game.example/ws", false, "https://game.example")]
    [InlineData("wss://game.example:8443/ws?x=1", false, "https://game.example:8443")]
    [InlineData("WSS://game.example", false, "https://game.example")]
    [InlineData("ws://127.0.0.1:7780/ws", true, "http://127.0.0.1:7780")]
    [InlineData("ws://127.0.0.1:7780/ws", false, null)]
    [InlineData("wss://good.example@evil.example/ws", true, null)]
    [InlineData("http://game.example/ws", true, null)]
    [InlineData("", true, null)]
    public void The_account_endpoints_follow_the_transport_policy(string url, bool cleartext, string? expected) => TransportPolicy.AccountBase(url, cleartext).ShouldBe(expected);

    [Fact]
    public void A_release_build_sends_no_credentials_over_plain_HTTP()
    {
        var (flow, http) = Create("ws://game.example/ws", cleartext: false);
        flow.SignIn("ada", "secret-password");
        flow.Register("ada", "secret-password");
        http.Sent.ShouldBeEmpty();
        flow.Notice.ShouldBe(AccountNotice.Unreachable);
    }

    // ---- words ----

    [Fact]
    public void Every_account_error_has_words_of_its_own_and_the_longest_is_the_one_the_layout_check_uses()
    {
        var other = UiText.Account(AccountNotice.Refused, "something new", 0);
        foreach (var error in AccountErrors.All.Ordered)
        {
            var text = UiText.Account(AccountNotice.Refused, error, 86_400);
            text.ShouldNotBeNullOrWhiteSpace(error);
            if (error != AccountErrors.InvalidRequest) text.ShouldNotBe(other, error);
            text.Length.ShouldBeLessThanOrEqualTo(UiText.LongestAccount.Length, error);
        }
    }

    // ---- T-710: the password screen ----

    [Fact]
    public void A_password_change_sends_both_passwords_once_and_keeps_the_new_session()
    {
        var (flow, http) = SignedIn();
        flow.OpenPassword();
        flow.Screen.ShouldBe(AccountScreen.Password);
        flow.ChangePassword("old-password-1", "new-password-1", "new-password-1").ShouldBeTrue();
        (http.Last.Method, http.Last.Url, http.Last.Token).ShouldBe(("POST", "http://127.0.0.1:7780/account/password", Token));
        var body = JsonDocument.Parse(http.Last.Body!).RootElement;
        body.EnumerateObject().Select(p => (p.Name, p.Value.GetString())).ShouldBe([("current", "old-password-1"), ("new", "new-password-1")]);
        flow.ChangePassword("a", "b", "b").ShouldBeFalse("one request at a time");
        var renewed = new string('B', 43);
        http.Answer(200, "{\"token\":\"" + renewed + "\",\"expires_in\":86400}");
        flow.Update();
        (flow.Screen, flow.Notice, flow.SessionToken).ShouldBe((AccountScreen.Characters, AccountNotice.PasswordChanged, renewed));
        flow.Characters.Count.ShouldBe(1);
        UiText.Account(flow.Notice, flow.Error, 0).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_repeat_that_differs_is_never_sent()
    {
        var (flow, http) = SignedIn();
        flow.OpenPassword();
        var sent = http.Sent.Count;
        flow.ChangePassword("old-password-1", "new-password-1", "new-password-2").ShouldBeFalse();
        http.Sent.Count.ShouldBe(sent);
        (flow.Screen, flow.Notice).ShouldBe((AccountScreen.Password, AccountNotice.PasswordsDiffer));
        UiText.Account(flow.Notice, flow.Error, 0).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(401, "invalid_credentials")]
    [InlineData(400, "same_password")]
    [InlineData(400, "invalid_password")]
    [InlineData(429, "rate_limited")]
    public void A_refused_change_stays_on_the_screen_and_signed_in(int status, string error)
    {
        var (flow, http) = SignedIn();
        flow.OpenPassword();
        flow.ChangePassword("old-password-1", "new-password-1", "new-password-1");
        http.Answer(status, "{\"error\":\"" + error + "\"}");
        flow.Update();
        (flow.Screen, flow.Notice, flow.Error, flow.SessionToken).ShouldBe((AccountScreen.Password, AccountNotice.Refused, error, Token));
        flow.ClosePassword();
        (flow.Screen, flow.Notice).ShouldBe((AccountScreen.Characters, AccountNotice.None));
    }

    [Fact]
    public void A_change_whose_session_has_ended_goes_back_to_signing_in()
    {
        var (flow, http) = SignedIn();
        flow.OpenPassword();
        flow.ChangePassword("old-password-1", "new-password-1", "new-password-1");
        http.Answer(401, "{\"error\":\"invalid_session\"}");
        flow.Update();
        (flow.Screen, flow.Notice, flow.SignedIn).ShouldBe((AccountScreen.SignIn, AccountNotice.SessionEnded, false));
        flow.OpenPassword();
        flow.Screen.ShouldBe(AccountScreen.SignIn);
        flow.ChangePassword("old-password-1", "new-password-1", "new-password-1").ShouldBeFalse("not signed in");
    }
}
