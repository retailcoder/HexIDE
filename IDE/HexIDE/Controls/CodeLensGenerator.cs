using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using AvaloniaEdit.Rendering;
using HexIDE.Lsp.Messages;

namespace HexIDE.Controls;

/// <summary>
/// Draws a language server's code lenses at the end of the line each starts on, and runs one when it is clicked
/// (hexide-io/HexIDE#323 built the requests; this is what shows them).
/// </summary>
/// <remarks>
/// <para>
/// <b>After the line, not above it.</b> The text view has no line of its own to put between two document lines, and
/// faking one would change every line's height and position. So a line's lenses follow its text, dimmed, in the
/// read-only colour, and take no place in the document: the caret, selection and edits never meet them.
/// </para>
/// <para>
/// <b>Clickable only when something would run.</b> A command goes to the server that declared it, and to no other, so
/// a lens whose command no started server declares is drawn as text. Clicking it would otherwise do nothing, which
/// reads as a fault.
/// </para>
/// </remarks>
/// <param name="canRun">Whether a started server declares the command.</param>
/// <param name="run">Runs the command, on the server that declared it.</param>
internal sealed class CodeLensGenerator(Func<Command, bool> canRun, Action<Command> run) : VisualLineElementGenerator
{
    internal const string Separator = " | ";

    private IReadOnlyDictionary<int, IReadOnlyList<Command>> _byLine = new Dictionary<int, IReadOnlyList<Command>>();

    /// <summary>The lenses to draw, by the line each starts on. One without a command is not drawn: there is nothing to run.</summary>
    public void SetLenses(IEnumerable<CodeLens> lenses) =>
        _byLine = lenses
            .Where(lens => lens.Command is not null)
            .GroupBy(lens => lens.Range.Start.Line + 1)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Command>)group.Select(lens => lens.Command!).ToList());

    /// <summary>The commands drawn after a line, 1-based as the editor counts lines.</summary>
    internal IReadOnlyList<Command> At(int lineNumber) => _byLine.TryGetValue(lineNumber, out var commands) ? commands : [];

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        return _byLine.ContainsKey(line.LineNumber) && startOffset <= line.EndOffset ? line.EndOffset : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        if (offset != line.EndOffset || !_byLine.TryGetValue(line.LineNumber, out var commands)) return null;

        // Of no length in the document: it is drawn after the line's text and is not part of it.
        return new InlineObjectElement(0, Build(commands, CurrentContext.GlobalTextRunProperties.FontRenderingEmSize));
    }

    /// <summary>One line's lenses, as drawn.</summary>
    internal Control Build(IReadOnlyList<Command> commands, double fontSize)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(fontSize * 2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Bind(TextBlock.ForegroundProperty, panel.GetResourceObservable("ReadOnlyTextBrush"));

        for (var i = 0; i < commands.Count; i++)
        {
            if (i > 0) panel.Children.Add(new TextBlock { Text = Separator, FontSize = fontSize * 0.9 });

            var command = commands[i];
            var title = new TextBlock { Text = command.Title, FontSize = fontSize * 0.9 };
            if (canRun(command))
            {
                title.Cursor = new Cursor(StandardCursorType.Hand);
                title.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(title).Properties.IsLeftButtonPressed) return;
                    // Handled, so the press is the lens's and not the start of a selection in the text behind it.
                    e.Handled = true;
                    run(command);
                };
            }
            panel.Children.Add(title);
        }
        return panel;
    }
}
