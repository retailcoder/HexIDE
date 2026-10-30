using HexIDE.Lsp.Messages;

namespace HexIDE.Lsp;

/// <summary>
/// A server asking for a document or an address to be shown (<c>window/showDocument</c>), and whether it was, once
/// whoever shows it says.
/// </summary>
/// <remarks>
/// Answered through <see cref="Respond"/>, as a <see cref="ServerMessageRequest"/> is, so the connection does not wait
/// for the user. With no answer, the server is told it was not shown.
/// </remarks>
public sealed class ServerDocumentRequest
{
    private Task<bool>? _answer;

    public ServerDocumentRequest(string uri, bool external, bool takeFocus, Position? selectionStart, CancellationToken cancellation)
    {
        Uri = uri;
        External = external;
        TakeFocus = takeFocus;
        SelectionStart = selectionStart;
        Cancellation = cancellation;
    }

    /// <summary>What to show, as the server named it.</summary>
    public string Uri { get; }

    /// <summary>Whether the server asks for it to be opened outside the IDE, in the program the system opens it with.</summary>
    public bool External { get; }

    /// <summary>Whether the server asks for it to be brought to the front.</summary>
    public bool TakeFocus { get; }

    /// <summary>Where in the document the server asks the caret to go, zero-based as the protocol counts; null for nowhere.</summary>
    public Position? SelectionStart { get; }

    /// <summary>Cancelled when the server withdraws the request or the connection ends.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>The server's name, for whoever shows it. The router sets it before it passes the request on.</summary>
    public string? ServerDisplayName { get; set; }

    /// <summary>The answer given through <see cref="Respond"/>, or null while nobody has given one.</summary>
    public Task<bool>? Answer => _answer;

    /// <summary>Gives the answer: whether it was shown. Only the first call counts.</summary>
    /// <returns>Whether this call's answer is the one that will be sent.</returns>
    public bool Respond(Task<bool> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Interlocked.CompareExchange(ref _answer, answer, null) is null;
    }
}
