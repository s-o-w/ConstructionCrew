using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Renders the Markdown subset the crew actually writes into Spectre
/// renderables. Supported: YAML frontmatter (dim panel), ATX headings h1-h3,
/// fenced code blocks, `-`/`*` bullets, ordered lists, `&gt;` blockquotes, GFM
/// pipe tables, thematic breaks, and inline **bold**, *italic*, `code` and
/// [[wikilinks]]. Everything else renders as literal text. Not a general
/// Markdown implementation and not trying to be -- see the plan's Phase 10.
///
/// <para>
/// Safety property, and the reason this file is worth reading before editing:
/// every character that comes from the source document goes through
/// <see cref="Markup.Escape"/> BEFORE any styling tag is added, and a fenced
/// code block's body goes into a <see cref="Text"/> (which never interprets
/// markup) rather than a <see cref="Markup"/>. Crew-authored content therefore
/// cannot inject Spectre markup -- neither a stray `[` in a sentence (which
/// would throw and take the TUI down) nor a deliberate `[red]` in a code
/// sample (which would silently recolor the console).
/// </para>
/// </summary>
public static class MarkdownRenderer
{
    private static readonly string[] ViewableFenceMarkers = ["```", "~~~"];

    /// <summary>Matches a GFM alignment row cell: ---, :---, ---:, :---:.</summary>
    private static readonly Regex AlignmentCell = new(@"^:?-{1,}:?$", RegexOptions.Compiled);

    private static readonly Regex OrderedItem = new(@"^(\s*)(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);

    private static readonly Regex BulletItem = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^(#{1,3})\s+(.*)$", RegexOptions.Compiled);

    // Runs against ALREADY-ESCAPED text, where a source `[` is "[[" -- so a
    // source [[wikilink]] arrives here as "[[[[wikilink]]]]".
    private static readonly Regex EscapedWikiLink = new(@"\[\[\[\[([^\[\]]+?)\]\]\]\]", RegexOptions.Compiled);

    private static readonly Regex BoldSpan = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

    private static readonly Regex ItalicSpan = new(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)", RegexOptions.Compiled);

    private static readonly Regex CodeSpan = new(@"`([^`\r\n]+)`", RegexOptions.Compiled);

    private static readonly Regex CodeSpanSlot = new("\u0000(\\d+)\u0000", RegexOptions.Compiled);

    public static IReadOnlyList<IRenderable> RenderBlocks(string markdown)
    {
        var blocks = new List<IRenderable>();
        if (string.IsNullOrEmpty(markdown))
        {
            return blocks;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;

        // Frontmatter is only frontmatter on line one. A "---" anywhere else is
        // a thematic break, which is why this is not part of the main loop.
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = -1;
            for (var scan = 1; scan < lines.Length; scan++)
            {
                if (lines[scan].Trim() is "---" or "...")
                {
                    end = scan;
                    break;
                }
            }

            if (end > 0)
            {
                var body = string.Join(Environment.NewLine, lines[1..end]);
                blocks.Add(
                    new Panel(new Text(body, new Style(foreground: Color.Grey)))
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Grey));
                i = end + 1;
            }
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                // One blank separator per run of blank lines, and never a leading one.
                if (blocks.Count > 0)
                {
                    blocks.Add(Text.Empty);
                }

                while (i < lines.Length && lines[i].Trim().Length == 0)
                {
                    i++;
                }

                continue;
            }

            if (TryFenceMarker(trimmed, out var fence))
            {
                blocks.Add(ReadFencedCode(lines, ref i, fence));
                continue;
            }

            if (IsThematicBreak(trimmed))
            {
                blocks.Add(new Rule());
                i++;
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var text = RenderInline(heading.Groups[2].Value.TrimEnd('#').Trim());
                blocks.Add(heading.Groups[1].Value.Length == 1
                    ? new Rule($"[bold]{text}[/]").LeftJustified()
                    : new Markup($"[bold]{text}[/]"));
                i++;
                continue;
            }

            if (TryReadTable(lines, ref i, out var table))
            {
                blocks.Add(table!);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                blocks.Add(ReadBlockQuote(lines, ref i));
                continue;
            }

            if (BulletItem.IsMatch(line) || OrderedItem.IsMatch(line))
            {
                blocks.Add(ReadList(lines, ref i));
                continue;
            }

            blocks.Add(ReadParagraph(lines, ref i));
        }

        return blocks;
    }

    public static IRenderable Render(string markdown) => new Rows(RenderBlocks(markdown));

    /// <summary>
    /// Source text -&gt; Spectre markup. The escape happens FIRST and the styling
    /// tags are added on top of already-escaped text, so nothing in the source
    /// can ever be read back as a markup tag. Code spans are lifted out before
    /// the emphasis passes so `**not bold**` inside backticks stays literal.
    /// </summary>
    internal static string RenderInline(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var escaped = Markup.Escape(text);

        // Code spans are parked behind a NUL-delimited slot number so the
        // emphasis passes below cannot reach inside them, then put back styled.
        // NUL is used because it cannot occur in a text file the crew wrote.
        var codeSpans = new List<string>();
        escaped = CodeSpan.Replace(escaped, match =>
        {
            codeSpans.Add(match.Groups[1].Value);
            return $"\u0000{codeSpans.Count - 1}\u0000";
        });

        escaped = EscapedWikiLink.Replace(escaped, match => $"[blue]{match.Value}[/]");
        escaped = BoldSpan.Replace(escaped, "[bold]$1[/]");
        escaped = ItalicSpan.Replace(escaped, "[italic]$1[/]");

        if (codeSpans.Count == 0)
        {
            return escaped;
        }

        return CodeSpanSlot.Replace(escaped, match =>
        {
            var slot = int.Parse(match.Groups[1].Value);
            return slot < codeSpans.Count ? $"[grey85 on grey19]{codeSpans[slot]}[/]" : string.Empty;
        });
    }

    private static bool TryFenceMarker(string trimmed, out string fence)
    {
        foreach (var marker in ViewableFenceMarkers)
        {
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                fence = marker;
                return true;
            }
        }

        fence = string.Empty;
        return false;
    }

    /// <summary>
    /// The body goes into a <see cref="Text"/>, never a <see cref="Markup"/> --
    /// a code sample containing "[red]" has to come out as the four characters
    /// the author typed, not as a color tag.
    /// </summary>
    private static IRenderable ReadFencedCode(string[] lines, ref int i, string fence)
    {
        i++; // the opening fence itself

        var body = new List<string>();
        while (i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal))
        {
            body.Add(lines[i]);
            i++;
        }

        if (i < lines.Length)
        {
            i++; // the closing fence
        }

        return new Panel(new Text(string.Join(Environment.NewLine, body)))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
    }

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        var c = trimmed[0];
        return c is '-' or '*' or '_' && trimmed.All(ch => ch == c);
    }

    /// <summary>
    /// A GFM pipe table is only a table when the line after the header is an
    /// alignment row. Without that check a prose line that happens to contain a
    /// pipe would be shredded into columns.
    /// </summary>
    private static bool IsTableStart(string[] lines, int i)
    {
        if (i + 1 >= lines.Length || !lines[i].Contains('|'))
        {
            return false;
        }

        var headerCells = SplitRow(lines[i]);
        var alignmentCells = SplitRow(lines[i + 1]);

        return headerCells.Count > 0 &&
               alignmentCells.Count > 0 &&
               alignmentCells.All(c => AlignmentCell.IsMatch(c.Trim()));
    }

    private static bool TryReadTable(string[] lines, ref int i, out Table? table)
    {
        table = null;

        if (!IsTableStart(lines, i))
        {
            return false;
        }

        var headerCells = SplitRow(lines[i]);

        table = new Table().Border(TableBorder.Rounded);
        foreach (var header in headerCells)
        {
            table.AddColumn(RenderInline(header.Trim()));
        }

        i += 2; // header + alignment row (the alignment row itself is discarded)

        while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
        {
            var cells = SplitRow(lines[i]);

            // Table.AddRow throws on a cell-count mismatch, and a hand-written
            // table with a short row is not worth crashing the viewer over.
            var row = new string[headerCells.Count];
            for (var c = 0; c < headerCells.Count; c++)
            {
                row[c] = c < cells.Count ? RenderInline(cells[c].Trim()) : string.Empty;
            }

            table.AddRow(row);
            i++;
        }

        return true;
    }

    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.Length == 0)
        {
            return [];
        }

        return [.. trimmed.Split('|')];
    }

    private static IRenderable ReadBlockQuote(string[] lines, ref int i)
    {
        var quoted = new List<string>();
        while (i < lines.Length && lines[i].Trim().StartsWith('>'))
        {
            var content = lines[i].Trim()[1..];
            quoted.Add($"[grey]│ {RenderInline(content.Trim())}[/]");
            i++;
        }

        return new Markup(string.Join(Environment.NewLine, quoted));
    }

    private static IRenderable ReadList(string[] lines, ref int i)
    {
        var items = new List<string>();

        while (i < lines.Length)
        {
            var bullet = BulletItem.Match(lines[i]);
            if (bullet.Success)
            {
                items.Add($"{Indent(bullet.Groups[1].Value)}• {RenderInline(bullet.Groups[2].Value)}");
                i++;
                continue;
            }

            var ordered = OrderedItem.Match(lines[i]);
            if (ordered.Success)
            {
                items.Add($"{Indent(ordered.Groups[1].Value)}{ordered.Groups[2].Value}. {RenderInline(ordered.Groups[3].Value)}");
                i++;
                continue;
            }

            break;
        }

        return new Markup(string.Join(Environment.NewLine, items));
    }

    private static string Indent(string leadingWhitespace) => new(' ', leadingWhitespace.Replace("\t", "  ").Length);

    private static IRenderable ReadParagraph(string[] lines, ref int i)
    {
        var paragraph = new List<string>();

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0 ||
                TryFenceMarker(trimmed, out _) ||
                IsThematicBreak(trimmed) ||
                Heading.IsMatch(line) ||
                trimmed.StartsWith('>') ||
                BulletItem.IsMatch(line) ||
                OrderedItem.IsMatch(line) ||
                IsTableStart(lines, i))
            {
                break;
            }

            paragraph.Add(RenderInline(trimmed));
            i++;
        }

        return new Markup(string.Join(Environment.NewLine, paragraph));
    }
}
