using System.CommandLine;
using Game.Tools.Commands;

const int NotImplementedExitCode = 2;

var root = new RootCommand("Game content and build tools");
foreach (var (name, description) in new[]
{
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

root.Subcommands.Add(SchemaCommand.Create());
root.Subcommands.Add(ValidateCommand.Create());

return root.Parse(args).Invoke();
