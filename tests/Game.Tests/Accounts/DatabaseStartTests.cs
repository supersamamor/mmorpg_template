using Game.Server.Hosting;
using Game.Server.Persistence;

namespace Game.Tests.Accounts;

/// <summary>T-701: how the server starts (or does not) without a usable database, as a real process. Needs no PostgreSQL.</summary>
[Collection(TimingCollection.Name)]
public class DatabaseStartTests
{
    [Fact]
    public async Task Outside_development_the_server_refuses_to_start_without_a_database()
    {
        var (code, output) = await DatabaseTests.RunServerAsync(null, "--environment", "Production");
        code.ShouldBe(1);
        output.ShouldContain(GameDatabase.ConnectionVariable);
        output.ShouldContain("Refusing to start.");
    }

    [Fact]
    public async Task The_migrate_command_needs_a_database()
    {
        var (code, output) = await DatabaseTests.RunServerAsync(null, "--environment", "Development", "--migrate");
        code.ShouldBe(1);
        output.ShouldContain("--migrate needs a database");
    }

    [Fact]
    public async Task Development_starts_without_one_and_says_that_accounts_are_off()
    {
        var (code, output) = await DatabaseTests.RunServerAsync(null, "--environment", "Development", "--net-port", "0", "--duration", "1");
        code.ShouldBe(0, output);
        output.ShouldContain("accounts are off");
        output.ShouldNotContain("account endpoints");
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=hunter2;Timeout=2", "cannot be used")]
    [InlineData("gibberish hunter2", "cannot be read")]
    public async Task A_database_that_does_not_work_stops_the_start_in_development_too_without_printing_the_connection_string(string connection, string expected)
    {
        var (code, output) = await DatabaseTests.RunServerAsync(connection, "--environment", "Development", "--net-port", "0", "--duration", "1");
        code.ShouldBe(1);
        output.ShouldContain(expected);
        output.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Migrate_is_an_option()
    {
        ServerOptions.Parse(["--migrate"], out _)!.Migrate.ShouldBeTrue();
        ServerOptions.Parse([], out _)!.Migrate.ShouldBeFalse();
    }
}
