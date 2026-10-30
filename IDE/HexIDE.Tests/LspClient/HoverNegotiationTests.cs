using System.Text.Json;
using HexIDE.Lsp;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using StreamJsonRpc;

// NB: namespace deliberately avoids a `Lsp` segment — see VBLspClientTests.
namespace HexIDE.Tests.LspClient;

/// <summary>The client asks for Markdown hovers, which the code window's hover card draws.</summary>
public class HoverNegotiationTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];

    private sealed class HandshakeRecorder
    {
        private readonly TaskCompletionSource<JsonElement> _initialize =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            JsonDocument.Parse("""{"contents":{"kind":"markdown","value":"```vba\nDim x\n```"}}""").RootElement.Clone();
    }

    [AvaloniaFact]
    public async Task TheClientPrefersMarkdownHovers_AndKeepsTheKindTheServerAnswered()
    {
        var server = new HandshakeRecorder();
        var (clientSide, serverSide) = FullDuplexStream.CreatePair();
        var serverRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(serverSide, serverSide, new SystemTextJsonFormatter()), server);
        serverRpc.StartListening();

        var transport = Substitute.For<ILspTransport>();
        transport.IsAlive.Returns(true);
        transport.ConnectAsync(Arg.Any<IJsonRpcMessageFormatter>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IJsonRpcMessageHandler?>(
                new HeaderDelimitedMessageHandler(clientSide, clientSide, ci.Arg<IJsonRpcMessageFormatter>())));

        var client = new VBLspClient(transport, Substitute.For<ILogger<VBLspClient>>(), "vb6");
        _disposables.Add(client);
        await client.StartAsync(TestContext.Current.CancellationToken);
        var initialize = await server.InitializeParams.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        initialize.GetProperty("capabilities").GetProperty("textDocument").GetProperty("hover")
            .GetProperty("contentFormat").EnumerateArray().Select(f => f.GetString())
            .Should().Equal(["markdown", "plaintext"], "Markdown is preferred, and plain text is still accepted");

        var hover = await client.RequestHoverAsync("untitled:Module1.bas", new(0, 4), TestContext.Current.CancellationToken);
        hover!.Contents.Kind.Should().Be("markdown");
        hover.Contents.Value.Should().Be("```vba\nDim x\n```");
        serverRpc.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }
}
