using System.CommandLine;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Spectre.Console;

namespace NiimPrint.Commands;

public static class CanvasCommand
{
    public static Command Build()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Output PNG file name (with or without .png extension)",
        };

        var modelOption = new Option<string>("--model", "-m")
        {
            Description = "Niimbot printer model",
            DefaultValueFactory = _ => PrinterModels.Default,
        };
        modelOption.AcceptOnlyFromAmong([.. PrinterModels.Names]);

        var lengthArgument = new Argument<int?>("length")
        {
            Description = "Label length in millimeters (the feed direction); width is fixed by the model's max head width",
            DefaultValueFactory = _ => null,
        };

        var lengthOption = new Option<int?>("--length", "-l")
        {
            Description = "Label length in millimeters (overrides the positional argument if both are given)",
        };

        var command = new Command("canvas", "Create a blank PNG canvas sized for a printer model and label length.")
        {
            nameArgument,
            modelOption,
            lengthArgument,
            lengthOption,
        };

        command.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var model = parseResult.GetValue(modelOption)!;
            var lengthMm = parseResult.GetValue(lengthOption) ?? parseResult.GetValue(lengthArgument) ?? 0;

            if (lengthMm <= 0)
            {
                AnsiConsole.MarkupLine("[bold red]Length must be greater than 0.[/]");
                return 1;
            }

            var spec = PrinterModels.Get(model);
            int width = (int)Math.Round(lengthMm * spec.PixelsPerMm);
            int height = spec.MaxHeadWidthPx;

            if (width > PrinterModels.MaxLengthPx)
            {
                AnsiConsole.MarkupLine($"[bold red]Label length too big ({width}px) - exceeds the {PrinterModels.MaxLengthPx}px sanity limit for a single print job.[/]");
                return 1;
            }

            var fileName = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";

            using var image = new Image<L8>(width, height, new L8(255));
            image.Save(fileName);

            AnsiConsole.MarkupLine($"[bold green]Created {fileName.EscapeMarkup()} ({width}x{height}px)[/]");
            return 0;
        });

        return command;
    }
}
