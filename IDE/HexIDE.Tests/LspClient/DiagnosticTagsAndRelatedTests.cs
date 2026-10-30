using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using HexIDE.Automation;
using HexIDE.Controls;
using HexIDE.Forms.ViewModels;
using HexIDE.Lsp;
using HexIDE.Lsp.Messages;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using StreamJsonRpc;

// NB: namespace deliberately avoids a `Lsp` segment — see VBLspClientTests.
namespace HexIDE.Tests.LspClient;

/// <summary>
/// A diagnostic's <c>tags</c> and <c>relatedInformation</c> (hexide-io/HexIDE#453): kept on both paths, read leniently,
/// declared, and drawn — unnecessary code faded, deprecated code struck through, related places listed in the card.
/// </summary>
public class DiagnosticTagsAndRelatedTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];

    private static readonly JsonSerializerOptions ClientOptions =
        new(LspJsonContext.Default.Options) { PropertyNameCaseInsensitive = true };

    private const string ARange = @"{""start"":{""line"":0,""character"":0},""end"":{""line"":0,""character"":9}}";

    private const string Members =
        @"""message"":""'os' imported but unused"",""severity"":2,""tags"":[1,2],"
      + @"""relatedInformation"":[{""location"":{""uri"":""file:///c:/p/Module2.bas"",""range"":"
      + @"{""start"":{""line"":11,""character"":0},""end"":{""line"":11,""character"":9}}},""message"":""first declared here""}]";

    private static string OneItem(string members) => "[{\"range\":" + ARange + "," + members + "}]";

    private static Diagnostic Pull(string members)
    {
        using var reply = JsonDocument.Parse(OneItem(members));
        return VBLspClient.ReadDiagnostics(reply.RootElement).Single();
    }

    private static Diagnostic Push(string members) =>
        JsonSerializer.Deserialize<PublishDiagnosticsParams>(
            "{\"uri\":\"file:///c:/x.bas\",\"diagnostics\":" + OneItem(members) + "}", ClientOptions)!.Diagnostics.Single();

    [Fact]
    public void BothPathsKeepTheTagsAndTheRelatedPlaces_PastTheReplyTheyArrivedIn()
    {
        foreach (var diagnostic in new[] { Pull(Members), Push(Members) })
        {
            diagnostic.Tags.Should().Equal(DiagnosticTag.Unnecessary, DiagnosticTag.Deprecated);
            var related = diagnostic.RelatedInformation.Should().ContainSingle().Subject;
            related.Should().Be(new DiagnosticRelatedInformation("file:///c:/p/Module2.bas", new Position(11, 0), "first declared here"));
        }
    }

    [Fact]
    public void WhatDoesNotReadAsATagOrAPlace_IsSkipped_AndTheDiagnosticKept()
    {
        const string odd =
            @"""message"":""m"",""tags"":[""unnecessary"",9,2,2],"
          + @"""relatedInformation"":[{""message"":""no location""},7,{""location"":{""uri"":""file:///c:/p/A.bas"",""range"":" + ARange + @"},""message"":""kept""}]";

        foreach (var diagnostic in new[] { Pull(odd), Push(odd) })
        {
            diagnostic.Tags.Should().Equal(DiagnosticTag.Deprecated);
            diagnostic.RelatedInformation.Select(r => r.Message).Should().Equal("kept");
        }

        const string notArrays = @"""message"":""m"",""tags"":""1"",""relatedInformation"":{}";
        foreach (var diagnostic in new[] { Pull(notArrays), Push(notArrays) })
        {
            diagnostic.Message.Should().Be("m");
            diagnostic.Tags.Should().BeEmpty();
            diagnostic.RelatedInformation.Should().BeEmpty();
        }
    }

    [Fact]
    public void ARelatedPlaceIsOneLine_NamedAsTheEditorCountsLines()
    {
        LspDocumentSession.RelatedLine(new("file:///c:/p/Module%202.bas", new Position(11, 4), "first declared here"))
            .Should().Be("Module 2.bas:12: first declared here");
        LspDocumentSession.RelatedLine(new("untitled:Form1", new Position(0, 0), "here")).Should().Be("Form1:1: here");
    }

    [AvaloniaFact]
    public void TheCardListsTheRelatedPlacesUnderTheMessage()
    {
        var card = HoverCard.Build(
            [new LspMarker(0, 3, false, "Duplicate declaration", "RDCore", Related: ["Module2.bas:12: first declared here"])],
            null, new FontFamily("Courier New"));

        UiAutomationDriver.TipText(card).Should().Be("RDCore: Duplicate declaration / Module2.bas:12: first declared here");
    }

    [AvaloniaFact]
    public void UnnecessaryCodeIsFadedInItsOwnColour_AndDeprecatedCodeStruckThrough()
    {
        var editor = new TextEditor { Document = new TextDocument("Dim unused\nOld()") };
        var window = new Window { Content = editor, Width = 400, Height = 200 };
        window.Show();
        var view = editor.TextArea.TextView;
        var colorizer = new LspDiagnosticsColorizer(view);
        view.LineTransformers.Add(colorizer);
        colorizer.SetMarkers(
        [
            new LspMarker(4, 10, false, "unused", Unnecessary: true),
            new LspMarker(11, 14, false, "obsolete", Deprecated: true),
        ]);

        var first = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
        var faded = first.Elements.Where(e => first.FirstDocumentLine.Offset + e.RelativeTextOffset >= 4).ToList();
        var plain = first.Elements.Where(e => first.FirstDocumentLine.Offset + e.RelativeTextOffset < 4).ToList();
        faded.Should().NotBeEmpty();
        faded.Select(e => e.TextRunProperties.ForegroundBrush).Should().AllSatisfy(b =>
            b.Should().BeAssignableTo<ISolidColorBrush>().Which.Opacity.Should().Be(LspDiagnosticsColorizer.FadedOpacity));
        plain.Select(e => (e.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Opacity).Should().AllSatisfy(o => o.Should().Be(1));

        var second = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(2));
        second.Elements.Where(e => e.RelativeTextOffset < 3).Should().AllSatisfy(e =>
            e.TextRunProperties.TextDecorations.Should().BeEquivalentTo(TextDecorations.Strikethrough));
        second.Elements.Where(e => e.RelativeTextOffset >= 3).Should().AllSatisfy(e =>
            e.TextRunProperties.TextDecorations.Should().BeNull());
        window.Close();
    }

    private sealed class HandshakeRecorder
    {
        private readonly TaskCompletionSource<JsonElement> _initialize = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonElement> InitializeParams => _initialize.Task;

        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public JsonElement Initialize(JsonElement p)
        {
            _initialize.TrySetResult(p.Clone());
            return JsonDocument.Parse("""{"capabilities":{}}""").RootElement.Clone();
        }

        [JsonRpcMethod("initialized")]
        public void Initialized(JsonElement _) { }
    }

    [AvaloniaFact]
    public async Task TheClientDeclaresBothTagsAndRelatedInformation()
    {
        var server = new HandshakeRecorder();
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

        await client.StartAsync(TestContext.Current.CancellationToken);
        var initialize = await server.InitializeParams.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var publish = initialize.GetProperty("capabilities").GetProperty("textDocument").GetProperty("publishDiagnostics");
        publish.GetProperty("relatedInformation").GetBoolean().Should().BeTrue();
        publish.GetProperty("tagSupport").GetProperty("valueSet").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(1, 2);
        serverRpc.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }
}
