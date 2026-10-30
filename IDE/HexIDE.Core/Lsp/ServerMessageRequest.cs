using System.Text.Json;
using HexIDE.Lsp.Messages;

namespace HexIDE.Lsp;

/// <summary>
/// One of the choices a server offers in <c>window/showMessageRequest</c>: its title, and the item exactly as the
/// server sent it, which is what is sent back when it is chosen.
/// </summary>
/// <remarks>
/// The item is kept whole because a <c>MessageActionItem</c> may carry members of the server's own beside its title, and
/// a client that declares <c>additionalPropertiesSupport</c> hands them back untouched.
/// </remarks>
public sealed record ServerMessageAction(string Title, JsonElement Item);

/// <summary>
/// A server asking the user something (<c>window/showMessageRequest</c>), and the answer, once whoever shows it gives one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Answered through <see cref="Respond"/>, not by blocking.</b> The request is raised as an event on the connection's
/// own thread, and the connection goes on while the user decides: the server's other messages, and the client's, are not
/// held behind the question. The first answer given is the one sent; with none, the server is told nothing was chosen.
/// </para>
/// <para>
/// <see cref="Cancellation"/> is cancelled when the server withdraws the question or the connection ends, which is when
/// whoever shows it should take it away again.
/// </para>
/// </remarks>
public sealed class ServerMessageRequest
{
    private Task<ServerMessageAction?>? _answer;

    public ServerMessageRequest(
        LspMessageType type, string message, IReadOnlyList<ServerMessageAction> actions, CancellationToken cancellation)
    {
        Type = type;
        Message = message;
        Actions = actions;
        Cancellation = cancellation;
    }

    /// <summary>How serious the server says it is.</summary>
    public LspMessageType Type { get; }

    /// <summary>What the server says, as it said it.</summary>
    public string Message { get; }

    /// <summary>The choices offered, in the server's order. Possibly none, when the message only needs reading.</summary>
    public IReadOnlyList<ServerMessageAction> Actions { get; }

    /// <summary>Cancelled when the question is withdrawn or the connection ends.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>The server's name, for whoever shows the question. The router sets it before it passes the request on.</summary>
    public string? ServerDisplayName { get; set; }

    /// <summary>The answer given through <see cref="Respond"/>, or null while nobody has given one.</summary>
    public Task<ServerMessageAction?>? Answer => _answer;

    /// <summary>
    /// Gives the answer: the action chosen, or null for none. Only the first call counts, so two subscribers cannot send a
    /// server two answers to one question.
    /// </summary>
    /// <returns>Whether this call's answer is the one that will be sent.</returns>
    public bool Respond(Task<ServerMessageAction?> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Interlocked.CompareExchange(ref _answer, answer, null) is null;
    }
}
