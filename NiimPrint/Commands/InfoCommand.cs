using System.CommandLine;
using Niimbot;
using Serilog;
using Serilog.Events;
using Spectre.Console;

namespace NiimPrint.Commands;

public static class InfoCommand
{
    public static Command Build()
    {
        var modelOption = new Option<string>("--model", "-m")
        {
            Description = "Niimbot printer model",
            DefaultValueFactory = _ => PrinterModels.Default,
        };
        modelOption.AcceptOnlyFromAmong([.. PrinterModels.Names]);

        var verboseOption = new Option<int>("--verbose", "-v")
        {
            Description = "Verbosity level: 0=info, 1=info, 2=debug, 3=trace",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command("info", "Show Niimbot printer information.")
        {
            modelOption,
            verboseOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var model = parseResult.GetValue(modelOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            if (verbose is < 0 or > 3)
            {
                AnsiConsole.MarkupLine("[bold red]Verbose must be between 0 and 3.[/]");
                return 1;
            }

            Program.LevelSwitch.MinimumLevel = verbose switch
            {
                0 or 1 => LogEventLevel.Information,
                2 => LogEventLevel.Debug,
                _ => LogEventLevel.Verbose,
            };
            Log.Information("Niimbot Information");
            AnsiConsole.MarkupLine("[bold blue]Niimbot Information[/]");

            PrinterClient? printer = null;
            try
            {
                var device = await DeviceFinder.FindDeviceAsync(model.ToLowerInvariant());
                printer = new PrinterClient(device);
                await printer.ConnectAsync();

                var deviceSerial = await printer.GetInfoAsync(InfoKind.DeviceSerial);
                var softwareVersion = await printer.GetInfoAsync(InfoKind.SoftVersion);
                var hardwareVersion = await printer.GetInfoAsync(InfoKind.HardVersion);
                var battery = await printer.GetInfoAsync(InfoKind.Battery);
                // The printer reports a raw 0-4 level (like a signal-bars indicator),
                // not a true percentage - this is an approximation of that level.
                var batteryPercent = battery is long level ? level * 100 / 4 : (long?)null;

                AnsiConsole.WriteLine($"Device Serial : {deviceSerial}");
                AnsiConsole.WriteLine($"Software Version : {softwareVersion}");
                AnsiConsole.WriteLine($"Hardware Version : {hardwareVersion}");
                AnsiConsole.WriteLine($"Battery Level : {batteryPercent}%");

                await printer.DisconnectAsync();
            }
            catch (Exception e)
            {
                Log.Debug(e.Message);
                AnsiConsole.MarkupLine($"[bold red]{e.Message.EscapeMarkup()}[/]");
                if (printer is not null)
                    await printer.DisconnectAsync();
            }

            return 0;
        });

        return command;
    }
}
