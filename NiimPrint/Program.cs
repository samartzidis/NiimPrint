using System.CommandLine;
using System.Reflection;
using NiimPrint.Commands;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Spectre.Console;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

if (args.Length == 0)
    AnsiConsole.MarkupLine($"[bold]niimprint[/] v{version}");

var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (!File.Exists(appsettingsPath))
{
    // Every command's Model option defaults from PrinterModels.Default, which
    // lazily loads this file - check for it up front so a missing file gives a
    // clear message instead of an opaque failure once something touches it.
    AnsiConsole.MarkupLine($"[bold red]appsettings.json not found at {appsettingsPath.EscapeMarkup()} - it must sit next to the exe.[/]");
    return 1;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(Program.LevelSwitch)
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "niimprint.log"),
        fileSizeLimitBytes: 100 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} | {Level:u5} | {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var rootCommand = new RootCommand("Niimbot label printer CLI");
rootCommand.Subcommands.Add(PrintCommand.Build());
rootCommand.Subcommands.Add(InfoCommand.Build());
rootCommand.Subcommands.Add(CanvasCommand.Build());

return await rootCommand.Parse(args).InvokeAsync();

partial class Program
{
    // -v/--verbose count -> Serilog level (0/1 -> Info, 2 -> Debug, 3+ -> Verbose),
    // shared so commands can adjust it after parsing their settings.
    public static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);
}
