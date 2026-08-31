using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Tests.CoreTests;

public class JobsiteColorPaletteTests
{
    [Fact]
    public void DeriveDeterministic_SameName_AlwaysReturnsSameColor()
    {
        var first = JobsiteColorPalette.DeriveDeterministic("Lighthouse");
        var second = JobsiteColorPalette.DeriveDeterministic("Lighthouse");

        Assert.Equal(first, second);
        Assert.Contains(first, JobsiteColorPalette.Names);
    }

    [Fact]
    public void DeriveDeterministic_DifferentNames_CanReturnDifferentColors()
    {
        var names = new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa" };
        var colors = names.Select(JobsiteColorPalette.DeriveDeterministic).Distinct().Count();

        // Not a strict requirement that every name differs, but with 8 palette
        // colors and 10 distinct inputs, getting only 1 unique color back would
        // indicate DeriveDeterministic is broken (e.g. ignoring its argument).
        Assert.True(colors > 1);
    }

    [Fact]
    public void PickRandom_AlwaysReturnsAPaletteName()
    {
        var random = new Random(42);

        for (var i = 0; i < 20; i++)
        {
            Assert.Contains(JobsiteColorPalette.PickRandom(random), JobsiteColorPalette.Names);
        }
    }
}
