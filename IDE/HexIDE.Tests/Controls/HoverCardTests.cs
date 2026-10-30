using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using HexIDE.Automation;
using HexIDE.Controls;
using HexIDE.Lsp.Messages;
using HexIDE.Themes;

namespace HexIDE.Tests.Controls;

/// <summary>
/// The code window's hover card: Markdown hovers drawn as formatted text, plain ones as they came, and the card's
/// colours from the theme.
/// </summary>
public class HoverCardTests
{
    private static readonly FontFamily Code = new("Courier New");

    private static MarkupContent Markdown(string text) => new("markdown", text);

    private static IEnumerable<TextBlock> Blocks(Control control) =>
        control.GetLogicalDescendants().Prepend(control).OfType<TextBlock>();

    private static string TextOf(Control control) =>
        string.Join("\n", Blocks(control).Select(b => b.Text ?? b.Inlines?.Text).Where(t => !string.IsNullOrEmpty(t)));

    private static IEnumerable<Inline> InlinesOf(Control control) =>
        Blocks(control).SelectMany(b => b.Inlines is { } inlines ? Flatten(inlines) : []);

    private static IEnumerable<Inline> Flatten(InlineCollection inlines) =>
        inlines.SelectMany(i => i is Span span ? Flatten(span.Inlines).Prepend(i) : [i]);

    [AvaloniaFact]
    public void Markdown_is_drawn_as_formatted_text_not_as_its_notation()
    {
        var shown = HoverMarkdown.Render(Markdown("A **bold** and *slanted* `word`."), Code);

        TextOf(shown).Should().Be("A bold and slanted word.");
        InlinesOf(shown).OfType<Bold>().Should().ContainSingle().Which.Inlines.Text.Should().Be("bold");
        InlinesOf(shown).OfType<Italic>().Should().ContainSingle().Which.Inlines.Text.Should().Be("slanted");
        InlinesOf(shown).OfType<Run>().Should().Contain(r => r.Text == "word" && r.FontFamily == Code);
    }

    [AvaloniaFact]
    public void Plain_text_is_shown_as_it_came_and_never_parsed()
    {
        var shown = HoverMarkdown.Render(new MarkupContent("plaintext", "x = *y* * 2  # not a heading"), Code);

        TextOf(shown).Should().Be("x = *y* * 2  # not a heading");
        InlinesOf(shown).Should().NotContain(i => i is Bold || i is Italic);
    }

    [AvaloniaFact]
    public void Raw_HTML_is_shown_as_text_and_never_applied()
    {
        var shown = HoverMarkdown.Render(Markdown("Before <b>tag</b> after"), Code);

        TextOf(shown).Should().Be("Before <b>tag</b> after");
        InlinesOf(shown).Should().NotContain(i => i is Bold);
    }

    [AvaloniaFact]
    public void An_image_is_its_alternative_text_and_is_never_fetched()
    {
        var shown = HoverMarkdown.Render(Markdown("See ![the diagram](https://example.invalid/d.png) here"), Code);

        TextOf(shown).Should().Be("See the diagram here");
        shown.GetLogicalDescendants().OfType<Image>().Should().BeEmpty();
    }

    [AvaloniaFact]
    public void A_link_is_drawn_as_one_with_its_label_and_runs_nothing()
    {
        var shown = HoverMarkdown.Render(Markdown("Read [the page](https://example.invalid/x) or [run](command:x.run)."), Code);

        TextOf(shown).Should().Be("Read the page or run.");
        InlinesOf(shown).OfType<Span>().Where(s => s.TextDecorations == TextDecorations.Underline)
            .Select(s => s.Inlines.Text).Should().Equal("the page", "run");
        shown.GetLogicalDescendants().OfType<Button>().Should().BeEmpty("nothing in a tooltip can be clicked");
    }

    [AvaloniaFact]
    public void A_VB_fence_is_coloured_as_the_code_window_colours_it()
    {
        SyntaxHighlightingTheme.EnsureRegistered();
        static Color? ColourOf(Run run) => (run.Foreground as ISolidColorBrush)?.Color;
        var keyword = ColourOf(HoverMarkdown.Highlighted("Dim").OfType<Run>().Single());
        keyword.Should().NotBeNull("the code window's definition colours a keyword");

        var shown = HoverMarkdown.Render(Markdown("```vba\nPublic Function Total() As Long\n```"), Code);

        var runs = InlinesOf(shown).OfType<Run>().ToList();
        string.Concat(runs.Select(r => r.Text)).Should().Be("Public Function Total() As Long");
        runs.Where(r => r.Text is "Public" or "Function").Select(ColourOf).Should().AllBeEquivalentTo(keyword);
        runs.Single(r => r.Text == " Total() ").IsSet(TextElement.ForegroundProperty).Should()
            .BeFalse("a name takes the card's own text colour");
        Blocks(shown).Single().FontFamily.Should().Be(Code);
    }

    [AvaloniaFact]
    public void A_fence_in_another_language_is_plain_code()
    {
        var shown = HoverMarkdown.Render(Markdown("```python\nimport os\n```"), Code);

        var block = Blocks(shown).Single();
        block.Text.Should().Be("import os");
        block.FontFamily.Should().Be(Code);
    }

    [AvaloniaFact]
    public void Lists_headings_and_rules_keep_their_shape()
    {
        var shown = HoverMarkdown.Render(Markdown("# Title\n\n1. one\n2. two\n\n---\n\n- dot"), Code);

        TextOf(shown).Should().Be("Title\n1.\none\n2.\ntwo\n•\ndot");
        Blocks(shown).First().FontWeight.Should().Be(FontWeight.Bold);
    }

    [AvaloniaFact]
    public void The_card_is_the_diagnostics_then_the_quick_info_and_nothing_when_both_are_empty()
    {
        var markers = new[] { new LspMarker(0, 3, true, "Variable not defined", "RDCore"), new LspMarker(0, 3, false, "unused") };

        var card = HoverCard.Build(markers, Markdown("```vba\nDim x\n```"), Code);

        card.Should().NotBeNull();
        UiAutomationDriver.TipText(card).Should().Be("RDCore: Variable not defined / unused / Dim x",
            "the automation hover tool reads the card as it always read the tip");
        HoverCard.Build([], null, Code).Should().BeNull();
        HoverCard.Build([], new MarkupContent("markdown", ""), Code).Should().BeNull();
    }

    [AvaloniaFact]
    public void The_card_takes_its_colours_from_the_theme()
    {
        var card = HoverCard.Build([], Markdown("text with `code` and [a link](https://example.invalid)"), Code)!;
        var resources = new Avalonia.Controls.ResourceDictionary
        {
            [HoverCard.BackgroundKey] = Brushes.Navy,
            [HoverCard.ForegroundKey] = Brushes.Ivory,
            [HoverCard.BorderKey] = Brushes.Gold,
            [HoverMarkdown.CodeBackgroundKey] = Brushes.Black,
            [HoverMarkdown.LinkKey] = Brushes.Aqua,
        };
        // Hosted directly, which puts it where the resources are, as opening it over the editor does.
        var window = new Window { Resources = resources, Content = card };
        window.Show();

        card.Background.Should().BeSameAs(Brushes.Navy);
        card.Foreground.Should().BeSameAs(Brushes.Ivory);
        card.BorderBrush.Should().BeSameAs(Brushes.Gold);
        InlinesOf(card).OfType<Run>().Single(r => r.Text == "code").Background.Should().BeSameAs(Brushes.Black);
        InlinesOf(card).OfType<Span>().Single(s => s.TextDecorations == TextDecorations.Underline).Foreground
            .Should().BeSameAs(Brushes.Aqua);
        window.Close();
    }
}
