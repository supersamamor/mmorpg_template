using System.Net;
using System.Text.Json;
using Game.Server.Admin;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Admin;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Network;

namespace Game.Tests.Admin;

/// <summary>T-804 over its own listener: who may call, the bounds of every endpoint, the commands' rules, kick end to end with the gateway, and the log.</summary>
public class AdminApiTests
{
    private static async Task<(AdminRig Rig, Guid Account, string Session, string Character)> WithPlayerAsync(Func<AdminOptions, AdminOptions>? configure = null)
    {
        var rig = await AdminRig.StartAsync(configure);
        var session = (await rig.Game.Accounts!.RegisterAsync("ada", ServiceRig.Password, "a", default)).Value!.Token;
        var character = (await rig.Game.Accounts.CreateAsync(session, "Lyra", default)).Value!.Id;
        return (rig, rig.Accounts!.AccountList.Single(a => a.Username == "ada").Id, session, character);
    }

    // ---- who may call ----

    [Fact]
    public async Task Without_the_credential_nothing_is_answered_and_the_address_is_slowed()
    {
        await using var rig = await AdminRig.StartAsync(o => o with { FailureBurst = 3 });
        rig.Http.Credential = null;
        (await rig.Http.GetAsync(AdminPaths.Status)).Status.ShouldBe(401);
        rig.Http.Credential = AdminRig.Credential[..^1] + "X";
        var (status, body, _) = await rig.Http.GetAsync(AdminPaths.Status);
        (status, body.GetProperty("error").GetString()).ShouldBe((401, AdminErrors.Unauthorized));
        (await rig.Http.KickAsync(Guid.NewGuid())).Status.ShouldBe(401);
        var (locked, lockedBody, response) = await rig.Http.GetAsync(AdminPaths.Status);
        (locked, lockedBody.GetProperty("error").GetString()).ShouldBe((429, AdminErrors.RateLimited));
        response.Headers.RetryAfter.ShouldNotBeNull();
        rig.Http.Credential = AdminRig.Credential;
        (await rig.Http.GetAsync(AdminPaths.Status)).Status.ShouldBe(429, "the address waits, even with the right one");
        rig.Time.Now += TimeSpan.FromMinutes(1);
        (await rig.Http.GetAsync(AdminPaths.Status)).Status.ShouldBe(200);
    }

    [Fact]
    public async Task The_credential_is_rate_limited()
    {
        await using var rig = await AdminRig.StartAsync(o => o with { RateLimitBurst = 4, RateLimitPerSecond = 1 });
        for (var i = 0; i < 4; i++) (await rig.Http.GetAsync(AdminPaths.Status)).Status.ShouldBe(200);
        var (status, _, response) = await rig.Http.GetAsync(AdminPaths.Status);
        status.ShouldBe(429);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
        rig.Time.Now += TimeSpan.FromSeconds(1);
        (await rig.Http.GetAsync(AdminPaths.Status)).Status.ShouldBe(200);
    }

    [Fact]
    public async Task The_staff_member_must_be_whole_and_have_a_role()
    {
        await using var rig = await AdminRig.StartAsync();
        foreach (var (staff, status, error) in new (( string?, string?, string?), int, string)[]
                 {
                     ((null, "Robin", "support"), 400, AdminErrors.InvalidStaff),
                     (("x y", "Robin", "support"), 400, AdminErrors.InvalidStaff),
                     (("s1", null, "support"), 400, AdminErrors.InvalidStaff),
                     (("s1", "Robin", null), 403, AdminErrors.UnknownRole),
                     (("s1", "Robin", "owner"), 403, AdminErrors.UnknownRole),
                 })
        {
            rig.Http.Staff = staff;
            var (got, body, _) = await rig.Http.GetAsync(AdminPaths.Status);
            (got, body.GetProperty("error").GetString()).ShouldBe((status, error), staff.ToString());
            (await rig.Http.KickAsync(Guid.NewGuid())).Status.ShouldBe(status);
        }
        rig.Memory!.Audit.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_answer_is_no_store_JSON_with_its_request_id_and_nothing_else_is_served()
    {
        await using var rig = await AdminRig.StartAsync();
        var (status, body, response) = await rig.Http.SendAsync(HttpMethod.Get, AdminPaths.Status, requestId: "panel-42");
        status.ShouldBe(200);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues(AdminHeaders.RequestId).Single().ShouldBe("panel-42");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.Contains("Server").ShouldBeFalse();
        foreach (var path in new[] { "/", "/ws", "/account/login", "/admin", "/admin/v2/status", AdminPaths.Root, AdminPaths.Root + "/nothing", AdminPaths.Accounts + "/not-a-guid" })
        {
            var (code, refused, answer) = await rig.Http.GetAsync(path);
            (code, refused.GetProperty("error").GetString()).ShouldBe((404, AdminErrors.NotFound), path);
            answer.Headers.CacheControl!.NoStore.ShouldBeTrue();
            refused.GetProperty("request_id").GetString()!.ShouldMatch("^[0-9a-f]{32}$");
        }
        (await rig.Http.SendAsync(HttpMethod.Delete, AdminPaths.Status)).Status.ShouldBe(404);
        (await rig.Http.SendAsync(HttpMethod.Put, AdminPaths.Commands + "/kick", "{}")).Status.ShouldBe(404);
    }

    // ---- reads ----

    [Fact]
    public async Task Status_has_players_instances_ticks_saves_errors_and_the_content_hash()
    {
        var (rig, _, session, character) = await WithPlayerAsync();
        await using var owner = rig;
        var client = await rig.Game.PlayAsync(session, character);
        await client.WaitForAsync<WelcomeMessage>();
        await Poll.UntilAsync(() => rig.Game.World.Instances.All(i => i.GetStats().Timings.Samples > 0), ok => ok, "ticks");
        rig.Log.Error("one for the count");
        var (status, body, _) = await rig.Http.GetAsync(AdminPaths.Status);
        status.ShouldBe(200);
        body.GetProperty("content_hash").GetString().ShouldBe(rig.Game.ContentHash);
        body.GetProperty("content_hash").GetString().ShouldBe(Game.Content.ClientExport.ClientBundleExporter.Hash(rig.Game.Content));
        body.GetProperty("players_online").GetInt32().ShouldBe(1);
        body.GetProperty("sessions").GetInt32().ShouldBe(1);
        body.GetProperty("started_at").GetString().ShouldBe("2026-11-26T00:00:00.0000000Z");
        var instances = body.GetProperty("instances").EnumerateArray().ToList();
        instances.Select(i => i.GetProperty("map_id").GetString()).ShouldBe(rig.Game.World.Instances.Select(i => i.MapId));
        foreach (var instance in instances)
        {
            instance.GetProperty("tick").GetInt64().ShouldBeGreaterThan(0);
            instance.GetProperty("tick_p99_micros").GetInt64().ShouldBeGreaterThanOrEqualTo(instance.GetProperty("tick_p50_micros").GetInt64());
            instance.GetProperty("tick_max_micros").GetInt64().ShouldBeGreaterThanOrEqualTo(instance.GetProperty("tick_p99_micros").GetInt64());
            instance.TryGetProperty("system_errors", out _).ShouldBeTrue();
        }
        body.GetProperty("saves").ValueKind.ShouldBe(JsonValueKind.Null, "the rig saves nothing");
        body.GetProperty("errors").GetProperty("logged_errors").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Lookups_are_paged_bounded_and_show_no_secret()
    {
        var (rig, account, session, _) = await WithPlayerAsync(o => o with { MaxPageSize = 2 });
        await using var owner = rig;
        foreach (var name in new[] { "adb", "adc", "bob" }) await rig.Game.Accounts!.RegisterAsync(name, ServiceRig.Password, "a", default);
        var (status, page, _) = await rig.Http.GetAsync(AdminPaths.Accounts + "?name=AD");
        status.ShouldBe(200);
        page.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("username").GetString()).ShouldBe(["ada", "adb"]);
        page.GetProperty("next").GetString().ShouldBe("adb");
        var (_, rest, _) = await rig.Http.GetAsync(AdminPaths.Accounts + "?name=ad&after=adb");
        rest.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("username").GetString()).ShouldBe(["adc"]);
        rest.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
        var (_, one, _) = await rig.Http.GetAsync($"{AdminPaths.Accounts}/{account}");
        one.GetProperty("account").GetProperty("id").GetGuid().ShouldBe(account);
        one.GetProperty("account").GetProperty("online").GetBoolean().ShouldBeFalse();
        var all = one.GetRawText() + page.GetRawText();
        all.ShouldNotContain("hash", Case.Insensitive);
        all.ShouldNotContain("token", Case.Insensitive);
        all.ShouldNotContain(session);
        all.ShouldNotContain(rig.Accounts!.OnlyHashOf(account));
        (await rig.Http.GetAsync($"{AdminPaths.Accounts}/{Guid.NewGuid()}")).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.UnknownTarget);
        (await rig.Http.GetAsync($"{AdminPaths.Characters}/{Guid.NewGuid()}")).Status.ShouldBe(404);
        foreach (var bad in new[]
                 {
                     "?limit=0", "?limit=3", "?limit=x", "?id=nope", "?name=" + new string('a', 129), "?name=a&name=b", "?name=%0A", "?name=" + Uri.EscapeDataString("not a name!"),
                 })
            (await rig.Http.GetAsync(AdminPaths.Accounts + bad)).Status.ShouldBe(400, bad);
        foreach (var bad in new[] { "?account=x", "?limit=-1" }) (await rig.Http.GetAsync(AdminPaths.Characters + bad)).Status.ShouldBe(400, bad);
        foreach (var bad in new[] { "?before=0", "?before=x", "?from=yesterday", "?action=ban", "?limit=1000" }) (await rig.Http.GetAsync(AdminPaths.Audit + bad)).Status.ShouldBe(400, bad);
        (await rig.Http.GetAsync(AdminPaths.Audit + "?target=x&staff=s&action=kick&from=2026-01-01T00:00:00Z&to=2027-01-01&before=9&limit=2")).Status.ShouldBe(200);
        var query = (AuditQuery)rig.Memory!.LastQuery!;
        (query.TargetId, query.StaffId, query.Action, query.BeforeId, query.Limit, query.From).ShouldBe(("x", "s", "kick", (long?)9, 2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        (await rig.Http.GetAsync(AdminPaths.Characters + "?name=LY")).Status.ShouldBe(200);
        ((CharacterQuery)rig.Memory.LastQuery!).NamePrefix.ShouldBe("ly");
    }

    [Fact]
    public async Task The_command_list_says_each_lowest_role()
    {
        await using var rig = await AdminRig.StartAsync();
        var (_, body, _) = await rig.Http.GetAsync(AdminPaths.Commands);
        var kick = body.GetProperty("commands").EnumerateArray().Single();
        (kick.GetProperty("action").GetString(), kick.GetProperty("lowest_role").GetString()).ShouldBe(("kick", "support"));
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["support", "gm", "admin"]);
    }

    [Fact]
    public async Task A_store_that_is_away_is_answered_unavailable()
    {
        var (rig, account, _, _) = await WithPlayerAsync();
        await using var owner = rig;
        rig.Memory!.Away = true;
        (await rig.Http.GetAsync(AdminPaths.Accounts)).Body.GetProperty("error").GetString().ShouldBe(AdminErrors.Unavailable);
        var (status, body, _) = await rig.Http.KickAsync(account);
        (status, body.GetProperty("outcome").GetString(), body.GetProperty("error").GetString()).ShouldBe((503, AdminOutcomes.Refused, AdminErrors.Unavailable));
        rig.Accounts!.AccountList.Single().Sessions.ShouldBe(1, "nothing was changed");
    }

    // ---- commands ----

    [Fact]
    public async Task A_command_needs_a_JSON_object_its_reason_and_a_known_target()
    {
        var (rig, account, _, _) = await WithPlayerAsync(o => o with { MaxBodyBytes = 1024 });
        await using var owner = rig;
        async Task Refused(string? json, int status, string error, string contentType = "application/json", string action = "kick")
        {
            var (code, body, _) = await rig.Http.SendAsync(HttpMethod.Post, AdminPaths.Commands + "/" + action, json, contentType: contentType);
            (code, body.GetProperty("error").GetString()).ShouldBe((status, error), json);
        }
        await Refused("{\"account_id\":\"" + account + "\"}", 400, AdminErrors.ReasonRequired);
        await Refused("{\"account_id\":\"" + account + "\",\"reason\":\" \"}", 400, AdminErrors.ReasonRequired);
        await Refused("{\"account_id\":\"" + account + "\",\"reason\":\"" + new string('r', 501) + "\"}", 400, AdminErrors.ReasonRequired);
        await Refused("{\"account_id\":\"nope\",\"reason\":\"r\"}", 400, AdminErrors.InvalidRequest);
        await Refused("[1]", 400, AdminErrors.InvalidRequest);
        await Refused("{not json", 400, AdminErrors.InvalidRequest);
        await Refused("", 400, AdminErrors.InvalidRequest);
        await Refused("{\"reason\":\"r\"}", 400, AdminErrors.InvalidRequest, contentType: "text/plain");
        await Refused("{\"reason\":\"r\"}", 404, AdminErrors.NotFound, action: "ban");
        await Refused("{\"account_id\":\"" + Guid.NewGuid() + "\",\"reason\":\"r\"}", 409, AdminErrors.UnknownTarget);
        rig.Memory!.Audit.ShouldBeEmpty("nothing was changed, so nothing is audited");
        try
        {
            // Over the body limit, last: refused without being read (Kestrel may close the connection while the client still sends).
            await Refused("{\"account_id\":\"" + account + "\",\"reason\":\"" + new string('r', 2000) + "\"}", 400, AdminErrors.InvalidRequest);
        }
        catch (HttpRequestException)
        {
        }
        rig.Accounts!.AccountList.Single().Sessions.ShouldBe(1);
    }

    [Fact]
    public async Task The_role_is_checked_on_the_server_for_every_command()
    {
        var gmOnly = new GmCommand();
        var table = new Dictionary<string, string>(AdminCommandTable.LowestRole) { [GmCommand.Name] = StaffRoles.Gm };
        await using var rig = await AdminRig.StartAsync(commands: [new KickCommand(), gmOnly], table: table);
        rig.Http.Staff = ("s1", "Robin", StaffRoles.Support);
        var (status, body, _) = await rig.Http.PostAsync(AdminPaths.Commands + "/" + GmCommand.Name, new { reason = "r" });
        (status, body.GetProperty("outcome").GetString(), body.GetProperty("error").GetString()).ShouldBe((403, AdminOutcomes.Refused, AdminErrors.Forbidden));
        gmOnly.Runs.ShouldBe(0);
        foreach (var role in new[] { StaffRoles.Gm, StaffRoles.Admin })
        {
            rig.Http.Staff = ("s1", "Robin", role);
            (await rig.Http.PostAsync(AdminPaths.Commands + "/" + GmCommand.Name, new { reason = "r" })).Status.ShouldBe(200, role);
        }
        gmOnly.Runs.ShouldBe(2);
    }

    /// <summary>A command only <c>gm</c> and above may run (in the test's own table).</summary>
    private sealed class GmCommand : IAdminCommand
    {
        public const string Name = "test_gm_only";

        public int Runs;

        public string Action => Name;

        public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(new AdminResult(AdminOutcomes.Done, null, null, null));
        }
    }

    [Fact]
    public async Task Kick_ends_the_game_and_every_session_and_the_player_signs_in_again()
    {
        var (rig, account, session, character) = await WithPlayerAsync();
        await using var owner = rig;
        var second = (await rig.Game.Accounts!.LoginAsync("ada", ServiceRig.Password, "a", default)).Value!.Token;   // another device, signed in but not playing
        var client = await rig.Game.PlayAsync(session, character);
        await client.WaitForAsync<WelcomeMessage>();
        await client.WaitForAsync<SnapshotMessage>();
        var entity = new EntityId(client.Id);
        var token = client.Token;
        var applied = new List<AdminActionApplied>();
        rig.Events.Bus.Subscribe<AdminActionApplied>(applied.Add);
        var left = new TaskCompletionSource<EntityLeftMap>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Game.World.GetInstance(rig.Game.World.LocationOf(entity)!).Events.Subscribe<EntityLeftMap>(e => { if (e.Entity == entity) left.TrySetResult(e); });

        rig.Http.Staff = ("oidc|robin", "Robin Q", StaffRoles.Support);
        var (status, body, _) = await rig.Http.KickAsync(account, "testing the kick", "req-kick-1");

        status.ShouldBe(200, body.ToString());
        body.GetProperty("outcome").GetString().ShouldBe(AdminOutcomes.Accepted);
        body.GetProperty("request_id").GetString().ShouldBe("req-kick-1");
        var result = body.GetProperty("result");
        (result.GetProperty("was_online").GetBoolean(), result.GetProperty("sessions_ended").GetInt32()).ShouldBe((true, 2));
        (await client.WaitForByeAsync()).ShouldBe(ByeReasons.Kicked);
        (await left.Task.WaitAsync(TimeSpan.FromSeconds(10))).Entity.ShouldBe(entity);
        rig.Game.Gateway.HoldsCharacter(Guid.Parse(character)).ShouldBeFalse();
        (await (await rig.Game.ResumeAsync(token)).WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed, "the resume token is dead");
        (await (await rig.Game.PlayAsync(session, character)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused, "the session is dead");
        (await (await rig.Game.PlayAsync(second, character)).WaitForByeAsync()).ShouldBe(ByeReasons.LoginRefused, "and the other device's");
        var fresh = (await rig.Game.Accounts.LoginAsync("ada", ServiceRig.Password, "a", default)).Value!.Token;
        var again = await rig.Game.PlayAsync(fresh, character);
        await again.WaitForAsync<WelcomeMessage>();

        var row = rig.Memory!.Audit.ShouldHaveSingleItem();
        (row.Id, row.StaffId, row.StaffName, row.StaffRole, row.Action, row.TargetKind, row.TargetId, row.Reason, row.RequestId)
            .ShouldBe((body.GetProperty("audit_id").GetInt64(), "oidc|robin", "Robin Q", "support", "kick", "account", account.ToString(), "testing the kick", "req-kick-1"));
        var before = JsonDocument.Parse(row.Before).RootElement;
        (before.GetProperty("online").GetBoolean(), before.GetProperty("character_id").GetString(), before.GetProperty("map").GetString())
            .ShouldBe((true, character, rig.Game.Start.MapId));
        before.TryGetProperty("cell_x", out _).ShouldBeTrue("read on the character's tick");
        var after = JsonDocument.Parse(row.After).RootElement;
        (after.GetProperty("online").GetBoolean(), after.GetProperty("sessions_ended").GetInt32()).ShouldBe((false, 2));
        (row.Before + row.After).ShouldNotContain("Lyra");
        (row.Before + row.After).ShouldNotContain("ada");
        var e = applied.ShouldHaveSingleItem();
        (e.AuditId, e.RequestId, e.StaffId, e.StaffRole, e.Action, e.TargetKind, e.TargetId, e.Name)
            .ShouldBe((row.Id, "req-kick-1", "oidc|robin", "support", "kick", "account", account.ToString(), EventNames.AdminActionApplied));

        var (_, audit, _) = await rig.Http.GetAsync($"{AdminPaths.Audit}?target={account}");
        var listed = audit.GetProperty("items").EnumerateArray().Single();
        (listed.GetProperty("action").GetString(), listed.GetProperty("before").GetProperty("online").GetBoolean(), listed.GetProperty("staff_name").GetString())
            .ShouldBe(("kick", true, "Robin Q"));
    }

    [Fact]
    public async Task Kick_reaches_a_character_in_its_grace_period_and_an_account_not_playing()
    {
        var (rig, account, session, character) = await WithPlayerAsync();
        await using var owner = rig;
        var client = await rig.Game.PlayAsync(session, character);
        await client.WaitForAsync<SnapshotMessage>();
        client.Dispose(); // dropped: the character waits for a resume
        await Poll.UntilAsync(() => rig.Game.Gateway.CharactersInGrace, n => n == 1, "the grace period");
        var token = client.Token;
        var (_, body, _) = await rig.Http.KickAsync(account);
        body.GetProperty("outcome").GetString().ShouldBe(AdminOutcomes.Accepted);
        JsonDocument.Parse(rig.Memory!.Audit.Single().Before).RootElement.GetProperty("in_grace").GetBoolean().ShouldBeTrue();
        await Poll.UntilAsync(() => rig.Game.Gateway.CharacterCount, n => n == 0, "the character left");
        (await (await rig.Game.ResumeAsync(token)).WaitForByeAsync()).ShouldBe(ByeReasons.ResumeFailed);

        var (_, idle, _) = await rig.Http.KickAsync(account, "again, nobody plays");
        (idle.GetProperty("outcome").GetString(), idle.GetProperty("result").GetProperty("sessions_ended").GetInt32()).ShouldBe((AdminOutcomes.Done, 0));
        rig.Memory.Audit.Count.ShouldBe(2, "a kick that changes nothing is still a staff action, and audited");
    }

    [Fact]
    public async Task Nothing_secret_is_in_the_log()
    {
        var (rig, account, session, character) = await WithPlayerAsync();
        await using var owner = rig;
        var client = await rig.Game.PlayAsync(session, character);
        await client.WaitForAsync<WelcomeMessage>();
        rig.Http.Staff = ("oidc|robin", "Robin Secretname", StaffRoles.Admin);
        await rig.Http.KickAsync(account, "a reason with private words");
        rig.Http.Credential = "wrong-credential-that-is-long-enough-to-matter";
        await rig.Http.GetAsync(AdminPaths.Status);
        rig.Http.Credential = AdminRig.Credential;
        await rig.Http.GetAsync(AdminPaths.Accounts + "?name=ada");
        var log = string.Join("\n", rig.Game.Log.Lines);
        log.ShouldContain("ran 'kick' on account " + account);
        foreach (var secret in new[] { AdminRig.Credential, "wrong-credential-that-is-long-enough-to-matter", session, "Secretname", "private words", rig.Accounts!.OnlyHashOf(account), "ada\"" })
            log.ShouldNotContain(secret);
    }

    // ---- the road into the tick ----

    [Fact]
    public async Task A_step_runs_on_the_tick_of_the_entitys_instance_once()
    {
        await using var rig = await AdminRig.StartAsync();
        var client = await rig.Game.JoinAsync("dev");
        var entity = new EntityId(client.Id);
        var road = new AdminRoad(rig.Game.World, rig.Game.Gateway, rig.Store, rig.Options, TimeProvider.System);
        var runs = 0;
        int? thread = null;
        var result = await road.OnTickAsync(entity, "test", (tick, e) =>
        {
            runs++;
            thread = Environment.CurrentManagedThreadId;
            Game.Server.Hosting.TickThread.IsCurrent.ShouldBeTrue();
            return AdminStepResult.Ok(new Dictionary<string, object?> { ["map"] = e.MapId, ["tick"] = tick.TickNumber });
        }, default);
        (result.Error, result.Values["map"]).ShouldBe((null, (object?)rig.Game.Start.MapId));
        runs.ShouldBe(1);
        thread.ShouldNotBe(Environment.CurrentManagedThreadId);

        await Should.ThrowAsync<AdminStepTimeoutException>(() => road.OnTickAsync(new EntityId(999_999), "test", (_, _) => AdminStepResult.Ok(new Dictionary<string, object?>()), default));
        await Should.ThrowAsync<InvalidOperationException>(() => road.OnTickAsync(entity, "test", (_, _) => throw new InvalidOperationException("step"), default));
        rig.Game.Log.Lines.ShouldContain(l => l.Contains("admin step 'test'") && l.StartsWith("ERROR"));
        (await road.OnTickAsync(entity, "test", (_, _) => AdminStepResult.Refused(AdminErrors.UnknownTarget), default)).Error.ShouldBe(AdminErrors.UnknownTarget);
    }

    [Fact]
    public async Task A_stalled_world_refuses_the_command_with_timeout_and_changes_nothing()
    {
        var (rig, account, session, character) = await WithPlayerAsync(o => o with { StepTimeoutMs = 300 });
        await using var owner = rig;
        var client = await rig.Game.PlayAsync(session, character);
        await client.WaitForAsync<SnapshotMessage>();
        var instance = rig.Game.World.GetInstance(rig.Game.World.LocationOf(new EntityId(client.Id))!);
        var hold = new ManualResetEventSlim();
        instance.Events.Subscribe<MovementStarted>(_ => hold.Wait(TimeSpan.FromSeconds(5)));   // the tick thread stops in a handler
        await client.SendAsync(new MoveMessage(rig.Game.NearStart((2, 0), (0, 2), (-2, 0), (0, -2))));
        await Task.Delay(300);
        var (status, body, _) = await rig.Http.KickAsync(account);
        hold.Set();
        (status, body.GetProperty("error").GetString()).ShouldBe((504, AdminErrors.Timeout));
        rig.Memory!.Audit.ShouldBeEmpty();
        rig.Accounts!.AccountList.Single().Sessions.ShouldBe(1);
    }
}
