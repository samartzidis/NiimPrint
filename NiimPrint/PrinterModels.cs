using Microsoft.Extensions.Configuration;

namespace NiimPrint;

public sealed record PrinterModelSpec(string Name, int MaxHeadWidthPx, int MaxDensity, int Dpi)
{
    public double PixelsPerMm => Dpi / 25.4;
}

// Known printer models and their hardware specs (head width, max density),
// loaded from appsettings.json so new models/specs can be added without a rebuild.
public static class PrinterModels
{
    // Thermal print heads on these printers are ~203 DPI (~8px/mm); no label in
    // practice exceeds 5cm, so this caps at 50mm as a runaway-job tripwire.
    public const int MaxLengthPx = 400;

    private static readonly IConfigurationRoot Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    private static readonly IReadOnlyDictionary<string, PrinterModelSpec> All = Load();

    public static string Default { get; } = Configuration["DefaultModel"]
        ?? throw new InvalidOperationException("appsettings.json is missing DefaultModel.");

    public static IEnumerable<string> Names => All.Keys;

    public static PrinterModelSpec Get(string model) => All[model.ToLowerInvariant()];

    public static bool IsKnown(string model) => All.ContainsKey(model.ToLowerInvariant());

    private static IReadOnlyDictionary<string, PrinterModelSpec> Load()
    {
        var specs = Configuration.GetSection("PrinterModels").Get<PrinterModelSpec[]>();
        if (specs is null || specs.Length == 0)
            throw new InvalidOperationException("appsettings.json is missing a non-empty PrinterModels section.");

        return specs.ToDictionary(s => s.Name.ToLowerInvariant());
    }
}
