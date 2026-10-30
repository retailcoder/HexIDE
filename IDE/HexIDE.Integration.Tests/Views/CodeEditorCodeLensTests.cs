using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using HexIDE.Bookmarks;
using HexIDE.Controls;
using HexIDE.Events;
using HexIDE.Forms.ViewModels;
using HexIDE.Forms.Views;
using HexIDE.IDE;
using HexIDE.Localization;
using HexIDE.Lsp;
using HexIDE.Lsp.Messages;
using HexIDE.Projects;
using HexIDE.Runtime.Components;
using HexIDE.Runtime.ProjectElements;

namespace HexIDE.Integration.Tests.Views;

/// <summary>
/// Code lenses in the code window itself: asked for on attach, drawn on a member whose attribute run is folded, kept from
/// a late answer, refreshed alone, dropped below an edit, and cleared by a failed request.
/// </summary>
/// <remarks>
/// Every wait here is for the request it depends on, signalled by the substitute that answers it, never for a fixed time:
/// the settle the view applies is its own business, and a test that slept past it would pass or fail with the machine.
/// </remarks>
public class CodeEditorCodeLensTests : IDisposable
{
    private readonly ILspClient _lspClient = Substitute.For<ILspClient>();
    private readonly List<Window> _windows = [];
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<CodeLens[]>> _lensAnswers = [];
    private Func<int, Task<CodeLens[]>> _answer = _ => Task.FromResult<CodeLens[]>([]);
    private int _lensRequests;
    private int _foldRequests;
    private TaskCompletionSource _lensAsked = NewSignal();
    private TaskCompletionSource _foldAsked = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    public CodeEditorCodeLensTests()
    {
        _lspClient.IsRunning.Returns(true);
        _lspClient.CanExecuteCommand(Arg.Any<string>()).Returns(true);
        _lspClient.RequestDocumentSymbolsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<DocumentSymbol>());
        _lspClient.RequestFoldingRangesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            lock (_gate) { _foldRequests++; _foldAsked.TrySetResult(); }
            return Task.FromResult<FoldingRange[]>([]);
        });
        _lspClient.RequestCodeLensesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            int number;
            lock (_gate) { number = ++_lensRequests; _lensAsked.TrySetResult(); }
            return _answer(number);
        });
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        GC.SuppressFinalize(this);
    }

    private static CodeLens Lens(int line, string title) =>
        new(new Lsp.Messages.Range(new Position(line, 0), new Position(line, 3)), new Command(title, "test." + title));

    private async Task NextLensRequest()
    {
        Task asked;
        lock (_gate) asked = _lensAsked.Task;
        await asked.WaitAsync(TimeSpan.FromSeconds(10), Token);
        lock (_gate) _lensAsked = NewSignal();
        Dispatcher.UIThread.RunJobs();
    }

    private async Task NextFoldRequest()
    {
        Task asked;
        lock (_gate) asked = _foldAsked.Task;
        await asked.WaitAsync(TimeSpan.FromSeconds(10), Token);
        lock (_gate) _foldAsked = NewSignal();
        Dispatcher.UIThread.RunJobs();
    }

    private static CodeLensGenerator LensesOf(CodeEditorView view) =>
        view.TextEditor.TextArea.TextView.ElementGenerators.OfType<CodeLensGenerator>().Single();

    [AvaloniaFact]
    public void AServerAlreadyUpIsAskedForLensesAsTheWindowAttaches()
    {
        using var vm = MakeViewModel();
        vm.Initialize(AModule());

        Show(vm);

        // Before any settle has had time to run: the attach itself asks, as it asks for tokens and folds (#446).
        lock (_gate) _lensRequests.Should().BeGreaterThan(0);
    }

    [AvaloniaFact]
    public async Task AMembersHeaderShowsItsLens_ItsAttributeRunFoldedUnderItByDefault()
    {
        // Line 3 of the window's text, under the file's own Attribute VB_Name line: the member's header.
        _answer = _ => Task.FromResult<CodeLens[]>([Lens(2, "Run")]);
        using var vm = MakeViewModel();
        vm.Initialize(AModule());

        var view = Show(vm);
        await NextLensRequest();
        Dispatcher.UIThread.RunJobs();

        var editor = view.TextEditor;
        var header = editor.Document.GetLineByNumber(3);
        editor.Document.GetText(header).Should().Be("Public Sub Foo()");
        var line = editor.TextArea.TextView.GetOrConstructVisualLine(header);
        line.LastDocumentLine.LineNumber.Should().BeGreaterThan(3, "the member's attribute run is folded under its header");
        line.Elements.OfType<InlineObjectElement>().Should().ContainSingle("the header's lens is drawn on the header")
            .Which.Element.Should().BeOfType<StackPanel>().Which.Children.OfType<TextBlock>().Select(t => t.Text).Should().Equal("Run");
    }

    [AvaloniaFact]
    public async Task ALateAnswerIsNotDrawnOverANewerOne_AndARefreshAsksForTheLensesAlone()
    {
        _answer = number =>
        {
            if (number != 1) return Task.FromResult<CodeLens[]>([Lens(4, "newer")]);
            var late = new TaskCompletionSource<CodeLens[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _lensAnswers.Add(late);
            return late.Task;
        };
        using var vm = MakeViewModel();
        vm.Initialize(AModule());
        var view = Show(vm);
        await NextLensRequest();

        // An edit's settle asks for everything, so it is let through first: what follows is the refresh's alone.
        vm.Document.Insert(vm.Document.TextLength, "' changed\r\n");
        await NextFoldRequest();
        await NextLensRequest();
        int folds;
        lock (_gate) folds = _foldRequests;

        _lspClient.CodeLensesRefreshRequested += Raise.Event();
        Dispatcher.UIThread.RunJobs();
        await NextLensRequest();
        lock (_gate) _foldRequests.Should().Be(folds, "the server asked about its lenses, not about folds");

        TaskCompletionSource<CodeLens[]> first;
        lock (_gate) first = _lensAnswers.Single();
        first.SetResult([Lens(4, "older")]);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        LensesOf(view).At(5).Select(c => c.Title).Should().Equal(["newer"], "the attach's answer arrived last, and is older");
    }

    [AvaloniaFact]
    public async Task AnEditDropsTheLensesBelowIt_AndAFailedRequestClearsThem()
    {
        _answer = _ => Task.FromResult<CodeLens[]>([Lens(1, "above"), Lens(4, "below")]);
        using var vm = MakeViewModel();
        vm.Initialize(AModule());
        var view = Show(vm);
        await NextLensRequest();
        Dispatcher.UIThread.RunJobs();
        LensesOf(view).At(5).Should().NotBeEmpty();

        vm.Document.Insert(vm.Document.GetLineByNumber(4).Offset, "    ' a note\r\n");

        LensesOf(view).At(2).Select(c => c.Title).Should().Equal(["above"]);
        LensesOf(view).At(5).Should().BeEmpty("the edit moved it, so it waits for the next answer");

        _answer = _ => Task.FromException<CodeLens[]>(new InvalidOperationException("the server fell over"));
        _lspClient.CodeLensesRefreshRequested += Raise.Event();
        Dispatcher.UIThread.RunJobs();
        await NextLensRequest();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        LensesOf(view).At(2).Should().BeEmpty("a request that failed offers nothing, so nothing old is left to click");
    }

    private CodeEditorViewModel MakeViewModel()
    {
        var eventBus = Substitute.For<IEventBus>();
        eventBus.Subscribe<CreateOrNavigateToSubEvent>(Arg.Any<Action<CreateOrNavigateToSubEvent>>()).Returns(Substitute.For<IDisposable>());
        eventBus.Subscribe<ApplyAllUnsavedChangesEvent>(Arg.Any<Action<ApplyAllUnsavedChangesEvent>>()).Returns(Substitute.For<IDisposable>());
        eventBus.Subscribe<FormUnloadedEvent>(Arg.Any<Action<FormUnloadedEvent>>()).Returns(Substitute.For<IDisposable>());
        eventBus.Subscribe<DocumentSavedEvent>(Arg.Any<Action<DocumentSavedEvent>>()).Returns(Substitute.For<IDisposable>());

        var localization = Substitute.For<ILocalizationService>();
        localization.GetString("Str.Document.CodeSuffix").Returns("Code");
        var settings = Substitute.For<ISettingsService>();
        settings.TabWidth.Returns(4);

        return new CodeEditorViewModel(
            Substitute.For<IWindowManager>(),
            Substitute.For<IEditorService>(),
            Substitute.For<IProjectService>(),
            eventBus,
            _lspClient,
            settings,
            Substitute.For<IStatusBarService>(),
            Substitute.For<IBookmarkService>(),
            Substitute.For<HexIDE.Debugging.IBreakpointService>(),
            Substitute.For<HexIDE.Runtime.Debugging.IDebugController>(),
            Substitute.For<HexIDE.Debugging.IRunScope>(),
            localization);
    }

    // A member with an Attribute line, which the code window folds under its header by default.
    private static ModuleDefinition AModule()
    {
        var project = new ProjectDefinition(VBProjectType.EXE, "P");
        var module = new ModuleDefinition(project, "Module1", ModuleKind.StandardModule);
        module.UpdateCode("Option Explicit\r\nPublic Sub Foo()\r\nAttribute Foo.VB_Description = \"Does foo\"\r\n    Debug.Print 1\r\nEnd Sub\r\n");
        return module;
    }

    private CodeEditorView Show(CodeEditorViewModel vm)
    {
        var window = new Window { Width = 1200, Height = 800 };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = new CodeEditorView { DataContext = vm };
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
        return view;
    }
}
