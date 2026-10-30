using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using HexIDE.Lsp.Messages;
using HexIDE.Themes;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HexIDE.Controls;

/// <summary>
/// Draws a language server's hover text: Markdown as formatted text, plain text exactly as it came.
/// </summary>
/// <remarks>
/// <para>
/// <b>The kind decides, never the text.</b> A hover the server marked <c>plaintext</c> is shown as it is, asterisks
/// and all: parsing it would turn a server's literal <c>*</c> into emphasis it never asked for.
/// </para>
/// <para>
/// <b>No raw HTML.</b> The pipeline disables HTML, so a tag in a hover reads as the text of the tag. An image is
/// shown as its alternative text and never fetched: a hover is opened by moving the pointer, and that must not
/// make a request anywhere.
/// </para>
/// <para>
/// <b>Links are drawn, not followed.</b> A link is an underlined span of text with no handler: nothing in a hover can
/// be clicked through, so a <c>command:</c> link runs nothing.
/// </para>
/// <para>
/// <b>Bounded, whatever the server sends.</b> The text is cut at <see cref="MaxLength"/> characters, and what is nested
/// deeper than <see cref="MaxDepth"/> levels is shown as its own source text rather than drawn, so no reply can make the
/// drawing recurse without end. <see cref="Parse"/> can run off the UI thread; only <see cref="Render(ParsedHover, FontFamily)"/>
/// builds controls.
/// </para>
/// <para>
/// Only what hovers use is drawn as such: paragraphs, emphasis, inline code, fenced and indented code, links,
/// lists, quotes, headings and rules. Anything else shows its text. A fence named <c>vb</c>, <c>vb6</c> or
/// <c>vba</c> is coloured by the code window's syntax highlighting, its definition's colours for keywords, strings and
/// comments; the semantic colours a server gives the code window are not applied to a hover's code.
/// </para>
/// </remarks>
internal static class HoverMarkdown
{
    /// <summary>The fence names whose code is coloured by the code window's syntax highlighting.</summary>
    internal static readonly IReadOnlySet<string> HighlightedFences =
        new HashSet<string>(["vb", "vb6", "vba"], StringComparer.OrdinalIgnoreCase);

    internal const string CodeBackgroundKey = "HoverCodeBackgroundBrush";
    internal const string LinkKey = "HoverLinkBrush";
    internal const string RuleKey = "HoverBorderBrush";

    /// <summary>The most of a hover's text that is shown; the rest is cut, and the cut is marked.</summary>
    internal const int MaxLength = 20_000;

    /// <summary>How deep blocks and inlines are drawn inside one another; deeper ones are shown as their source text.</summary>
    internal const int MaxDepth = 16;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>A hover read and ready to draw: its text, cut to length, and the parsed document when it is Markdown.</summary>
    internal sealed record ParsedHover(string Text, MarkdownDocument? Document);

    /// <summary>Reads a hover. Touches no control, so it can run on whichever thread the answer arrived on.</summary>
    public static ParsedHover Parse(MarkupContent content)
    {
        var text = content.Value.Length <= MaxLength ? content.Value : content.Value[..MaxLength] + "…";
        if (!string.Equals(content.Kind, "markdown", StringComparison.Ordinal)) return new ParsedHover(text, null);

        try
        {
            return new ParsedHover(text, Markdown.Parse(text, Pipeline));
        }
        catch (ArgumentException)
        {
            // Markdig's own refusal of input nested past its limit: shown as the text it is.
            return new ParsedHover(text, null);
        }
    }

    /// <summary>The hover's content as controls, in reading order.</summary>
    /// <param name="content">What the server answered.</param>
    /// <param name="codeFont">The code window's font, for code.</param>
    public static Control Render(MarkupContent content, FontFamily codeFont) => Render(Parse(content), codeFont);

    /// <summary>A hover already read, as controls, in reading order. On the UI thread.</summary>
    public static Control Render(ParsedHover hover, FontFamily codeFont)
    {
        if (hover.Document is not { } document) return Text(hover.Text);

        var drawing = new Drawing(hover.Text, codeFont);
        var panel = new StackPanel { Spacing = 6 };
        foreach (var block in document)
            AddBlock(panel.Children, block, drawing, 0);
        return panel;
    }

    // What every level of the drawing needs: the source, for what is shown as text, and the font for code.
    private sealed record Drawing(string Source, FontFamily CodeFont)
    {
        public string SourceOf(MarkdownObject node) =>
            node.Span.Start >= 0 && node.Span.End < Source.Length && node.Span.Length > 0
                ? Source.Substring(node.Span.Start, node.Span.Length)
                : string.Empty;
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static void AddBlock(Avalonia.Controls.Controls children, Block block, Drawing drawing, int depth)
    {
        if (depth >= MaxDepth)
        {
            if (drawing.SourceOf(block) is { Length: > 0 } source) children.Add(Text(source));
            return;
        }

        switch (block)
        {
            case HeadingBlock heading:
                children.Add(Inlines(heading.Inline, drawing, depth, FontWeight.Bold));
                break;
            case ParagraphBlock paragraph:
                children.Add(Inlines(paragraph.Inline, drawing, depth));
                break;
            case CodeBlock code:
                children.Add(Code(code, drawing.CodeFont));
                break;
            case ThematicBreakBlock:
                children.Add(Rule());
                break;
            case ListBlock list:
                children.Add(List(list, drawing, depth));
                break;
            case QuoteBlock quote:
                var quoted = new StackPanel { Spacing = 6 };
                foreach (var inner in quote) AddBlock(quoted.Children, inner, drawing, depth + 1);
                var bar = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Child = quoted };
                bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable(RuleKey));
                children.Add(bar);
                break;
            case LinkReferenceDefinitionGroup:
                // A definition is where a link's address comes from, not text; the link that uses it is drawn.
                break;
            case ContainerBlock container:
                foreach (var inner in container) AddBlock(children, inner, drawing, depth + 1);
                break;
            case LeafBlock leaf when leaf.Inline is { } inline:
                children.Add(Inlines(inline, drawing, depth));
                break;
            case LeafBlock leaf when leaf.Lines.Count > 0:
                children.Add(Text(leaf.Lines.ToString()));
                break;
        }
    }

    private static Control List(ListBlock list, Drawing drawing, int depth)
    {
        var items = new StackPanel { Spacing = 2 };
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            ? start : 1;
        foreach (var block in list)
        {
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}{list.OrderedDelimiter}" : "•",
                Margin = new Thickness(0, 0, 6, 0),
            };
            var content = new StackPanel { Spacing = 4 };
            if (block is ContainerBlock item)
                foreach (var inner in item) AddBlock(content.Children, inner, drawing, depth + 1);
            else
                AddBlock(content.Children, block, drawing, depth + 1);

            var row = new DockPanel();
            DockPanel.SetDock(marker, Avalonia.Controls.Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(content);
            items.Children.Add(row);
        }
        return items;
    }

    private static Control Rule()
    {
        var rule = new Border { Height = 1 };
        rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(RuleKey));
        return rule;
    }

    private static Control Code(CodeBlock code, FontFamily codeFont)
    {
        var text = code.Lines.ToString();
        var block = new TextBlock { FontFamily = codeFont, TextWrapping = TextWrapping.Wrap };
        var language = (code as FencedCodeBlock)?.Info;
        if (language is not null && HighlightedFences.Contains(language))
            block.Inlines = Highlighted(text);
        else
            block.Text = text;

        var box = new Border { Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(3), Child = block };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable(CodeBackgroundKey));
        return box;
    }

    /// <summary>VB code, coloured section by section by the code window's syntax highlighting.</summary>
    /// <remarks>
    /// A section can lie inside another, a string inside a statement, and the innermost one is what is drawn there,
    /// so each character takes the brush of the last section that covers it, and runs of one brush become one run.
    /// </remarks>
    internal static InlineCollection Highlighted(string code)
    {
        var inlines = new InlineCollection();
        var document = new TextDocument(code);
        using var highlighter = new DocumentHighlighter(document, SyntaxHighlightingTheme.Definition);
        for (var number = 1; number <= document.LineCount; number++)
        {
            if (number > 1) inlines.Add(new LineBreak());

            var line = document.GetLineByNumber(number);
            var brushes = new IBrush?[line.Length];
            foreach (var section in highlighter.HighlightLine(number).Sections)
            {
                if (section.Color?.Foreground?.GetBrush(null) is not { } brush) continue;
                var from = Math.Max(section.Offset - line.Offset, 0);
                var to = Math.Min(section.Offset + section.Length - line.Offset, line.Length);
                for (var i = from; i < to; i++) brushes[i] = brush;
            }

            var runStart = 0;
            for (var i = 1; i <= line.Length; i++)
            {
                if (i < line.Length && ReferenceEquals(brushes[i], brushes[runStart])) continue;
                var run = new Run(document.GetText(line.Offset + runStart, i - runStart));
                if (brushes[runStart] is { } brush) run.Foreground = brush;
                inlines.Add(run);
                runStart = i;
            }
        }
        return inlines;
    }

    private static TextBlock Inlines(ContainerInline? container, Drawing drawing, int depth, FontWeight? weight = null)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = [] };
        if (weight is { } bold) block.FontWeight = bold;
        if (container is not null) AddInlines(block.Inlines, container, drawing, depth + 1);
        return block;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container, Drawing drawing, int depth)
    {
        if (depth >= MaxDepth)
        {
            if (drawing.SourceOf(container) is { Length: > 0 } source) target.Add(new Run(source));
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = drawing.CodeFont };
                    run.Bind(TextElement.BackgroundProperty, run.GetResourceObservable(CodeBackgroundKey));
                    target.Add(run);
                    break;
                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case AutolinkInline autolink:
                    target.Add(Link(new Run(autolink.Url)));
                    break;
                case LinkInline { IsImage: true } image:
                    // The alternative text, and no request for the picture.
                    var alt = new Span();
                    AddInlines(alt.Inlines, image, drawing, depth + 1);
                    target.Add(alt);
                    break;
                case LinkInline link:
                    var label = new Span();
                    AddInlines(label.Inlines, link, drawing, depth + 1);
                    if (label.Inlines.Count == 0 && link.Url is { } url) label.Inlines.Add(new Run(url));
                    target.Add(Link(label));
                    break;
                case EmphasisInline emphasis:
                    Span styled = emphasis.DelimiterChar == '~'
                        ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(styled.Inlines, emphasis, drawing, depth + 1);
                    target.Add(styled);
                    break;
                case HtmlInline html:
                    // Not produced while HTML is disabled; were it ever, the tag is shown as text, never applied.
                    target.Add(new Run(html.Tag));
                    break;
                case ContainerInline nested:
                    AddInlines(target, nested, drawing, depth + 1);
                    break;
            }
        }
    }

    private static Avalonia.Controls.Documents.Inline Link(Avalonia.Controls.Documents.Inline label)
    {
        var link = label as Span ?? new Span { Inlines = { label } };
        link.TextDecorations = TextDecorations.Underline;
        link.Bind(TextElement.ForegroundProperty, link.GetResourceObservable(LinkKey));
        return link;
    }
}
