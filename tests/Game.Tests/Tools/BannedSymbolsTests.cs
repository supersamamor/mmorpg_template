using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Game.Tests.Tools;

/// <summary>
/// Proves rule 8 is enforced by the compiler: a scratch project that uses the real <c>src/Game.Shared/BannedSymbols.txt</c> and the
/// same analyzer package version fails to build when its code reads the clock, creates a <c>Random</c>, touches the disk and so on,
/// and builds when it uses none of them. Runs <c>dotnet build</c>, so it needs the analyzer package in the NuGet cache (CI restores it).
/// </summary>
[Collection(TimingCollection.Name)]
public class BannedSymbolsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "banned_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static string AnalyzerVersion() =>
        Regex.Match(File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Packages.props")),
            "Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\" Version=\"([^\"]+)\"").Groups[1].Value;

    private (int Exit, string Output) Build(string source)
    {
        Directory.CreateDirectory(_temp);
        File.WriteAllText(Path.Combine(_temp, "Code.cs"), source);
        var symbols = Path.Combine(RepoRoot(), "src", "Game.Shared", "BannedSymbols.txt");
        File.WriteAllText(Path.Combine(_temp, "Scratch.csproj"), $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Code.cs"" />
    <PackageReference Include=""Microsoft.CodeAnalysis.BannedApiAnalyzers"" Version=""{AnalyzerVersion()}"" PrivateAssets=""all"" />
    <AdditionalFiles Include=""{symbols}"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(_temp, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_temp, "Directory.Packages.props"), "<Project />");

        var info = new ProcessStartInfo("dotnet", "build --nologo -v:q") { WorkingDirectory = _temp, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void Code_that_uses_the_clock_randomness_or_the_disk_does_not_build()
    {
        var (exit, output) = Build(@"
using System;
using System.Diagnostics;
using System.IO;

public static class Offender
{
    public static object[] All() => new object[]
    {
        DateTime.Now, DateTime.UtcNow, DateTimeOffset.UtcNow, Environment.TickCount64,
        new Random(), new Random(1), Random.Shared, Guid.NewGuid(),
        Stopwatch.StartNew(), File.Exists(""x""), Directory.Exists(""x""),
    };
}");

        exit.ShouldNotBe(0, output);
        foreach (var symbol in new[] { "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.UtcNow", "Environment.TickCount64", "Random.Random()",
                     "Random.Random(int)", "Random.Shared", "Guid.NewGuid()", "Stopwatch", "File", "Directory" })
            output.ShouldContain($"RS0030: The symbol '{symbol}' is banned", customMessage: output);
    }

    [Fact]
    public void Code_that_uses_none_of_them_builds()
    {
        var (exit, output) = Build(@"
public static class Fine
{
    public static long Next(long seed) => seed * 6364136223846793005L + 1442695040888963407L;
}");

        exit.ShouldBe(0, output);
    }
}
