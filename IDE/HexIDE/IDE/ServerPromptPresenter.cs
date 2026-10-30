using HexIDE.Forms.ViewModels;
using HexIDE.Localization;
using HexIDE.Lsp;
using Serilog;

namespace HexIDE.IDE;

/// <summary>
/// Puts what a language server asks of the user in front of them: its questions (<c>window/showMessageRequest</c>), one
/// at a time, and its requests to show something (<c>window/showDocument</c>), and sends back the answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>A window the user answers when they choose, not a modal.</b> A question is shown beside the IDE, which stays usable,
/// and the connection goes on while it waits: the server's other messages and the client's requests are not held behind
/// it. Closing it answers that nothing was chosen.
/// </para>
/// <para>
/// <b>One at a time.</b> A second question waits for the first to be answered, so a server asking several in a row cannot
/// stack windows on top of each other. A question the server withdraws while it waits is never shown; one withdrawn while
/// it is shown is taken away.
/// </para>
/// <para>
/// <b>A document the IDE has is opened in it; anything else is asked about first.</b> A server can name a document of the
/// open projects, which is opened in its code window with the caret where the server says. Opening something outside the
/// IDE hands it to another program, so the user is asked first, in the same queue as the questions, and only a web
/// address is ever handed on: a <c>file:</c> address opened outside would run whatever it names.
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
    private readonly IEditorService _editor;
    private readonly ILocalizationService _localization;
    private readonly Func<Func<Task>, Task> _onUiThread;
    private readonly Action<Action> _postToUiThread;
    private readonly Func<Uri, Task<bool>> _launch;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public ServerPromptPresenter(ILspClient client, IWindowManager windows, IEditorService editor, ILocalizationService localization)
        : this(client, windows, editor, localization,
            work => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            uri => Avalonia.Controls.TopLevel.GetTopLevel(Static.MainView)?.Launcher.LaunchUriAsync(uri) ?? Task.FromResult(false))
    {
    }

    /// <param name="onUiThread">Runs the showing of something where windows are shown, and completes when it is done.</param>
    /// <param name="postToUiThread">Runs a withdrawal there: a question is withdrawn on whichever thread the connection cancels it.</param>
    /// <param name="launch">Hands an address to the program the system opens it with; whether it did.</param>
    internal ServerPromptPresenter(
        ILspClient client, IWindowManager windows, IEditorService editor, ILocalizationService localization,
        Func<Func<Task>, Task> onUiThread, Action<Action> postToUiThread, Func<Uri, Task<bool>> launch)
    {
        _client = client;
        _windows = windows;
        _editor = editor;
        _localization = localization;
        _onUiThread = onUiThread;
        _postToUiThread = postToUiThread;
        _launch = launch;
        _client.MessageRequested += OnMessageRequested;
        _client.DocumentRequested += OnDocumentRequested;
    }

    private void OnMessageRequested(object? sender, ServerMessageRequest request) => request.Respond(AskAsync(request));

    private void OnDocumentRequested(object? sender, ServerDocumentRequest request) => request.Respond(ShowAsync(request));

    private async Task<ServerMessageAction?> AskAsync(ServerMessageRequest request)
    {
        if (!await TakeTurnAsync(request.Cancellation)) return null;
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

    private async Task<bool> ShowAsync(ServerDocumentRequest request)
    {
        if (!request.External)
        {
            var shown = false;
            var line = (request.SelectionStart?.Line ?? 0) + 1;
            var column = (request.SelectionStart?.Character ?? 0) + 1;
            await _onUiThread(() =>
            {
                shown = _editor.NavigateTo(request.Uri, line, column);
                return Task.CompletedTask;
            });
            return shown;
        }

        // Handed only to a browser, and only once the user agrees.
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            Log.Information("A language server asked to open {Uri} outside the IDE; only a web address is opened that way.", request.Uri);
            return false;
        }

        if (!await TakeTurnAsync(request.Cancellation)) return false;
        try
        {
            if (request.Cancellation.IsCancellationRequested) return false;

            var server = string.IsNullOrWhiteSpace(request.ServerDisplayName)
                ? _localization.GetString("Str.StatusBar.LanguageServer")
                : request.ServerDisplayName;
            var agreed = MessageBoxResult.None;
            await _onUiThread(async () => agreed = await _windows.MessageBox(
                string.Format(_localization.GetString("Str.Dialog.ServerDocument.OpenExternal"), server, address.AbsoluteUri),
                string.Format(_localization.GetString("Str.Dialog.ServerPrompt.Title"), server),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question));
            return agreed == MessageBoxResult.Yes && !request.Cancellation.IsCancellationRequested && await _launch(address);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A language server's address could not be opened; answering that it was not shown.");
            return false;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    // Waits for the question before to be answered; false when this one is withdrawn meanwhile.
    private async Task<bool> TakeTurnAsync(CancellationToken withdrawal)
    {
        try
        {
            await _oneAtATime.WaitAsync(withdrawal);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client.MessageRequested -= OnMessageRequested;
        _client.DocumentRequested -= OnDocumentRequested;
        _oneAtATime.Dispose();
    }
}
