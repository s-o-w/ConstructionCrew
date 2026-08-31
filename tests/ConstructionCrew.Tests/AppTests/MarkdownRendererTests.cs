using ConstructionCrew.App.Tui;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// The Markdown subset /view renders. The escaping assertions here are the
/// point of the file: crew-authored content is arbitrary text, and Spectre
/// throws on an unbalanced tag -- an unescaped "[" in a sitrep would take the
/// whole TUI down, and a "[red]" in a code sample would silently recolor it.
/// Every one of those is asserted against real rendered output through a
/// TestConsole, not against the intermediate markup string.
/// </summary>
public class MarkdownRendererTests
{
    private static string RenderToText(IRenderable renderable)
    {
        var console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(renderable);
        return console.Output;
    }

    private static string RenderToText(IEnumerable<IRenderable> blocks) =>
        string.Concat(blocks.Select(RenderToText));

    [Fact]
    public void RenderBlocks_Frontmatter_IsItsOwnBlock()
    {
        var blocks = MarkdownRenderer.RenderBlocks(
            """
            ---
            name: Sitewalk
            jobsite: Frontend
            ---

            # Sitewalk
            """);

        Assert.IsType<Panel>(blocks[0]);

        var frontmatter = RenderToText(blocks[0]);
        Assert.Contains("name: Sitewalk", frontmatter);
        Assert.Contains("jobsite: Frontend", frontmatter);

        // The heading after it is a separate block, not swallowed by the panel.
        Assert.DoesNotContain("# Sitewalk", frontmatter);
        Assert.Contains(blocks.Skip(1), b => b is Rule);
    }

    /// <summary>
    /// The security-relevant case. A fenced body containing "[red]" must reach
    /// the console as those five characters, not as a Spectre color tag -- which
    /// is why fenced code goes into a Text and never a Markup.
    /// </summary>
    [Fact]
    public void RenderBlocks_FencedCode_IsNotInterpretedAsMarkup()
    {
        var blocks = MarkdownRenderer.RenderBlocks(
            """
            ```csharp
            AnsiConsole.MarkupLine("[red]boom[/]");
            var x = arr[0];
            ```
            """);

        var panel = Assert.IsType<Panel>(Assert.Single(blocks));
        var rendered = RenderToText(panel);

        Assert.Contains("[red]boom[/]", rendered);
        Assert.Contains("arr[0]", rendered);
    }

    [Fact]
    public void RenderBlocks_PipeTable_BecomesATable()
    {
        var blocks = MarkdownRenderer.RenderBlocks(
            """
            | task | owner |
            | --- | ----: |
            | wire the panel | Frontend |
            | short row |
            """);

        var table = Assert.IsType<Table>(Assert.Single(blocks));
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(2, table.Rows.Count);

        var rendered = RenderToText(table);
        Assert.Contains("owner", rendered);
        Assert.Contains("wire the panel", rendered);
        Assert.DoesNotContain("---", rendered);
    }

    /// <summary>
    /// The other half of the escaping guarantee: emphasis is applied on top of
    /// already-escaped text, so a bracket inside a bold span cannot close or
    /// open a tag. If the escape happened after styling, this render throws.
    /// </summary>
    [Theory]
    [InlineData("**a[b]**", "a[b]")]
    [InlineData("plain [red] text", "plain [red] text")]
    [InlineData("a [[Wiki Link]] here", "a [[Wiki Link]] here")]
    [InlineData("code `arr[0]` span", "code arr[0] span")]
    [InlineData("unclosed [ bracket", "unclosed [ bracket")]
    [InlineData("*italic [x]* and `[/]`", "italic [x] and [/]")]
    public void RenderInline_EscapesBeforeStyling(string source, string expectedVisibleText)
    {
        var markup = MarkdownRenderer.RenderInline(source);

        // The real assertion: Spectre parses it without throwing, and what comes
        // out the far side is the author's literal characters.
        var rendered = RenderToText(new Markup(markup));

        Assert.Contains(expectedVisibleText, rendered);
        Assert.DoesNotContain("[bold]", rendered);
        Assert.DoesNotContain("[italic]", rendered);
    }

    [Fact]
    public void RenderBlocks_UnsupportedSyntax_FallsBackToLiteralText()
    {
        var blocks = MarkdownRenderer.RenderBlocks(
            """
            ![diagram](./one-line.png)

            <div class="callout">raw html</div>

            #### h4 is outside the subset

            Setext heading
            ==============

            | not | a table without an alignment row
            """);

        var rendered = RenderToText(blocks);

        Assert.Contains("![diagram](./one-line.png)", rendered);
        Assert.Contains("raw html", rendered);
        Assert.Contains("#### h4 is outside the subset", rendered);
        Assert.Contains("Setext heading", rendered);
        Assert.Contains("not | a table without an alignment row", rendered);
    }

    [Fact]
    public void RenderBlocks_ListsQuotesAndBreaks_Render()
    {
        var blocks = MarkdownRenderer.RenderBlocks(
            """
            ## Findings

            - first
            - second with **bold**
              - nested

            1. step one
            2. step two

            > a quoted note

            ---

            trailing paragraph
            """);

        var rendered = RenderToText(blocks);

        Assert.Contains("Findings", rendered);
        Assert.Contains("• first", rendered);
        Assert.Contains("• nested", rendered);
        Assert.Contains("1. step one", rendered);
        Assert.Contains("a quoted note", rendered);
        Assert.Contains("trailing paragraph", rendered);
        Assert.Contains(blocks, b => b is Rule);
    }

    [Fact]
    public void RenderBlocks_EmptyInput_IsEmptyAndRenderDoesNotThrow()
    {
        Assert.Empty(MarkdownRenderer.RenderBlocks(string.Empty));
        Assert.Equal(string.Empty, RenderToText(MarkdownRenderer.Render(string.Empty)).Trim());
    }

    /// <summary>
    /// The whole-file path, against content shaped like what the crew writes.
    /// Any renderer bug that produces unbalanced markup surfaces here as a throw.
    /// </summary>
    [Fact]
    public void Render_WholeDocument_DoesNotThrow()
    {
        var document =
            """
            ---
            type: "[[Workorder]]"
            ---

            # Workorder [1]

            Uses `Foo[T]` and [[Some Note]] and **bold [x]** text.

            | field | value |
            | --- | --- |
            | path | src/a[0].cs |

            ```text
            [red]not a tag[/]
            ```

            > note: `x[1]`
            """;

        var rendered = RenderToText(MarkdownRenderer.Render(document));

        Assert.Contains("[red]not a tag[/]", rendered);
        Assert.Contains("Workorder", rendered);
    }
}
