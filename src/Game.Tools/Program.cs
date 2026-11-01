using System.CommandLine;
using Game.Tools.Commands;

var root = new RootCommand("Game content and build tools");
root.Subcommands.Add(SchemaCommand.Create());
root.Subcommands.Add(ValidateCommand.Create());
root.Subcommands.Add(StatsCommand.Create());

return root.Parse(args).Invoke();
