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
/// <b>Links are drawn, not followed.</b> The hover is a tooltip, which closes when the pointer leaves the word, so
/// nothing in it can be clicked; a link is coloured and underlined so it reads as one, and a <c>command:</c> link
/// runs nothing.
/// </para>
/// <para>
/// Only what hovers use is drawn as such: paragraphs, emphasis, inline code, fenced and indented code, links,
/// lists, quotes, headings and rules. Anything else shows its text. A fence named <c>vb</c>, <c>vb6</c> or
/// <c>vba</c> is coloured with the code window's own highlighting.
/// </para>
/// </remarks>
internal static class HoverMarkdown
{
    /// <summary>The fence names whose code is coloured as the code window colours it.</summary>
    internal static readonly IReadOnlySet<string> HighlightedFences =
        new HashSet<string>(["vb", "vb6", "vba"], StringComparer.OrdinalIgnoreCase);

    internal const string CodeBackgroundKey = "HoverCodeBackgroundBrush";
    internal const string LinkKey = "HoverLinkBrush";
    internal const string RuleKey = "HoverBorderBrush";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>The hover's content as controls, in reading order.</summary>
    /// <param name="content">What the server answered.</param>
    /// <param name="codeFont">The code window's font, for code.</param>
    public static Control Render(MarkupContent content, FontFamily codeFont)
    {
        if (!string.Equals(content.Kind, "markdown", StringComparison.Ordinal))
            return Text(content.Value);

        var panel = new StackPanel { Spacing = 6 };
        foreach (var block in Markdown.Parse(content.Value, Pipeline))
            AddBlock(panel.Children, block, codeFont);
        return panel;
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static void AddBlock(Avalonia.Controls.Controls children, Block block, FontFamily codeFont)
    {
        switch (block)
        {
            case HeadingBlock heading:
                children.Add(Inlines(heading.Inline, codeFont, FontWeight.Bold));
                break;
            case ParagraphBlock paragraph:
                children.Add(Inlines(paragraph.Inline, codeFont));
                break;
            case CodeBlock code:
                children.Add(Code(code, codeFont));
                break;
            case ThematicBreakBlock:
                children.Add(Rule());
                break;
            case ListBlock list:
                children.Add(List(list, codeFont));
                break;
            case QuoteBlock quote:
                var quoted = new StackPanel { Spacing = 6 };
                foreach (var inner in quote) AddBlock(quoted.Children, inner, codeFont);
                var bar = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Child = quoted };
                bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable(RuleKey));
                children.Add(bar);
                break;
            case LinkReferenceDefinitionGroup:
                // A definition is where a link's address comes from, not text; the link that uses it is drawn.
                break;
            case ContainerBlock container:
                foreach (var inner in container) AddBlock(children, inner, codeFont);
                break;
            case LeafBlock leaf when leaf.Inline is { } inline:
                children.Add(Inlines(inline, codeFont));
                break;
            case LeafBlock leaf when leaf.Lines.Count > 0:
                children.Add(Text(leaf.Lines.ToString()));
                break;
        }
    }

    private static Control List(ListBlock list, FontFamily codeFont)
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
                foreach (var inner in item) AddBlock(content.Children, inner, codeFont);
            else
                AddBlock(content.Children, block, codeFont);

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

    /// <summary>VB code, coloured section by section as the code window colours it.</summary>
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

    private static TextBlock Inlines(ContainerInline? container, FontFamily codeFont, FontWeight? weight = null)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = [] };
        if (weight is { } bold) block.FontWeight = bold;
        if (container is not null) AddInlines(block.Inlines, container, codeFont);
        return block;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container, FontFamily codeFont)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = codeFont };
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
                    AddInlines(alt.Inlines, image, codeFont);
                    target.Add(alt);
                    break;
                case LinkInline link:
                    var label = new Span();
                    AddInlines(label.Inlines, link, codeFont);
                    if (label.Inlines.Count == 0 && link.Url is { } url) label.Inlines.Add(new Run(url));
                    target.Add(Link(label));
                    break;
                case EmphasisInline emphasis:
                    Span styled = emphasis.DelimiterChar == '~'
                        ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(styled.Inlines, emphasis, codeFont);
                    target.Add(styled);
                    break;
                case HtmlInline html:
                    // Not produced while HTML is disabled; were it ever, the tag is shown as text, never applied.
                    target.Add(new Run(html.Tag));
                    break;
                case ContainerInline nested:
                    AddInlines(target, nested, codeFont);
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
