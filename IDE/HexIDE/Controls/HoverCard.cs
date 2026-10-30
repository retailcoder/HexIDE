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

    /// <summary>The card for these diagnostics and this quick info, or null when there is nothing to show.</summary>
    /// <param name="markers">The diagnostics under the pointer, in the order they are drawn.</param>
    /// <param name="quickInfo">The server's hover, or null.</param>
    /// <param name="codeFont">The code window's font, for code in the quick info.</param>
    public static ToolTip? Build(IEnumerable<LspMarker> markers, MarkupContent? quickInfo, FontFamily codeFont)
    {
        var sections = new StackPanel { Spacing = 8, MaxWidth = MaxWidth };
        foreach (var marker in markers)
        {
            sections.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(marker.Source) ? marker.Message : $"{marker.Source}: {marker.Message}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (quickInfo is { Value.Length: > 0 } info)
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

        var tip = new ToolTip { Content = sections, BorderThickness = new Thickness(1), Padding = new Thickness(8, 6) };
        tip.Bind(TemplatedControl.BackgroundProperty, tip.GetResourceObservable(BackgroundKey));
        tip.Bind(TemplatedControl.ForegroundProperty, tip.GetResourceObservable(ForegroundKey));
        tip.Bind(TemplatedControl.BorderBrushProperty, tip.GetResourceObservable(BorderKey));
        return tip;
    }
}
