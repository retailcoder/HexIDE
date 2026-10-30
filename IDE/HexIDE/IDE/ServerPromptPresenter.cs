using HexIDE.Forms.ViewModels;
using HexIDE.Localization;
using HexIDE.Lsp;
using Serilog;

namespace HexIDE.IDE;

/// <summary>
/// Puts a language server's questions (<c>window/showMessageRequest</c>) in front of the user, one at a time, and sends
/// back the choice made.
/// </summary>
/// <remarks>
/// <para>
/// <b>A window the user answers when they choose, not a modal.</b> The question is shown beside the IDE, which stays
/// usable, and the connection goes on while it waits: the server's other messages and the client's requests are not held
/// behind it. Closing it answers that nothing was chosen.
/// </para>
/// <para>
/// <b>One at a time.</b> A second question waits for the first to be answered, so a server asking several in a row
/// cannot stack windows on top of each other. A question the server withdraws while it waits is never shown; one
/// withdrawn while it is shown is taken away.
/// </para>
/// <para>
/// Status messages (<c>window/showMessage</c>) stay in the status bar (<see cref="LanguageServerMessageReporter"/>): a
/// question needs an answer, and a message only needs reading.
/// </para>
/// </remarks>
public sealed class ServerPromptPresenter : IDisposable
{
    private readonly ILspClient _client;
    private readonly IWindowManager _windows;
    private readonly ILocalizationService _localization;
    private readonly Func<Func<Task>, Task> _onUiThread;
    private readonly Action<Action> _postToUiThread;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public ServerPromptPresenter(ILspClient client, IWindowManager windows, ILocalizationService localization)
        : this(client, windows, localization,
            work => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action))
    {
    }

    /// <param name="onUiThread">Runs the showing of a question where windows are shown, and completes when it closes.</param>
    /// <param name="postToUiThread">Runs a withdrawal there: a question is withdrawn on whichever thread the connection cancels it.</param>
    internal ServerPromptPresenter(
        ILspClient client, IWindowManager windows, ILocalizationService localization,
        Func<Func<Task>, Task> onUiThread, Action<Action> postToUiThread)
    {
        _client = client;
        _windows = windows;
        _localization = localization;
        _onUiThread = onUiThread;
        _postToUiThread = postToUiThread;
        _client.MessageRequested += OnMessageRequested;
    }

    private void OnMessageRequested(object? sender, ServerMessageRequest request) => request.Respond(AskAsync(request));

    private async Task<ServerMessageAction?> AskAsync(ServerMessageRequest request)
    {
        try
        {
            await _oneAtATime.WaitAsync(request.Cancellation);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (request.Cancellation.IsCancellationRequested) return null;

            var dialog = new ServerPromptDialogViewModel(request, _localization);
            await _onUiThread(async () =>
            {
                using var withdrawn = request.Cancellation.Register(() => _postToUiThread(dialog.Withdraw));
                await _windows.ShowWindow(dialog);
            });
            return dialog.Chosen;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A language server's question could not be shown; answering that nothing was chosen.");
            return null;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public void Dispose()
    {
        _client.MessageRequested -= OnMessageRequested;
        _oneAtATime.Dispose();
    }
}
