using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using HexIDE.Lsp.Messages;

namespace HexIDE.Controls;

/// <summary>
/// The code window's hover: the diagnostics under the pointer, then the server's quick info, on one card.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tooltip, coloured by the theme.</b> The card is the editor's <see cref="ToolTip"/> itself, so it opens and
/// closes as the hover always has and the automation hover tool reads its text blocks. Its colours are the theme's
/// <c>Hover*</c> keys, so a pack can recolour it; the other tooltips in the IDE are untouched.
/// </para>
/// </remarks>
internal static class HoverCard
{
    internal const string BackgroundKey = "HoverBackgroundBrush";
    internal const string ForegroundKey = "HoverForegroundBrush";
    internal const string BorderKey = "HoverBorderBrush";

    /// <summary>The widest a card grows before its text wraps.</summary>
    internal const double MaxWidth = 560;

    /// <summary>
    /// The tallest a card grows. What is longer is cut, with a "…" line to say so: the card is a tooltip, which closes as
    /// the pointer moves to it and takes no wheel, so there is nothing to scroll with.
    /// </summary>
    internal const double MaxHeight = 360;

    /// <summary>The card for these diagnostics and this quick info, or null when there is nothing to show.</summary>
    /// <param name="markers">The diagnostics under the pointer, in the order they are drawn.</param>
    /// <param name="quickInfo">The server's hover, or null.</param>
    /// <param name="codeFont">The code window's font, for code in the quick info.</param>
    public static ToolTip? Build(IEnumerable<LspMarker> markers, MarkupContent? quickInfo, FontFamily codeFont) =>
        Build(markers, quickInfo is { Value.Length: > 0 } info ? HoverMarkdown.Parse(info) : null, codeFont);

    /// <summary>The card, from a quick info already read off the UI thread (<see cref="HoverMarkdown.Parse"/>).</summary>
    public static ToolTip? Build(IEnumerable<LspMarker> markers, HoverMarkdown.ParsedHover? quickInfo, FontFamily codeFont)
    {
        var sections = new StackPanel { Spacing = 8, MaxWidth = MaxWidth };
        foreach (var marker in markers)
        {
            sections.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(marker.Source) ? marker.Message : $"{marker.Source}: {marker.Message}",
                TextWrapping = TextWrapping.Wrap,
            });

            // The places the server says it concerns, under its message (hexide-io/HexIDE#453). Text only: the card is a
            // tooltip, so nothing in it can be clicked to go there.
            foreach (var related in marker.Related ?? [])
                sections.Children.Add(new TextBlock { Text = related, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, -4, 0, 0) });
        }

        if (quickInfo is { Text.Length: > 0 } info)
        {
            if (sections.Children.Count > 0)
            {
                var rule = new Border { Height = 1 };
                rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(BorderKey));
                sections.Children.Add(rule);
            }
            sections.Children.Add(HoverMarkdown.Render(info, codeFont));
        }

        if (sections.Children.Count == 0) return null;

        // However much a server says, the card stays a card: what does not fit is cut, and the cut is shown.
        var tip = new ToolTip { Content = new CutAtHeight(sections, MaxHeight), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6) };
        tip.Bind(TemplatedControl.BackgroundProperty, tip.GetResourceObservable(BackgroundKey));
        tip.Bind(TemplatedControl.ForegroundProperty, tip.GetResourceObservable(ForegroundKey));
        tip.Bind(TemplatedControl.BorderBrushProperty, tip.GetResourceObservable(BorderKey));
        return tip;
    }
}

/// <summary>
/// Shows its content up to a height, and below it a "…" line when the content is taller and has been cut.
/// </summary>
/// <remarks>
/// The marker's text is empty while nothing is cut, so a reader of the card's text blocks, the automation hover tool
/// among them, sees the content and nothing else.
/// </remarks>
internal sealed class CutAtHeight : Panel
{
    internal const string Marker = "…";

    private readonly Border _clip;
    private readonly TextBlock _marker = new();
    private readonly double _maxHeight;

    public CutAtHeight(Control content, double maxHeight)
    {
        _maxHeight = maxHeight;
        _clip = new Border { ClipToBounds = true, Child = content };
        Children.Add(_clip);
        Children.Add(_marker);
    }

    /// <summary>Whether the content was taller than the height, and so is cut.</summary>
    public bool IsCut { get; private set; }

    private double MarkerHeight => _marker.FontSize * 1.4;

    protected override Size MeasureOverride(Size availableSize)
    {
        _clip.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var content = _clip.DesiredSize;
        IsCut = content.Height > _maxHeight;
        var marker = IsCut ? Marker : string.Empty;
        if (_marker.Text != marker) _marker.Text = marker;
        _marker.Measure(new Size(availableSize.Width, MarkerHeight));
        return IsCut ? new Size(content.Width, _maxHeight) : content;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!IsCut)
        {
            _clip.Arrange(new Rect(finalSize));
            _marker.Arrange(new Rect(0, finalSize.Height, finalSize.Width, 0));
            return finalSize;
        }

        var shown = Math.Max(0, finalSize.Height - MarkerHeight);
        _clip.Arrange(new Rect(0, 0, finalSize.Width, shown));
        _marker.Arrange(new Rect(0, shown, finalSize.Width, MarkerHeight));
        return finalSize;
    }
}
