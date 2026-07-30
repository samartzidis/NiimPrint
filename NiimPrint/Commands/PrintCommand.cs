using System.CommandLine;
using Niimbot;
using Serilog;
using Serilog.Events;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Spectre.Console;

namespace NiimPrint.Commands;

public static class PrintCommand
{
    private static readonly string[] Rotations = ["0", "90", "180", "270"];

    public static Command Build()
    {
        var modelOption = new Option<string>("--model", "-m")
        {
            Description = "Niimbot printer model",
            DefaultValueFactory = _ => PrinterModels.Default,
        };
        modelOption.AcceptOnlyFromAmong([.. PrinterModels.Names]);

        var densityOption = new Option<int>("--density", "-d")
        {
            Description = "Print density (1-5)",
            DefaultValueFactory = _ => 3,
        };

        var quantityOption = new Option<int>("--quantity", "-n")
        {
            Description = "Print quantity",
            DefaultValueFactory = _ => 1,
        };

        var verticalOffsetOption = new Option<int>("--vo")
        {
            Description = "Vertical offset in pixels",
            DefaultValueFactory = _ => 0,
        };

        var horizontalOffsetOption = new Option<int>("--ho")
        {
            Description = "Horizontal offset in pixels",
            DefaultValueFactory = _ => 0,
        };

        var rotateOption = new Option<string>("--rotate", "-r")
        {
            Description = "Image rotation, clockwise (0, 90, 180, 270)",
            DefaultValueFactory = _ => "0",
        };
        rotateOption.AcceptOnlyFromAmong(Rotations);

        var thresholdOption = new Option<int>("--threshold", "-t")
        {
            Description = "Black/white cutoff (0-255) for converting the image to 1-bit",
            DefaultValueFactory = _ => 128,
        };

        var ditherOption = new Option<bool>("--dither")
        {
            Description = "Apply Floyd-Steinberg dithering instead of a flat threshold cutoff",
            DefaultValueFactory = _ => false,
        };

        var imageArgument = new Argument<string?>("image")
        {
            Description = "Image path",
            DefaultValueFactory = _ => null,
        };

        var imageOption = new Option<string?>("--image", "-i")
        {
            Description = "Image path (overrides the positional argument if both are given)",
        };

        var verboseOption = new Option<int>("--verbose", "-v")
        {
            Description = "Verbosity level: 0=info, 1=info, 2=debug, 3=trace",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command("print", "Print an image on a Niimbot label printer.")
        {
            imageArgument,
            modelOption,
            densityOption,
            quantityOption,
            verticalOffsetOption,
            horizontalOffsetOption,
            rotateOption,
            thresholdOption,
            ditherOption,
            imageOption,
            verboseOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var model = parseResult.GetValue(modelOption)!.ToLowerInvariant();
            var density = parseResult.GetValue(densityOption);
            var quantity = parseResult.GetValue(quantityOption);
            var verticalOffset = parseResult.GetValue(verticalOffsetOption);
            var horizontalOffset = parseResult.GetValue(horizontalOffsetOption);
            var rotate = parseResult.GetValue(rotateOption)!;
            var threshold = parseResult.GetValue(thresholdOption);
            var dither = parseResult.GetValue(ditherOption);
            var verbose = parseResult.GetValue(verboseOption);
            var imagePath = parseResult.GetValue(imageOption) ?? parseResult.GetValue(imageArgument) ?? string.Empty;

            if (density is < 1 or > 5)
            {
                AnsiConsole.MarkupLine("[bold red]Density must be between 1 and 5.[/]");
                return 1;
            }

            if (verbose is < 0 or > 3)
            {
                AnsiConsole.MarkupLine("[bold red]Verbose must be between 0 and 3.[/]");
                return 1;
            }

            if (threshold is < 0 or > 255)
            {
                AnsiConsole.MarkupLine("[bold red]Threshold must be between 0 and 255.[/]");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                AnsiConsole.MarkupLine("[bold red]Image path is required and must exist.[/]");
                return 1;
            }

            Program.LevelSwitch.MinimumLevel = verbose switch
            {
                0 or 1 => LogEventLevel.Information,
                2 => LogEventLevel.Debug,
                _ => LogEventLevel.Verbose,
            };
            Log.Information("Niimbot Printing Start");

            var spec = PrinterModels.Get(model);
            // Fixed dots-across-the-print-head - i.e. the tape stock's physical width,
            // the short axis of the finished label.
            int maxHeadWidthPx = spec.MaxHeadWidthPx;
            int effectiveDensity = Math.Min(density, spec.MaxDensity);

            try
            {
                using var image = await Image.LoadAsync<L8>(imagePath, cancellationToken);

                if (rotate != "0")
                {
                    var mode = rotate switch
                    {
                        "90" => RotateMode.Rotate90,
                        "180" => RotateMode.Rotate180,
                        "270" => RotateMode.Rotate270,
                        _ => RotateMode.None,
                    };
                    image.Mutate(ctx => ctx.Rotate(mode));
                }

                if (image.Width > maxHeadWidthPx && image.Height <= maxHeadWidthPx)
                {
                    // Landscape input (e.g. from `canvas`) that doesn't fit as-is but would
                    // fit rotated - auto-rotate rather than forcing the caller to pass -r 90.
                    image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate90));
                    Log.Information("Auto-rotated image 90 degrees to fit the print head width.");
                }

                if (image.Width > maxHeadWidthPx)
                    throw new PrinterException($"Image width too big for {model.ToUpperInvariant()}");

                // Sanity guard: without this, a huge image would silently turn into a
                // print job streaming thousands of rows one BLE write at a time with no
                // progress feedback, taking minutes with no way to tell it isn't hung.
                int labelLengthPx = image.Height + Math.Max(verticalOffset, 0);
                if (labelLengthPx > PrinterModels.MaxLengthPx)
                    throw new PrinterException($"Label length too big ({labelLengthPx}px) - exceeds the {PrinterModels.MaxLengthPx}px sanity limit for a single print job.");

                return await RunPrintJobAsync(model, effectiveDensity, image, quantity, verticalOffset, horizontalOffset, threshold, dither);
            }
            catch (Exception e)
            {
                Log.Information(e.Message);
                AnsiConsole.MarkupLine($"[bold red]{e.Message.EscapeMarkup()}[/]");
                return 1;
            }
        });

        return command;
    }

    private static async Task<int> RunPrintJobAsync(string model, int density, Image<L8> image, int quantity, int verticalOffset, int horizontalOffset, int threshold, bool dither)
    {
        PrinterClient? printer = null;
        try
        {
            AnsiConsole.MarkupLine("[bold blue]Starting print job[/]");
            var device = await DeviceFinder.FindDeviceAsync(model);
            printer = new PrinterClient(device);
            if (await printer.ConnectAsync())
                AnsiConsole.WriteLine($"Connected to {printer.DeviceName}");

            await printer.PrintImageAsync(image, density, quantity, verticalOffset, horizontalOffset, threshold, dither);
            AnsiConsole.MarkupLine("[bold green]Print job completed[/]");
            await printer.DisconnectAsync();
            return 0;
        }
        catch (Exception e)
        {
            Log.Debug(e.Message);
            AnsiConsole.MarkupLine($"[bold red]{e.Message.EscapeMarkup()}[/]");
            if (printer is not null)
                await printer.DisconnectAsync();
            return 1;
        }
    }
}
