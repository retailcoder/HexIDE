using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexIDE.IDE;
using HexIDE.Localization;
using HexIDE.Lsp;

namespace HexIDE.Forms.ViewModels;

/// <summary>
/// A language server's question (<c>window/showMessageRequest</c>): its message, and a button for each choice it offers.
/// </summary>
/// <remarks>
/// The choices are the server's own words, shown as they came, and the one pressed is answered exactly as the server sent
/// it. Closing the window, or the server withdrawing the question, answers that nothing was chosen. A question with no
/// choices only needs reading, and gets a Close button.
/// </remarks>
public sealed partial class ServerPromptDialogViewModel : ObservableObject, IDialog
{
    private readonly ILocalizationService _localization;
    private readonly string? _serverName;

    public ServerPromptDialogViewModel(ServerMessageRequest request, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(request);
        _localization = localization;
        _serverName = request.ServerDisplayName;
        Message = request.Message;
        Choices = [.. request.Actions.Select(action => new ServerPromptChoice(action, Choose))];
    }

    public string Title => string.Format(_localization.GetString("Str.Dialog.ServerPrompt.Title"),
        string.IsNullOrWhiteSpace(_serverName) ? _localization.GetString("Str.StatusBar.LanguageServer") : _serverName);

    public bool CanResize => false;

    public event Action<bool>? CloseRequested;

    /// <summary>What the server says.</summary>
    public string Message { get; }

    /// <summary>A button per choice, in the server's order.</summary>
    public IReadOnlyList<ServerPromptChoice> Choices { get; }

    /// <summary>Whether the question offers nothing to choose, and so needs a Close button of its own.</summary>
    public bool HasNoChoices => Choices.Count == 0;

    /// <summary>The choice pressed, or null when none was.</summary>
    public ServerMessageAction? Chosen { get; private set; }

    private void Choose(ServerMessageAction action)
    {
        Chosen = action;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);

    /// <summary>Takes the question away unanswered: the server withdrew it, or its connection ended.</summary>
    public void Withdraw() => CloseRequested?.Invoke(false);
}

/// <summary>One of a server's choices, as a button.</summary>
public sealed class ServerPromptChoice(ServerMessageAction action, Action<ServerMessageAction> choose)
{
    public string Title => action.Title;

    public IRelayCommand Choose { get; } = new RelayCommand(() => choose(action));
}
