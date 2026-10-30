using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using HexIDE.Controls;
using HexIDE.Lsp;
using HexIDE.Lsp.Messages;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using StreamJsonRpc;

// NB: namespace deliberately avoids a `Lsp` segment — see VBLspClientTests.
namespace HexIDE.Tests.LspClient;

/// <summary>
/// Code lenses drawn in the code window and run from it (#323 built the requests), and asked for again when a server
/// says to (<c>workspace/codeLens/refresh</c>).
/// </summary>
public class CodeLensDrawingTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CodeLens Lens(int line, string title, string command) =>
        new(new Lsp.Messages.Range(new Position(line, 0), new Position(line, 3)), new Command(title, command));

    private static (TextEditor Editor, Window Window) Editor(string text)
    {
        var editor = new TextEditor { Document = new TextDocument(text), FontSize = 14 };
        var window = new Window { Content = editor, Width = 600, Height = 300 };
        window.Show();
        return (editor, window);
    }

    [AvaloniaFact]
    public void AHeadersLensIsDrawnWhenItsAttributeRunIsFolded_AndTheFoldStillCollapses()
    {
        // As the code window folds a member's attribute run by default: from the header's end, over the Attribute lines.
        var (editor, window) = Editor("Public Sub Foo()\nAttribute Foo.VB_Description = \"Does foo\"\n    Debug.Print 1\nEnd Sub");
        var folds = AvaloniaEdit.Folding.FoldingManager.Install(editor.TextArea);
        var generator = new CodeLensGenerator(_ => true, _ => { });
        editor.TextArea.TextView.ElementGenerators.Insert(0, generator);
        var header = editor.Document.GetLineByNumber(1);
        var run = editor.Document.GetLineByNumber(2);
        folds.CreateFolding(header.EndOffset, run.EndOffset).IsFolded = true;
        generator.SetLenses([Lens(0, "Run test", "test.run")]);

        var line = editor.TextArea.TextView.GetOrConstructVisualLine(header);

        line.LastDocumentLine.LineNumber.Should().Be(2, "the attribute run is folded under the header");
        var lens = line.Elements.OfType<InlineObjectElement>().Should().ContainSingle("the header's lens is on the header").Subject;
        ((StackPanel)lens.Element).Children.OfType<TextBlock>().Select(t => t.Text).Should().Equal("Run test");
        line.Elements.Should().Contain(e => e.GetType().Name == "FoldingLineElement", "and the fold is built after it, still folded");
        AvaloniaEdit.Folding.FoldingManager.Uninstall(folds);
        window.Close();
    }

    [Fact]
    public void AnEditDropsTheLensesFromItsLineDown()
    {
        var generator = new CodeLensGenerator(_ => true, _ => { });
        generator.SetLenses([Lens(0, "first", "a"), Lens(4, "fifth", "b"), Lens(9, "tenth", "c")]);

        generator.DropFrom(5).Should().BeTrue();
        generator.DropFrom(5).Should().BeFalse("nothing is left there to drop");

        generator.At(1).Select(c => c.Title).Should().Equal("first");
        generator.At(5).Should().BeEmpty();
        generator.At(10).Should().BeEmpty();
    }

    [AvaloniaFact]
    public void ALensWhoseServerStoppedSinceItWasDrawnRunsNothing()
    {
        var running = true;
        var ran = 0;
        var generator = new CodeLensGenerator(_ => running, _ => ran++);
        generator.SetLenses([Lens(0, "Run test", "test.run")]);
        var drawn = (StackPanel)generator.Build(generator.At(1), 14);
        var host = new Window { Content = drawn };
        host.Show();
        var title = drawn.Children.OfType<TextBlock>().Single();

        running = false;

        Press(host, title).Should().BeFalse("the press is the text's once nothing would run");
        ran.Should().Be(0);
        host.Close();
    }

    [AvaloniaFact]
    public void ALinesLensesFollowItsText_TakeNoPlaceInTheDocument_AndOnlyTheRunnableOnesClick()
    {
        var ran = new List<string>();
        var generator = new CodeLensGenerator(c => c.CommandName == "test.run", c => ran.Add(c.CommandName));
        var (editor, window) = Editor("Sub Test1()\nEnd Sub\nSub Other()");
        editor.TextArea.TextView.ElementGenerators.Add(generator);
        generator.SetLenses(
        [
            Lens(0, "Run test", "test.run"),
            Lens(0, "2 references", "nobody.declares.this"),
            new CodeLens(new Lsp.Messages.Range(new Position(2, 0), new Position(2, 3))),   // unresolved: nothing to run
        ]);
        var view = editor.TextArea.TextView;

        var first = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
        var lens = first.Elements.OfType<InlineObjectElement>().Should().ContainSingle().Subject;
        lens.DocumentLength.Should().Be(0, "a lens is drawn after the text and is not part of it");
        first.Elements.Last().Should().BeSameAs(lens);
        ((StackPanel)lens.Element).Children.OfType<TextBlock>().Select(t => t.Text)
            .Should().Equal("Run test", CodeLensGenerator.Separator, "2 references");
        view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(2)).Elements.OfType<InlineObjectElement>().Should().BeEmpty();
        view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(3))
            .Elements.OfType<InlineObjectElement>().Should().BeEmpty("a lens with no command has nothing to offer");
        editor.Document.Text.Should().Be("Sub Test1()\nEnd Sub\nSub Other()");
        window.Close();

        // Pressed, as a person would: only the lens whose command a server declares runs it, and the press is its own.
        var drawn = (StackPanel)generator.Build(generator.At(1), 14);
        var host = new Window { Content = drawn };
        host.Show();
        var titles = drawn.Children.OfType<TextBlock>().ToList();
        Press(host, titles[2]).Should().BeFalse("nothing would run, so the press is the text's");
        Press(host, titles[0]).Should().BeTrue();
        ran.Should().Equal("test.run");
        titles[0].Cursor.Should().NotBeNull();
        titles[2].Cursor.Should().BeNull();
        host.Close();
    }

    // A left press on the control; whether something took it.
    private static bool Press(Window root, Control target)
    {
        var args = new PointerPressedEventArgs(target, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), root,
            new Point(1, 1), 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
        target.RaiseEvent(args);
        return args.Handled;
    }

    [Fact]
    public async Task TheRouterSaysWhichCommandsWouldRun_AndForwardsAServersRefresh()
    {
        var client = Substitute.For<ILspClient>();
        client.IsRunning.Returns(true);
        client.AdvertisedCapabilities.Returns(
            JsonDocument.Parse("""{"executeCommandProvider":{"commands":["test.run"]}}""").RootElement.Clone());
        var router = new LspClientRegistry([new LanguageServerRegistration("s", "s", [".bas"], "vb6", () => client)],
            Substitute.For<ILogger<LspClientRegistry>>());
        _disposables.Add(router);
        router.CanExecuteCommand("test.run").Should().BeFalse("no server has started");

        await router.OpenDocumentAsync("file:///c:/p/M.bas", "x", TestContext.Current.CancellationToken);
        var refreshed = 0;
        router.CodeLensesRefreshRequested += (_, _) => refreshed++;
        client.CodeLensesRefreshRequested += Raise.Event();

        router.CanExecuteCommand("test.run").Should().BeTrue();
        router.CanExecuteCommand("other.command").Should().BeFalse();
        refreshed.Should().Be(1);
    }

    private sealed class RefreshingServer
    {
        private readonly TaskCompletionSource<JsonElement> _initialize = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonElement> InitializeParams => _initialize.Task;

        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public JsonElement Initialize(JsonElement p)
        {
            _initialize.TrySetResult(p.Clone());
            return JsonDocument.Parse("""{"capabilities":{"codeLensProvider":{}}}""").RootElement.Clone();
        }

        [JsonRpcMethod("initialized")]
        public void Initialized(JsonElement _) { }
    }

    [AvaloniaFact]
    public async Task TheClientDeclaresRefreshSupport_AndAServersRefreshIsAnsweredAndRaised()
    {
        var server = new RefreshingServer();
        var (clientSide, serverSide) = FullDuplexStream.CreatePair();
        var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverSide, serverSide, new SystemTextJsonFormatter()), server);
        serverRpc.StartListening();
        var transport = Substitute.For<ILspTransport>();
        transport.IsAlive.Returns(true);
        transport.ConnectAsync(Arg.Any<IJsonRpcMessageFormatter>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IJsonRpcMessageHandler?>(
                new HeaderDelimitedMessageHandler(clientSide, clientSide, ci.Arg<IJsonRpcMessageFormatter>())));
        var client = new VBLspClient(transport, Substitute.For<ILogger<VBLspClient>>(), "vb6");
        _disposables.Add(client);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CodeLensesRefreshRequested += (_, _) => raised.TrySetResult();

        await client.StartAsync(TestContext.Current.CancellationToken);
        var initialize = await server.InitializeParams.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        initialize.GetProperty("capabilities").GetProperty("workspace").GetProperty("codeLens")
            .GetProperty("refreshSupport").GetBoolean().Should().BeTrue();

        // As a conformant server sends it: no params at all.
        await serverRpc.InvokeAsync<object?>("workspace/codeLens/refresh").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        serverRpc.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }
}
