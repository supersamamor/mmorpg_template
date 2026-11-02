using System.Buffers.Text;
using Game.Server.Net;
using Game.Server.World;
using Game.Shared.Protocol;

namespace Game.Tests.Network;

/// <summary>The resume token store, the failed-resume guard and their options (T-210), without sockets.</summary>
public class ResumeTokenTests
{
    private static readonly EntityId Ada = new(7), Bo = new(8);

    [Fact]
    public void Tokens_are_256_random_bits_in_base64url_and_never_repeat()
    {
        var tokens = new ResumeTokens();
        var issued = Enumerable.Range(1, 200).Select(i => tokens.Issue(new EntityId(i))).ToList();
        issued.Distinct().Count().ShouldBe(200);
        foreach (var token in issued)
        {
            token.ShouldMatch("^[A-Za-z0-9_-]{43}$");
            Base64Url.DecodeFromChars(token).Length.ShouldBe(ResumeTokens.TokenBytes);
        }
        tokens.Count.ShouldBe(200);
    }

    [Fact]
    public void A_token_finds_its_character_and_nothing_else_does()
    {
        var tokens = new ResumeTokens();
        var ada = tokens.Issue(Ada);
        var bo = tokens.Issue(Bo);
        tokens.Find(ada).ShouldBe(Ada);
        tokens.Find(bo).ShouldBe(Bo);
        tokens.Find(ada[..42] + (ada[42] == 'A' ? 'B' : 'A')).ShouldBeNull();
        tokens.Find("").ShouldBeNull();
        tokens.Find(ada.ToLowerInvariant() == ada ? ada.ToUpperInvariant() : ada.ToLowerInvariant()).ShouldBeNull();
    }

    [Fact]
    public void Rotation_is_single_use_and_bound_to_the_character()
    {
        var tokens = new ResumeTokens();
        var first = tokens.Issue(Ada);
        tokens.TryRotate(first, Bo, out _).ShouldBeFalse();       // another character's token
        tokens.Find(first).ShouldBe(Ada);                          // a refused rotation changes nothing

        tokens.TryRotate(first, Ada, out var second).ShouldBeTrue();
        second.ShouldNotBe(first);
        tokens.Find(second).ShouldBe(Ada);
        tokens.Find(first).ShouldBe(Ada);                          // not acknowledged yet: still the fallback
        tokens.Acknowledge(Ada).ShouldBeTrue();
        tokens.Find(first).ShouldBeNull();
        tokens.TryRotate(first, Ada, out _).ShouldBeFalse();       // used
        tokens.Count.ShouldBe(1);
    }

    [Fact]
    public void An_unacknowledged_resume_can_be_redone_with_the_old_token_which_kills_the_lost_one()
    {
        var tokens = new ResumeTokens();
        var first = tokens.Issue(Ada);
        tokens.TryRotate(first, Ada, out var lost).ShouldBeTrue();  // its welcome never arrived
        tokens.HasUnacknowledged(Ada).ShouldBeTrue();

        tokens.TryRotate(first, Ada, out var third).ShouldBeTrue(); // the client only has the first one
        tokens.Find(lost).ShouldBeNull();                           // issued in between, reached nobody who spoke with it
        tokens.Find(third).ShouldBe(Ada);
        tokens.Find(first).ShouldBe(Ada);
        tokens.Count.ShouldBe(2);

        tokens.Acknowledge(Ada).ShouldBeTrue();                     // the new session spoke
        tokens.Acknowledge(Ada).ShouldBeFalse();
        tokens.Find(first).ShouldBeNull();
        tokens.TryRotate(first, Ada, out _).ShouldBeFalse();
        tokens.Find(third).ShouldBe(Ada);
        tokens.Count.ShouldBe(1);

        // Redeeming the newest token before acknowledging keeps it as the fallback; revoking kills both.
        tokens.TryRotate(third, Ada, out var fourth).ShouldBeTrue();
        tokens.Find(third).ShouldBe(Ada);
        tokens.Revoke(Ada).ShouldBeTrue();
        tokens.Find(third).ShouldBeNull();
        tokens.Find(fourth).ShouldBeNull();
        tokens.HasUnacknowledged(Ada).ShouldBeFalse();
        tokens.Count.ShouldBe(0);
    }

    [Fact]
    public void Issuing_again_or_revoking_kills_the_old_token()
    {
        var tokens = new ResumeTokens();
        var first = tokens.Issue(Ada);
        var second = tokens.Issue(Ada);
        tokens.Find(first).ShouldBeNull();
        tokens.Find(second).ShouldBe(Ada);
        tokens.Revoke(Ada).ShouldBeTrue();
        tokens.Find(second).ShouldBeNull();
        tokens.Revoke(Ada).ShouldBeFalse();
        tokens.Count.ShouldBe(0);
    }

    [Fact]
    public void A_fingerprint_is_short_stable_and_not_the_token()
    {
        var token = new ResumeTokens().Issue(Ada);
        var print = ResumeTokens.Fingerprint(token);
        print.ShouldMatch("^#[0-9a-f]{6}$");
        ResumeTokens.Fingerprint(token).ShouldBe(print);
        token.ShouldNotContain(print[1..]);
    }

    [Fact]
    public void Dev_resume_checks_the_token_in_every_environment()
    {
        var tokens = new ResumeTokens();
        var token = tokens.Issue(Ada);
        foreach (var development in new[] { true, false })
        {
            var login = new DevLogin(development, 10, tokens);
            login.Resume(new HelloMessage(ProtocolVersion.Current, null, null, token)).Character.ShouldBe(Ada);
            var refused = login.Resume(new HelloMessage(ProtocolVersion.Current, null, null, "nope"));
            refused.Character.ShouldBeNull();
            refused.Refusal.ShouldBe(ByeReasons.ResumeFailed);
        }
        tokens.Find(token).ShouldBe(Ada, "checking does not use the token up");
    }

    [Fact]
    public void The_guard_locks_an_address_out_after_its_burst_of_failures()
    {
        var guard = new ResumeGuard(new GatewayOptions { ResumeFailureBurst = 3, ResumeFailuresPerMinute = 6, ResumeLockoutMs = 5_000 });
        guard.Fail("10.0.0.1", 0).ShouldBeFalse();
        guard.Fail("10.0.0.1", 0).ShouldBeFalse();
        guard.Fail("10.0.0.1", 0).ShouldBeFalse();
        guard.IsLockedOut("10.0.0.1", 0).ShouldBeFalse();
        guard.Fail("10.0.0.1", 0).ShouldBeTrue();
        guard.IsLockedOut("10.0.0.1", 4_999).ShouldBeTrue();
        guard.IsLockedOut("10.0.0.2", 0).ShouldBeFalse("per address");
        guard.IsLockedOut("10.0.0.1", 5_000).ShouldBeFalse();
        guard.Fail("10.0.0.1", 10_000).ShouldBeFalse();            // 6 per minute: one back every 10 s

        guard.Prune(10_000 + 29_999);
        guard.Tracked.ShouldBe(1);                                 // not refilled yet (3 failures take 30 s to come back)
        guard.Prune(10_000 + 30_000);
        guard.Tracked.ShouldBe(0);
    }

    [Fact]
    public void Grace_and_resume_options_parse_and_are_checked()
    {
        var options = GatewayOptions.Parse(["--net-grace-period-ms", "5000", "--net-resume-failure-burst", "2"],
            n => n == "GAME_NET_RESUME_LOCKOUT_MS" ? "900" : null, true, out var error)!;
        error.ShouldBeNull();
        options.GracePeriodMs.ShouldBe(5_000);
        options.ResumeFailureBurst.ShouldBe(2);
        options.ResumeLockoutMs.ShouldBe(900);
        new GatewayOptions().GracePeriodMs.ShouldBe(60_000);
        (options with { GracePeriodMs = -1 }).Problems().ShouldContain(p => p.Contains("grace-period-ms"));
        (options with { ResumeFailuresPerMinute = 0 }).Problems().ShouldContain(p => p.Contains("resume-failures-per-minute"));
        ClientSession.AddressOf("127.0.0.1:5123").ShouldBe("127.0.0.1");
        ClientSession.AddressOf("::1:5123").ShouldBe("::1");
    }
}
