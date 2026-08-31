using ConstructionCrew.Core.Models;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Maps a JobsiteColorPalette name (plain data, no UI dependency) to an
/// actual Spectre.Console.Color. Built from explicit RGB triples rather than
/// Spectre's named-color constants, so it doesn't depend on which extended
/// color names Spectre exposes.
/// </summary>
public static class JobsiteColors
{
    private static readonly IReadOnlyDictionary<string, Color> Palette = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new Color(220, 60, 60),
        ["green"] = new Color(60, 180, 90),
        ["yellow"] = new Color(210, 190, 60),
        ["blue"] = new Color(70, 130, 220),
        ["magenta"] = new Color(190, 70, 190),
        ["cyan"] = new Color(60, 180, 190),
        ["orange"] = new Color(220, 140, 60),
        ["purple"] = new Color(140, 90, 200),
    };

    public static Color Resolve(string? colorName) =>
        colorName is not null && Palette.TryGetValue(colorName, out var color) ? color : Color.Grey;

    /// <summary>Resolves a jobsite's color, falling back to a stable per-name derivation if none was persisted.</summary>
    public static Color ResolveForJobsite(JobsiteConfig jobsite) =>
        Resolve(jobsite.ColorName ?? JobsiteColorPalette.DeriveDeterministic(jobsite.Name));
}
