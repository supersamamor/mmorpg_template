using Game.Client.Core;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Random;

namespace Game.Tests.Client;

/// <summary>The client's resume flow (ARCHITECTURE §10): every <c>bye</c> reason, backoff, the grace limit, background and foreground.</summary>
public class ConnectionMachineTests
{
    private static readonly ClientConfig Config = new();

    private sealed class FixedRandom(double value) : IRandom
    {
        public uint NextUInt() => 0;

        public double NextDouble() => value;

        public int Next(int maxExclusive) => 0;
    }

    private static ConnectionMachine New(double random = 0.5) => new(Config, new FixedRandom(random));

    private static List<ConnectionCommandKind> Drain(ConnectionMachine machine)
    {
        var commands = new List<ConnectionCommandKind>();
        while (machine.TryNextCommand(out var command)) commands.Add(command);
        return commands;
    }

    private static WelcomeMessage Welcome(string token, long id = 42) => new(ProtocolVersion.Current, id, "map.a", 10, "hash", token);

    /// <summary>A machine that logged in at time 0 and is playing.</summary>
    private static ConnectionMachine Online(double random = 0.5)
    {
        var machine = New(random);
        machine.Login("Ada", 0);
        machine.OnSocketOpened();
        machine.OnHeard(0);
        machine.OnWelcome(Welcome("token-1"), 0);
        Drain(machine);
        return machine;
    }

    /// <summary>A machine resuming: the socket dropped at time 10 and the new socket has said hello.</summary>
    private static ConnectionMachine Resuming()
    {
        var machine = Online();
        machine.OnHeard(10);
        machine.OnSocketClosed(10);
        machine.Tick(20);
        machine.OnSocketOpened();
        Drain(machine);
        machine.Phase.ShouldBe(ConnectionPhase.AwaitingWelcome);
        return machine;
    }

    // ---- login ----

    [Fact]
    public void A_login_opens_a_socket_and_says_hello_with_the_name()
    {
        var machine = New();
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        machine.Login("Ada", 0);
        Drain(machine).ShouldBe([ConnectionCommandKind.OpenSocket]);
        machine.OnSocketOpened();
        Drain(machine).ShouldBe([ConnectionCommandKind.SendLogin]);
        machine.LoginName.ShouldBe("Ada");
        machine.Phase.ShouldBe(ConnectionPhase.AwaitingWelcome);
    }

    [Fact]
    public void Welcome_goes_online_and_the_first_message_acknowledges_it()
    {
        var machine = New();
        machine.Login("Ada", 0);
        machine.OnSocketOpened();
        Drain(machine);
        machine.OnWelcome(Welcome("token-1"), 0);
        machine.Phase.ShouldBe(ConnectionPhase.Online);
        machine.EntityId.ShouldBe(42);
        Drain(machine).ShouldBe([ConnectionCommandKind.SendPing]); // the implicit acknowledgement of a resume's welcome (T-214)
    }

    [Fact]
    public void A_login_that_cannot_connect_goes_back_to_the_login_screen_without_retrying()
    {
        var machine = New();
        machine.Login("Ada", 0);
        machine.OnSocketClosed(1);
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        machine.Notice.ShouldBe(ConnectionNotice.ConnectionFailed);
    }

    // ---- the token ----

    [Fact]
    public void The_token_is_replaced_on_every_welcome()
    {
        var machine = Online();
        machine.ResumeToken.ShouldBe("token-1");

        machine.OnSocketClosed(1);
        machine.Tick(5);
        machine.OnSocketOpened();
        Drain(machine).ShouldBe([ConnectionCommandKind.OpenSocket, ConnectionCommandKind.SendResume]);
        machine.ResumeToken.ShouldBe("token-1");

        machine.OnWelcome(Welcome("token-2"), 6);
        machine.ResumeToken.ShouldBe("token-2");

        machine.OnSocketClosed(7);
        machine.Tick(12);
        machine.OnSocketOpened();
        machine.OnWelcome(Welcome("token-3"), 13);
        machine.ResumeToken.ShouldBe("token-3");
    }

    [Fact]
    public void A_welcome_without_a_token_clears_the_old_one()
    {
        var machine = Online();
        machine.OnSocketClosed(1);
        machine.Tick(5);
        machine.OnSocketOpened();
        machine.OnWelcome(new WelcomeMessage(1, 42, "map.a", 10), 6);
        machine.HasToken.ShouldBeFalse();
    }

    // ---- every bye reason ----

    public static TheoryData<string, ConnectionPhase, ConnectionNotice, bool> ByeWhileOnline => new()
    {
        // Final answers.
        { ByeReasons.Logout, ConnectionPhase.LoginScreen, ConnectionNotice.LoggedOut, false },
        { ByeReasons.Replaced, ConnectionPhase.Stopped, ConnectionNotice.Replaced, false },
        { ByeReasons.UpdateRequired, ConnectionPhase.Stopped, ConnectionNotice.UpdateRequired, false },
        { ByeReasons.BadVersion, ConnectionPhase.Stopped, ConnectionNotice.UpdateRequired, false },
        { ByeReasons.ResumeFailed, ConnectionPhase.LoginScreen, ConnectionNotice.ResumeFailed, false },
        { ByeReasons.LoginRefused, ConnectionPhase.LoginScreen, ConnectionNotice.LoginRefused, false },
        { ByeReasons.ServerFull, ConnectionPhase.LoginScreen, ConnectionNotice.ServerFull, false },
        { ByeReasons.Shutdown, ConnectionPhase.Backoff, ConnectionNotice.ServerShutdown, false },
        // The server keeps the character through these (§10 step 6), so they are a drop: resume.
        { ByeReasons.Timeout, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.TooSlow, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.ServerError, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.Malformed, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.UnknownType, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.Oversize, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.BeforeHello, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.DuplicateHello, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
        { ByeReasons.RateLimit, ConnectionPhase.Backoff, ConnectionNotice.Reconnecting, true },
    };

    [Fact]
    public void The_table_covers_every_bye_reason()
    {
        var covered = ByeWhileOnline.Select(row => (string)row[0]).ToHashSet();
        foreach (var reason in ByeReasons.All.Ordered) covered.ShouldContain(reason);
    }

    [Theory]
    [MemberData(nameof(ByeWhileOnline))]
    public void A_bye_while_online_does_what_the_flow_says(string reason, ConnectionPhase phase, ConnectionNotice notice, bool keepsToken)
    {
        var machine = Online();
        machine.OnBye(reason, 5);
        machine.Phase.ShouldBe(phase);
        machine.Notice.ShouldBe(notice);
        machine.HasToken.ShouldBe(keepsToken);
        Drain(machine).ShouldContain(ConnectionCommandKind.CloseSocket);
    }

    [Theory]
    [InlineData(ByeReasons.Logout)]
    [InlineData(ByeReasons.Replaced)]
    [InlineData(ByeReasons.UpdateRequired)]
    [InlineData(ByeReasons.ResumeFailed)]
    public void After_a_final_bye_nothing_ever_reconnects(string reason)
    {
        var machine = Online();
        machine.OnBye(reason, 5);
        Drain(machine);
        machine.OnSocketClosed(5);
        for (var t = 5.0; t < 300; t += 1) machine.Tick(t);
        Drain(machine).ShouldNotContain(ConnectionCommandKind.OpenSocket);
    }

    [Fact]
    public void Resume_failed_during_a_resume_goes_to_login()
    {
        var machine = Resuming();
        machine.OnBye(ByeReasons.ResumeFailed, 21);
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        machine.Notice.ShouldBe(ConnectionNotice.ResumeFailed);
        machine.HasToken.ShouldBeFalse();
    }

    [Fact]
    public void Rate_limit_during_a_resume_goes_to_login_without_retrying()
    {
        var machine = Resuming();
        machine.OnBye(ByeReasons.RateLimit, 21);
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        machine.Notice.ShouldBe(ConnectionNotice.ResumeRateLimited);
        machine.HasToken.ShouldBeFalse();
        Drain(machine);
        for (var t = 21.0; t < 300; t += 1) machine.Tick(t);
        Drain(machine).ShouldNotContain(ConnectionCommandKind.OpenSocket);
    }

    [Fact]
    public void A_full_socket_table_during_a_resume_keeps_trying()
    {
        var machine = Resuming();
        machine.OnBye(ByeReasons.ServerFull, 21);
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
        machine.HasToken.ShouldBeTrue();
    }

    [Fact]
    public void Shutdown_retries_later_with_a_normal_login()
    {
        var machine = Online();
        machine.OnBye(ByeReasons.Shutdown, 5);
        Drain(machine);
        machine.Tick(5 + Config.ShutdownRetrySeconds - 0.1);
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
        machine.Tick(5 + Config.ShutdownRetrySeconds);
        Drain(machine).ShouldBe([ConnectionCommandKind.OpenSocket]);
        machine.OnSocketOpened();
        Drain(machine).ShouldBe([ConnectionCommandKind.SendLogin]); // the server's restart ended every token

        machine.OnSocketClosed(40); // it is still down: wait again
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
        machine.Notice.ShouldBe(ConnectionNotice.ServerShutdown);
    }

    [Fact]
    public void Replaced_can_go_back_to_login_only_by_the_player()
    {
        var machine = Online();
        machine.OnBye(ByeReasons.Replaced, 5);
        Drain(machine);
        machine.Login("Ada", 6);
        Drain(machine).ShouldBe([ConnectionCommandKind.OpenSocket]);
    }

    [Fact]
    public void Logout_sends_logout_and_waits_for_the_answer()
    {
        var machine = Online();
        machine.Logout(5);
        Drain(machine).ShouldBe([ConnectionCommandKind.SendLogout]);
        machine.Phase.ShouldBe(ConnectionPhase.LoggingOut);
        machine.OnBye(ByeReasons.Logout, 5.1);
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        machine.HasToken.ShouldBeFalse();
    }

    [Fact]
    public void Logout_with_no_answer_ends_anyway()
    {
        var machine = Online();
        machine.Logout(5);
        machine.Tick(5 + Config.ConnectTimeoutSeconds);
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
    }

    // ---- backoff ----

    [Fact]
    public void Backoff_doubles_up_to_the_cap()
    {
        // With a random of 0.5 the jitter is exactly 0.
        var machine = Online();
        var delays = new List<double>();
        var now = 1.0;
        machine.OnSocketClosed(now);
        Drain(machine);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var waited = 0.0;
            while (!Drain(machine).Contains(ConnectionCommandKind.OpenSocket))
            {
                now += 0.01;
                waited += 0.01;
                machine.Tick(now);
            }
            delays.Add(Math.Round(waited, 1));
            machine.OnSocketClosed(now);
        }
        delays.ShouldBe([0.5, 1, 2, 4, 4, 4]);
    }

    [Theory]
    [InlineData(0.0, 0.75)]
    [InlineData(0.999999, 1.25)]
    public void Jitter_stays_within_its_limits(double random, double factor)
    {
        var machine = Online(random);
        machine.OnSocketClosed(1);
        Drain(machine);
        var delay = Config.BackoffInitialSeconds * factor;
        machine.Tick(1 + delay - 0.001);
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
        machine.Tick(1 + delay + 0.001);
        machine.Phase.ShouldBe(ConnectionPhase.Connecting);
    }

    [Fact]
    public void A_failed_connect_times_out_and_backs_off()
    {
        var machine = Online();
        machine.OnSocketClosed(1);
        machine.Tick(2);
        Drain(machine);
        machine.Phase.ShouldBe(ConnectionPhase.Connecting);
        machine.Tick(2 + Config.ConnectTimeoutSeconds);
        Drain(machine).ShouldBe([ConnectionCommandKind.CloseSocket]);
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
    }

    // ---- the grace limit ----

    [Fact]
    public void It_stops_trying_once_the_grace_period_must_be_over()
    {
        var machine = Online();
        machine.OnHeard(10);
        machine.OnSocketClosed(11);
        Drain(machine);
        var deadline = 10 + Config.GraceSeconds + Config.GraceMarginSeconds;
        for (var t = 11.0; t < deadline; t += 0.5)
        {
            machine.Tick(t);
            machine.OnSocketClosed(t); // every attempt fails
            Drain(machine);
            machine.Phase.ShouldNotBe(ConnectionPhase.LoginScreen, $"gave up early at {t}");
        }
        machine.Tick(deadline);
        machine.OnSocketClosed(deadline);
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        machine.Notice.ShouldBe(ConnectionNotice.SessionExpired);
        machine.HasToken.ShouldBeFalse();
        Drain(machine);
        for (var t = deadline; t < deadline + 120; t += 1) machine.Tick(t);
        Drain(machine).ShouldNotContain(ConnectionCommandKind.OpenSocket);
    }

    [Fact]
    public void A_successful_resume_starts_a_new_grace_period()
    {
        var machine = Resuming();
        machine.OnHeard(21);
        machine.OnWelcome(Welcome("token-2"), 21);
        machine.OnHeard(500);
        machine.OnSocketClosed(500);
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
    }

    // ---- staying alive ----

    [Fact]
    public void It_pings_while_online()
    {
        var machine = Online();
        machine.Tick(Config.PingIntervalSeconds - 0.1);
        Drain(machine).ShouldBeEmpty();
        machine.OnHeard(Config.PingIntervalSeconds - 0.1);
        machine.Tick(Config.PingIntervalSeconds);
        Drain(machine).ShouldBe([ConnectionCommandKind.SendPing]);
    }

    [Fact]
    public void A_silent_socket_counts_as_dropped()
    {
        var machine = Online();
        machine.OnHeard(3);
        machine.Tick(3 + Config.SilenceTimeoutSeconds);
        Drain(machine).ShouldContain(ConnectionCommandKind.CloseSocket);
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
    }

    // ---- background and foreground ----

    [Fact]
    public void In_the_background_nothing_is_sent_and_no_reconnect_starts()
    {
        var machine = Online();
        machine.OnBackgrounded();
        machine.OnSocketClosed(5);
        Drain(machine);
        machine.Tick(40);
        Drain(machine).ShouldBeEmpty();
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
    }

    [Fact]
    public void Back_in_the_foreground_it_reconnects_at_once_if_the_socket_went_away()
    {
        var machine = Online();
        machine.OnBackgrounded();
        machine.OnSocketClosed(5);
        Drain(machine);
        machine.OnForegrounded(40);
        machine.Tick(40);
        Drain(machine).ShouldBe([ConnectionCommandKind.OpenSocket]);
    }

    [Fact]
    public void Back_in_the_foreground_a_socket_that_looks_open_must_answer_a_ping()
    {
        var machine = Online();
        machine.OnHeard(5);
        machine.OnBackgrounded();
        machine.OnForegrounded(40);
        Drain(machine).ShouldBe([ConnectionCommandKind.SendPing]);
        machine.Tick(40.1); // long silence in the background does not count
        machine.Phase.ShouldBe(ConnectionPhase.Online);

        machine.OnHeard(40.2);
        machine.Tick(40 + Config.ForegroundProbeSeconds + 1);
        machine.Phase.ShouldBe(ConnectionPhase.Online);
    }

    [Fact]
    public void A_dead_socket_found_after_the_background_resumes()
    {
        var machine = Online();
        machine.OnHeard(5);
        machine.OnBackgrounded();
        machine.OnForegrounded(40);
        Drain(machine);
        machine.Tick(40 + Config.ForegroundProbeSeconds);
        Drain(machine).ShouldBe([ConnectionCommandKind.CloseSocket]);
        machine.Phase.ShouldBe(ConnectionPhase.Backoff);
        machine.HasToken.ShouldBeTrue();
    }

    [Fact]
    public void A_background_longer_than_the_grace_period_goes_to_login()
    {
        var machine = Online();
        machine.OnHeard(5);
        machine.OnBackgrounded();
        machine.OnSocketClosed(6);
        Drain(machine);
        machine.OnForegrounded(1000);
        machine.Tick(1000);
        machine.Phase.ShouldBe(ConnectionPhase.LoginScreen);
        machine.Notice.ShouldBe(ConnectionNotice.SessionExpired);
    }

    [Fact]
    public void Ticking_with_no_news_allocates_nothing()
    {
        var machine = Online();
        machine.OnHeard(0);
        AllocationProbe.MinAllocatedBytes(() =>
        {
            for (var i = 0; i < 100; i++) machine.Tick(1);
        }).ShouldBe(0);
    }
}
