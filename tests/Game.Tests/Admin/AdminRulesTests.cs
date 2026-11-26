using System.Net;
using System.Reflection;
using System.Text.Json;
using Game.Server.Admin;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Tests.Accounts;
using Game.Tests.Server;

namespace Game.Tests.Admin;

/// <summary>T-804's rules without a listener: the options and their refusals, the credential, the staff member, the role table, the rate limits, the reason.</summary>
public class AdminRulesTests
{
    private static readonly string Secret = AdminRig.Credential;

    private static Func<string, string?> Env(string? credential = null, Dictionary<string, string>? more = null) =>
        name => name == "GAME_ADMIN_CREDENTIAL" ? credential ?? Secret : more is not null && more.TryGetValue(name, out var v) ? v : null;

    private static AdminOptions Parse(bool development, params string[] args) =>
        AdminOptions.Parse(args, Env(), development, out var error) ?? throw new InvalidOperationException(error);

    // ---- options ----

    [Fact]
    public void It_is_off_unless_a_port_is_set_and_then_loopback()
    {
        var off = Parse(development: false);
        off.Enabled.ShouldBeFalse();
        off.Problems(7780, Env(credential: "")).ShouldBeEmpty("off needs nothing, not even a credential");
        var on = Parse(true, "--admin-port", "7781");
        (on.Enabled, on.Port, on.ListenAddress).ShouldBe((true, (int?)7781, IPAddress.Loopback));
        on.Problems(7780, Env()).ShouldBeEmpty();
    }

    [Fact]
    public void Environment_and_command_line_set_it_and_the_command_line_wins()
    {
        var options = AdminOptions.Parse(["--admin-port", "7900", "--admin-private-network", "true"], Env(more: new() { ["GAME_ADMIN_PORT"] = "7800", ["GAME_ADMIN_MAX_PAGE_SIZE"] = "50" }), false, out _)!;
        (options.Port, options.MaxPageSize, options.PrivateNetwork).ShouldBe(((int?)7900, 50, true));
        AdminOptions.Parse(["--admin-nope", "1"], Env(), false, out var error).ShouldBeNull();
        error!.ShouldContain("--admin-nope");
        AdminOptions.Parse(["--admin-port", "x"], Env(), false, out error).ShouldBeNull();
        AdminOptions.Parse(["--admin-port"], Env(), false, out error).ShouldBeNull();
        error!.ShouldContain("needs a value");
        AdminOptions.Usage.ShouldContain("credential-env");
        AdminOptions.Usage.ShouldNotContain("development");
    }

    [Fact]
    public void It_never_shares_the_games_port()
    {
        Parse(true, "--admin-port", "7780").Problems(7780, Env()).ShouldContain(p => p.Contains("game's port"));
        Parse(true, "--admin-port", "0").Problems(0, Env()).ShouldBeEmpty("0 picks a free port for both");
        Parse(true, "--admin-port", "70000").Problems(7780, Env()).ShouldContain(p => p.Contains("0-65535"));
    }

    [Fact]
    public void Development_is_loopback_only_and_plain()
    {
        Parse(true, "--admin-port", "1", "--admin-bind-address", "0.0.0.0").Problems(7780, Env()).ShouldContain(p => p.Contains("loopback only"));
        Parse(true, "--admin-port", "1", "--admin-bind-address", "10.0.0.5", "--admin-private-network", "true").Problems(7780, Env()).ShouldContain(p => p.Contains("loopback only"));
        Parse(true, "--admin-port", "1", "--admin-bind-address", "::1").Problems(7780, Env()).ShouldBeEmpty();
        Parse(true, "--admin-port", "1", "--admin-bind-address", "nope").Problems(7780, Env()).ShouldContain(p => p.Contains("not an IP address"));
    }

    [Fact]
    public void Outside_development_it_needs_TLS_and_a_private_network_to_leave_loopback()
    {
        var certificate = Path.GetTempFileName();
        try
        {
            Parse(false, "--admin-port", "1").Problems(7780, Env()).ShouldContain(p => p.Contains("needs TLS"));
            Parse(false, "--admin-port", "1", "--admin-certificate-path", certificate).Problems(7780, Env()).ShouldBeEmpty("loopback with TLS");
            var open = Parse(false, "--admin-port", "1", "--admin-certificate-path", certificate, "--admin-bind-address", "0.0.0.0").Problems(7780, Env());
            open.ShouldHaveSingleItem().ShouldContain("--admin-private-network true");
            Parse(false, "--admin-port", "1", "--admin-certificate-path", certificate, "--admin-bind-address", "10.8.0.1", "--admin-private-network", "true").Problems(7780, Env())
                .ShouldBeEmpty("a VPN address, said to be private");
            Parse(false, "--admin-port", "1", "--admin-bind-address", "10.8.0.1", "--admin-private-network", "true").Problems(7780, Env()).ShouldContain(p => p.Contains("needs TLS"),
                "private is no excuse for plain HTTP");
            Parse(false, "--admin-port", "1", "--admin-certificate-path", certificate + ".missing").Problems(7780, Env()).ShouldContain(p => p.Contains("does not exist"));
        }
        finally
        {
            File.Delete(certificate);
        }
    }

    [Fact]
    public void It_needs_its_credential_and_never_says_it()
    {
        var options = Parse(true, "--admin-port", "1");
        options.Problems(7780, Env(credential: "")).ShouldContain(p => p.Contains("GAME_ADMIN_CREDENTIAL"));
        var shortOne = "short-secret-value";
        var problems = options.Problems(7780, Env(credential: shortOne));
        problems.ShouldContain(p => p.Contains($"at least {AdminOptions.MinCredentialLength}"));
        problems.ShouldAllBe(p => !p.Contains(shortOne));
        (options with { CredentialEnv = "" }).Problems(7780, Env()).ShouldContain(p => p.Contains("credential-env"));
        (options with { MaxPageSize = 0 }).Problems(7780, Env()).ShouldContain(p => p.Contains("max-page-size"));
        (options with { RateLimitBurst = 0 }).Problems(7780, Env()).ShouldContain(p => p.Contains("rate limits"));
        (options with { StepTimeoutMs = 0 }).Problems(7780, Env()).ShouldContain(p => p.Contains("timeouts"));
        (options with { MaxBodyBytes = 10 }).Problems(7780, Env()).ShouldContain(p => p.Contains("max-body-bytes"));
    }

    // ---- the credential ----

    [Fact]
    public void The_credential_matches_only_itself()
    {
        var credential = new ServiceCredential(Secret);
        credential.Matches(Secret).ShouldBeTrue();
        credential.Matches(" " + Secret + " ").ShouldBeFalse("the header is trimmed before, not here");
        credential.Matches(Secret[..^1]).ShouldBeFalse();
        credential.Matches(Secret + "x").ShouldBeFalse();
        credential.Matches(Secret.ToUpperInvariant()).ShouldBeFalse();
        credential.Matches("").ShouldBeFalse();
        credential.Matches(null).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new ServiceCredential("too short"));
        ServiceCredential.FromHeader("Bearer " + Secret).ShouldBe(Secret);
        ServiceCredential.FromHeader("Bearer  " + Secret + " ").ShouldBe(Secret);
        ServiceCredential.FromHeader("Basic " + Secret).ShouldBeNull();
        ServiceCredential.FromHeader(null).ShouldBeNull();
    }

    [Fact]
    public void The_credential_keeps_no_plaintext_and_compares_fixed_size_digests()
    {
        var credential = new ServiceCredential(Secret);
        credential.ToString().ShouldNotContain(Secret);
        credential.Key.ShouldNotContain(Secret[..8]);
        var fields = typeof(ServiceCredential).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in fields)
        {
            var value = field.GetValue(credential);
            (value as string ?? string.Empty).ShouldNotContain(Secret);
            if (value is byte[] bytes) bytes.Length.ShouldBe(32, "a SHA-256 digest: every candidate is compared as 32 bytes, whatever its length");
        }
        fields.ShouldContain(f => f.FieldType == typeof(byte[]));
        // The comparison is CryptographicOperations.FixedTimeEquals over the two digests (by construction: its time depends on neither the content nor the length).
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Game.Server", "Admin", "AdminAccess.cs"));
        source.ShouldContain("CryptographicOperations.FixedTimeEquals(_digest, Digest(candidate");
    }

    // ---- the staff member ----

    [Theory]
    [InlineData("oidc|abc-123", "Robin Q", "support", null)]
    [InlineData("a@b.c:1_2", "R%C3%A9mi", "admin", null)]
    [InlineData(null, "Robin", "support", AdminErrors.InvalidStaff)]
    [InlineData("", "Robin", "support", AdminErrors.InvalidStaff)]
    [InlineData("has space", "Robin", "support", AdminErrors.InvalidStaff)]
    [InlineData("x", null, "support", AdminErrors.InvalidStaff)]
    [InlineData("x", "%0A", "support", AdminErrors.InvalidStaff)]
    [InlineData("x", "%7F", "support", AdminErrors.InvalidStaff)]
    [InlineData("x", "Robin", null, AdminErrors.UnknownRole)]
    [InlineData("x", "Robin", "Support", AdminErrors.UnknownRole)]
    [InlineData("x", "Robin", "owner", AdminErrors.UnknownRole)]
    public void The_staff_member_is_checked_for_shape_and_role(string? id, string? name, string? role, string? error)
    {
        var staff = StaffIdentity.Parse(id, name, role, out var refused);
        refused.ShouldBe(error);
        (staff is null).ShouldBe(error is not null);
        if (staff is not null && name == "R%C3%A9mi") staff.Name.ShouldBe("Rémi");
    }

    [Fact]
    public void Staff_ids_and_names_have_limits_and_the_log_shows_the_id_only()
    {
        StaffIdentity.Parse(new string('a', 128), "R", "gm", out _).ShouldNotBeNull();
        StaffIdentity.Parse(new string('a', 129), "R", "gm", out _).ShouldBeNull();
        StaffIdentity.Parse("a", new string('n', 64), "gm", out _).ShouldNotBeNull();
        StaffIdentity.Parse("a", new string('n', 65), "gm", out _).ShouldBeNull();
        StaffIdentity.Parse("a", "   ", "gm", out _).ShouldBeNull();
        new StaffIdentity("oidc-7", "Robin Q", "gm").ToString().ShouldBe("staff oidc-7 (gm)");
    }

    // ---- roles and the command table ----

    [Fact]
    public void Roles_are_ranked_and_an_unknown_role_is_never_enough()
    {
        StaffRoles.Rank("support").ShouldBe(0);
        StaffRoles.Rank("gm").ShouldBe(1);
        StaffRoles.Rank("admin").ShouldBe(2);
        StaffRoles.Rank("root").ShouldBe(-1);
        StaffRoles.Rank(null).ShouldBe(-1);
        StaffRoles.AtLeast("admin", "gm").ShouldBeTrue();
        StaffRoles.AtLeast("gm", "gm").ShouldBeTrue();
        StaffRoles.AtLeast("support", "gm").ShouldBeFalse();
        StaffRoles.AtLeast("root", "support").ShouldBeFalse();
    }

    [Fact]
    public void The_command_table_has_every_action_once_with_its_lowest_role()
    {
        AdminCommandTable.LowestRole.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(AdminActions.All.Ordered.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var role in AdminCommandTable.LowestRole.Values) StaffRoles.All.Contains(role).ShouldBeTrue(role);
        AdminCommandTable.LowestRole[AdminActions.Kick].ShouldBe(StaffRoles.Support);
        AdminCommandTable.ReadRole.ShouldBe(StaffRoles.Support);
        foreach (var role in StaffRoles.All) AdminCommandTable.Allows(AdminActions.Kick, role).ShouldBeTrue(role);
        AdminCommandTable.Allows(AdminActions.Kick, "guest").ShouldBeFalse();
        AdminCommandTable.Allows("no_such_action", StaffRoles.Admin).ShouldBeFalse("an action that is not in the table is never allowed");
    }

    // ---- rate limits ----

    [Fact]
    public void A_credential_gets_its_burst_then_its_rate()
    {
        var time = new FakeTime();
        var limits = new AdminRateLimit(new AdminOptions { RateLimitPerSecond = 2, RateLimitBurst = 3 }, time);
        for (var i = 0; i < 3; i++) limits.AllowRequest("k", out _).ShouldBeTrue();
        limits.AllowRequest("k", out var retry).ShouldBeFalse();
        retry.ShouldBe(1);
        limits.AllowRequest("other", out _).ShouldBeTrue("each credential has its own bucket");
        time.Now += TimeSpan.FromMilliseconds(500);
        limits.AllowRequest("k", out _).ShouldBeTrue();
        limits.AllowRequest("k", out _).ShouldBeFalse();
        time.Now -= TimeSpan.FromHours(1);
        limits.AllowRequest("k", out _).ShouldBeFalse("a clock set back gives nothing");
    }

    [Fact]
    public void Failed_credentials_lock_their_address_out_for_a_while()
    {
        var time = new FakeTime();
        var limits = new AdminRateLimit(new AdminOptions { FailureBurst = 2, FailuresPerMinute = 6 }, time);
        var address = IPAddress.Parse("10.1.2.3");
        limits.MayTry(address, out _).ShouldBeTrue();
        limits.CountFailure(address);
        limits.MayTry(address, out _).ShouldBeTrue();
        limits.CountFailure(address);
        limits.MayTry(address, out var wait).ShouldBeFalse();
        wait.ShouldBe(10);
        limits.MayTry(IPAddress.Parse("10.1.2.4"), out _).ShouldBeTrue("another address");
        limits.MayTry(IPAddress.Parse("2001:db8::1"), out _).ShouldBeTrue();
        time.Now += TimeSpan.FromSeconds(10);
        limits.MayTry(address, out _).ShouldBeTrue();
    }

    [Fact]
    public void The_failure_table_is_bounded()
    {
        var limits = new AdminRateLimit(new AdminOptions { FailureBurst = 1 }, new FakeTime());
        for (var i = 0; i < AdminRateLimit.MaxKeys + 10; i++) limits.CountFailure(new IPAddress(BitConverter.GetBytes(i + 1)));
        var table = (System.Collections.IDictionary)typeof(AdminRateLimit).GetField("_failures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(limits)!;
        table.Count.ShouldBeLessThanOrEqualTo(AdminRateLimit.MaxKeys);
    }

    // ---- the reason, the request id and audit values ----

    [Theory]
    [InlineData("{\"reason\":\"spam\"}", "spam")]
    [InlineData("{\"reason\":\"  two\\nlines\\t \"}", "two\nlines")]
    [InlineData("{\"reason\":\"\"}", null)]
    [InlineData("{\"reason\":\"   \"}", null)]
    [InlineData("{\"reason\":7}", null)]
    [InlineData("{}", null)]
    [InlineData("{\"reason\":\"bell\\u0007\"}", null)]
    public void A_write_needs_its_reason(string json, string? reason)
    {
        AdminCommands.ReasonOf(JsonDocument.Parse(json).RootElement).ShouldBe(reason);
    }

    [Fact]
    public void A_reason_has_a_length_limit()
    {
        AdminCommands.ReasonOf(JsonDocument.Parse($"{{\"reason\":\"{new string('r', AdminCommandFields.MaxReasonLength)}\"}}").RootElement).ShouldNotBeNull();
        AdminCommands.ReasonOf(JsonDocument.Parse($"{{\"reason\":\"{new string('r', AdminCommandFields.MaxReasonLength + 1)}\"}}").RootElement).ShouldBeNull();
    }

    [Fact]
    public void A_request_id_is_kept_when_it_is_one_and_made_otherwise()
    {
        AdminApi.RequestIdOf("panel-1.a_b").ShouldBe("panel-1.a_b");
        foreach (var bad in new[] { null, "", "has space", new string('a', 65), "new\nline", "ü" })
            AdminApi.RequestIdOf(bad).ShouldMatch("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Audit_values_are_ids_numbers_booleans_and_text_only()
    {
        var id = Guid.NewGuid();
        AdminJson.Object(new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2L, ["c"] = true, ["d"] = "x", ["e"] = null, ["f"] = id })
            .ShouldBe($"{{\"a\":1,\"b\":2,\"c\":true,\"d\":\"x\",\"e\":null,\"f\":\"{id}\"}}");
        Should.Throw<ArgumentException>(() => AdminJson.Object(new Dictionary<string, object?> { ["x"] = new object() }));
    }

    [Fact]
    public void A_command_needs_a_row_in_the_table_and_is_there_once()
    {
        var road = new AdminRoad(null!, null!, new MemoryAdminStore(), new AdminOptions(), TimeProvider.System);
        var events = new AdminEvents(new ListLog());
        Should.Throw<ArgumentException>(() => new AdminCommands([new Unlisted()], road, events, new ListLog())).Message.ShouldContain("AdminCommandTable");
        Should.Throw<ArgumentException>(() => new AdminCommands([new KickCommand(), new KickCommand()], road, events, new ListLog()));
    }

    private sealed class Unlisted : IAdminCommand
    {
        public string Action => "unlisted";

        public Task<AdminResult> RunAsync(AdminCall call, AdminRoad road, CancellationToken cancellation) => throw new NotSupportedException();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new InvalidOperationException("Game.sln not found.");
    }
}
