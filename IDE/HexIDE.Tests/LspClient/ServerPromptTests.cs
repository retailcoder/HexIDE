using System.Text.Json;
using HexIDE.Forms.ViewModels;
using HexIDE.IDE;
using HexIDE.Localization;
using HexIDE.Lsp;
using HexIDE.Lsp.Messages;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using StreamJsonRpc;

// NB: namespace deliberately avoids a `Lsp` segment — see VBLspClientTests.
namespace HexIDE.Tests.LspClient;

/// <summary>
/// A server's question (<c>window/showMessageRequest</c>): declared, raised without holding the connection, answered with
/// the server's own item, withdrawn when the server withdraws it, and shown one at a time.
/// </summary>
public class ServerPromptTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class AskingServer
    {
        private readonly TaskCompletionSource<JsonElement> _initialize = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonElement> InitializeParams => _initialize.Task;

        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public JsonElement Initialize(JsonElement p)
        {
            _initialize.TrySetResult(p.Clone());
            return JsonDocument.Parse("""{"capabilities":{"hoverProvider":true}}""").RootElement.Clone();
        }

        [JsonRpcMethod("initialized")]
        public void Initialized(JsonElement _) { }

        [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
        public JsonElement Hover(JsonElement _) =>
            JsonDocument.Parse("""{"contents":{"kind":"plaintext","value":"still answering"}}""").RootElement.Clone();
    }

    private async Task<(VBLspClient Client, JsonRpc Server, JsonElement Initialize)> ConnectedAsync()
    {
        var server = new AskingServer();
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
        await client.StartAsync(Token);
        var initialize = await server.InitializeParams.WaitAsync(TimeSpan.FromSeconds(10), Token);
        return (client, serverRpc, initialize);
    }

    private static object Ask(params object[] actions) => new { type = 3, message = "Import the VB6 project?", actions };

    [AvaloniaFact]
    public async Task TheClientDeclaresIt_AndAnswersWithTheChosenItemAsTheServerSentIt()
    {
        var (client, server, initialize) = await ConnectedAsync();
        initialize.GetProperty("capabilities").GetProperty("window").GetProperty("showMessage")
            .GetProperty("messageActionItem").GetProperty("additionalPropertiesSupport").GetBoolean().Should().BeTrue();
        ServerMessageRequest? asked = null;
        client.MessageRequested += (_, request) =>
        {
            asked = request;
            request.Respond(Task.FromResult<ServerMessageAction?>(request.Actions[1]));
        };

        var answer = await server.InvokeWithParameterObjectAsync<JsonElement>("window/showMessageRequest",
            Ask(new { title = "Not now" }, new { title = "Import", projectId = 7 }), Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        asked!.Message.Should().Be("Import the VB6 project?");
        asked.Type.Should().Be(LspMessageType.Info);
        asked.Actions.Select(a => a.Title).Should().Equal("Not now", "Import");
        answer.GetProperty("title").GetString().Should().Be("Import");
        answer.GetProperty("projectId").GetInt32().Should().Be(7, "the server's own members go back with its item");
        server.Dispose();
    }

    [AvaloniaFact]
    public async Task WithNobodyToAsk_NothingIsChosen()
    {
        var (_, server, _) = await ConnectedAsync();

        var answer = await server.InvokeWithParameterObjectAsync<JsonElement?>("window/showMessageRequest",
            Ask(new { title = "OK" }), Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        answer.Should().BeNull();
        server.Dispose();
    }

    [AvaloniaFact]
    public async Task AnUnansweredQuestionHoldsNothingElseOnTheConnection_AndIsWithdrawnWhenTheServerWithdrawsIt()
    {
        var (client, server, _) = await ConnectedAsync();
        var asked = new TaskCompletionSource<ServerMessageRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<ServerMessageAction?>();
        client.MessageRequested += (_, request) =>
        {
            request.Respond(never.Task);
            asked.TrySetResult(request);
        };
        using var withdraw = new CancellationTokenSource();

        var question = server.InvokeWithParameterObjectAsync<JsonElement?>("window/showMessageRequest", Ask(new { title = "OK" }), withdraw.Token);
        var request = await asked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // Both directions still flow while the question waits.
        (await client.RequestHoverAsync("untitled:Module1.bas", new Position(0, 0), Token).WaitAsync(TimeSpan.FromSeconds(10), Token))!
            .Contents.Value.Should().Be("still answering");

        var takenAway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var told = request.Cancellation.Register(() => takenAway.TrySetResult());
        withdraw.Cancel();
        await takenAway.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        request.Cancellation.IsCancellationRequested.Should().BeTrue("whoever shows it is told to take it away");
        var withdrawn = () => question;
        await withdrawn.Should().ThrowAsync<OperationCanceledException>();
        server.Dispose();
    }

    [Fact]
    public async Task TheRouterPassesTheSameQuestionOn_NamedForItsServer()
    {
        var client = Substitute.For<ILspClient>();
        client.IsRunning.Returns(true);
        var router = new LspClientRegistry([new LanguageServerRegistration("rd", "RDCore", [".bas"], "vba", () => client)],
            Substitute.For<ILogger<LspClientRegistry>>());
        _disposables.Add(router);
        await router.OpenDocumentAsync("file:///c:/p/M.bas", "x", Token);
        ServerMessageRequest? forwarded = null;
        router.MessageRequested += (_, r) => forwarded = r;
        var request = new ServerMessageRequest(LspMessageType.Warning, "?", [], NeverWithdrawn);

        client.MessageRequested += Raise.Event<EventHandler<ServerMessageRequest>>(client, request);

        forwarded.Should().BeSameAs(request, "an answer given to it has to reach the server that asked");
        forwarded!.ServerDisplayName.Should().Be("RDCore");
    }

    // ── The presenter ──────────────────────────────────────────────────────────────────────────────

    private static ILocalizationService Localization()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization.GetString("Str.Dialog.ServerPrompt.Title").Returns("{0} asks");
        localization.GetString("Str.StatusBar.LanguageServer").Returns("Language server");
        return localization;
    }

    // A window manager whose windows stay open until the test acts in them, as a person would.
    private sealed class Windows
    {
        public readonly IWindowManager Manager = Substitute.For<IWindowManager>();
        public readonly List<ServerPromptDialogViewModel> Shown = [];
        private readonly SemaphoreSlim _opened = new(0);

        public Windows()
        {
            Manager.ShowWindow(Arg.Any<IDialog>()).Returns(ci =>
            {
                var dialog = (ServerPromptDialogViewModel)ci.Arg<IDialog>();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                dialog.CloseRequested += _ => closed.TrySetResult();
                lock (Shown) Shown.Add(dialog);
                _opened.Release();
                return closed.Task;
            });
        }

        public async Task<ServerPromptDialogViewModel> NextShownAsync()
        {
            (await _opened.WaitAsync(TimeSpan.FromSeconds(10), Token)).Should().BeTrue("a question was shown");
            lock (Shown) return Shown[^1];
        }
    }

    private static ServerPromptPresenter Presenter(ILspClient client, Windows windows) =>
        new(client, windows.Manager, Localization(), work => work(), action => action());

    /// <summary>The token of a question its server never withdraws.</summary>
    private static readonly CancellationToken NeverWithdrawn = new CancellationTokenSource().Token;

    private static ServerMessageRequest Question(string message, params string[] titles) => Withdrawable(message, NeverWithdrawn, titles);

    private static ServerMessageRequest Withdrawable(string message, CancellationToken withdrawal, params string[] titles) =>
        new(LspMessageType.Info, message,
            [.. titles.Select(t => new ServerMessageAction(t, JsonDocument.Parse($$"""{"title":"{{t}}"}""").RootElement.Clone()))],
            withdrawal) { ServerDisplayName = "RDCore" };

    [Fact]
    public async Task ThePressedChoiceIsTheAnswer_AndQuestionsAreShownOneAtATime()
    {
        var client = Substitute.For<ILspClient>();
        var windows = new Windows();
        using var presenter = Presenter(client, windows);
        var first = Question("First?", "Yes", "No");
        var second = Question("Second?", "OK");

        client.MessageRequested += Raise.Event<EventHandler<ServerMessageRequest>>(client, first);
        client.MessageRequested += Raise.Event<EventHandler<ServerMessageRequest>>(client, second);
        var shown = await windows.NextShownAsync();

        shown.Title.Should().Be("RDCore asks");
        shown.Message.Should().Be("First?");
        windows.Shown.Should().ContainSingle("the second waits for the first to be answered");
        shown.Choices[1].Choose.Execute(null);
        (await first.Answer!.WaitAsync(TimeSpan.FromSeconds(10), Token))!.Title.Should().Be("No");

        var next = await windows.NextShownAsync();
        next.Message.Should().Be("Second?");
        next.CloseCommand.Execute(null);
        (await second.Answer!.WaitAsync(TimeSpan.FromSeconds(10), Token)).Should().BeNull("closing it chooses nothing");
    }

    [Fact]
    public async Task AQuestionWithdrawnWhileShownIsTakenAway_AndOneWithdrawnWhileWaitingIsNeverShown()
    {
        var client = Substitute.For<ILspClient>();
        var windows = new Windows();
        using var presenter = Presenter(client, windows);
        using var withdrawFirst = new CancellationTokenSource();
        using var withdrawSecond = new CancellationTokenSource();
        var first = Withdrawable("First?", withdrawFirst.Token, "OK");
        var second = Withdrawable("Second?", withdrawSecond.Token, "OK");
        client.MessageRequested += Raise.Event<EventHandler<ServerMessageRequest>>(client, first);
        client.MessageRequested += Raise.Event<EventHandler<ServerMessageRequest>>(client, second);
        await windows.NextShownAsync();

        withdrawSecond.Cancel();
        (await second.Answer!.WaitAsync(TimeSpan.FromSeconds(10), Token)).Should().BeNull();
        withdrawFirst.Cancel();
        (await first.Answer!.WaitAsync(TimeSpan.FromSeconds(10), Token)).Should().BeNull();

        windows.Shown.Select(d => d.Message).Should().Equal("First?");
    }

    [Fact]
    public void AQuestionWithNothingToChooseOffersAClose_AndNamesTheServerWhenItCan()
    {
        var bare = new ServerPromptDialogViewModel(Question("Indexing finished."), Localization());
        bare.HasNoChoices.Should().BeTrue();

        var anonymous = new ServerPromptDialogViewModel(
            new ServerMessageRequest(LspMessageType.Info, "?", [], NeverWithdrawn), Localization());
        anonymous.Title.Should().Be("Language server asks");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }
}
