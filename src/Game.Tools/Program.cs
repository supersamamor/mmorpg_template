using System.CommandLine;

const int NotImplementedExitCode = 2;

var root = new RootCommand("Game content and build tools");
foreach (var (name, description) in new[]
{
    ("validate", "Validate all content and cross-references"),
    ("schema", "Generate JSON Schemas from the C# definitions"),
    ("stats", "Print content statistics"),
})
{
    var command = new Command(name, description);
    command.SetAction(_ =>
    {
        Console.WriteLine("not implemented");
        return NotImplementedExitCode;
    });
    root.Subcommands.Add(command);
}

return root.Parse(args).Invoke();
