using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Save a Link: an address, a board to put it on, and the page's own headline once it has been
/// read.
/// </summary>
/// <remarks>
/// The feature this dialog is the front of is the one that makes a board more than a folder: a
/// reader keeps a board for a subject, and some of what belongs on it was sent to them rather
/// than published in a feed they follow.
/// <para>
/// The page is read for its headline, its summary and its picture, so a saved link sits in the
/// article list beside the things that arrived from a feed and looks like one of them — but the
/// save does not depend on that working. A page that will not load is still saved, headed with
/// its address, and this says which happened rather than leaving the reader to wonder whether
/// their link went anywhere.
/// </para>
/// </remarks>
public sealed class SaveLinkDialog : Window
{
    private readonly MailRepository _mail;
    private readonly Func<string, Board?, Task<(bool Ok, string Headline, string Trouble)>> _save;

    private readonly TextBox _url = new();
    private readonly ComboBox _board = new();
    private readonly TextBlock _note = new();
    private readonly Button _ok;

    private List<Board> _boards = [];

    /// <summary>The board that was chosen, or null when the reader chose to save it to none.</summary>
    public Board? Chosen { get; private set; }

    /// <summary>The address as typed, trimmed. Empty until OK is pressed.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>True when something was saved.</summary>
    public bool Saved { get; private set; }

    /// <param name="save">
    /// Does the saving, given the address and the board chosen, and says how it went. Passed in
    /// rather than done here so the fetching stays out of a dialog.
    /// </param>
    /// <param name="preferred">The board to have selected on the way in — the one being read.</param>
    /// <param name="suggested">An address to start with, from the clipboard.</param>
    public SaveLinkDialog(
        MailRepository mail,
        Func<string, Board?, Task<(bool Ok, string Headline, string Trouble)>> save,
        Board? preferred = null,
        string suggested = "")
    {
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _save = save ?? throw new ArgumentNullException(nameof(save));

        Title = "Save a Link";
        Width = 560;
        Height = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _ok = Push("Save", () => _ = SaveAsync());
        _ok.IsDefault = true;
        _ok.IsEnabled = false;

        var cancel = Push("Cancel", Close);
        cancel.IsCancel = true;

        DialogChrome.Apply(this, Layout(cancel));
        FillBoards(preferred);

        Opened += (_, _) =>
        {
            if (suggested is { Length: > 0 } seed)
            {
                _url.Text = seed;
                _url.SelectAll();
            }

            _url.Focus();
        };
    }

    private Control Layout(Button cancel)
    {
        var heading = Label("Save a Link", bold: true, size: 15);

        var explain = Label(
            "Any web address can go on a board, whether or not you are subscribed to the site. "
            + "The page is read for its headline and its picture so it sits in the list like an "
            + "article.");
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 16);
        Bind(explain, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _url.PlaceholderText = "https://example.com/an-article";
        _url.TextChanged += (_, _) => Validate();
        _url.KeyDown += (_, e) =>
        {
            if (e.Key is not Key.Enter || !_ok.IsEnabled) return;
            e.Handled = true;
            _ = SaveAsync();
        };

        _board.MinWidth = 220;
        _board.HorizontalAlignment = HorizontalAlignment.Left;
        _board.SelectionChanged += (_, _) =>
        {
            // "New board…" is the last entry, and it is a way into the dialog that makes them —
            // the new one comes back selected, so a reader does not make a board and then have
            // to find it in the list.
            if (_filling || _board.SelectedIndex != _boards.Count + 1) return;
            _ = NewBoardAsync();
        };

        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 12, 0, 0);
        Bind(_note, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { _ok, cancel },
        };

        return new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                heading,
                explain,
                Label("Address:"),
                _url,
                Spacer(),
                Label("Save it to:"),
                _board,
                _note,
                buttons,
            },
        };
    }

    private static Control Spacer() => new Border { Height = 12 };

    /// <summary>
    /// The boards, with the one being read selected.
    /// </summary>
    /// <remarks>
    /// "No board — just save it" is a real answer and is offered: a link saved with nowhere to
    /// put it is still filed and still searchable, and forcing a collection on somebody who only
    /// wanted to keep a page is how a keep pile becomes a chore.
    /// </remarks>
    private void FillBoards(Board? preferred)
    {
        _boards = [.. _mail.Boards()];

        var choices = new List<string> { "No board — just save it" };
        choices.AddRange(_boards.Select(b => b.Name));
        choices.Add("New board…");

        // Refilled after a board is made, so the list is rebuilt while its own selection event
        // is suppressed — otherwise setting the index below reads as the reader picking again.
        _filling = true;
        _board.ItemsSource = choices;
        _board.SelectedIndex = preferred is { } wanted && _boards.FindIndex(b => b.Id == wanted.Id) is >= 0 and var at
            ? at + 1
            : _boards.Count > 0 ? 1 : 0;
        _filling = false;
    }

    private bool _filling;

    private async Task NewBoardAsync()
    {
        Board? made = null;
        var dialog = new BoardsDialog(_mail, DateTimeOffset.UtcNow) { Made = board => made = board };
        await dialog.ShowDialog(this);

        FillBoards(made);
    }

    private void Validate()
    {
        var typed = _url.Text?.Trim() ?? string.Empty;

        if (typed.Length == 0)
        {
            _ok.IsEnabled = false;
            _note.Text = string.Empty;
            return;
        }

        if (Mailbox.Protocols.SavedLinks.Normalize(typed) is null)
        {
            _ok.IsEnabled = false;
            _note.Text = "That is not a web address.";
            return;
        }

        _ok.IsEnabled = true;
        _note.Text = string.Empty;
    }

    private async Task SaveAsync()
    {
        // The button becomes Close once a link has been saved but the page could not be read.
        if (Saved)
        {
            Close();
            return;
        }

        var typed = _url.Text?.Trim() ?? string.Empty;
        if (Mailbox.Protocols.SavedLinks.Normalize(typed) is null) return;

        Address = typed;
        Chosen = _board.SelectedIndex is var at && at >= 1 && at <= _boards.Count ? _boards[at - 1] : null;

        // Held open while the page is read, rather than closing on the press: the reader wants to
        // see what was saved, and a dialog that vanishes before the fetch is a dialog that cannot
        // tell them the page would not load.
        _ok.IsEnabled = false;
        _url.IsEnabled = false;
        _note.Text = "Reading the page…";

        var (ok, headline, trouble) = await _save(Address, Chosen);

        if (!ok)
        {
            _note.Text = trouble is { Length: > 0 } why ? why : "That link could not be saved.";
            _ok.IsEnabled = true;
            _url.IsEnabled = true;
            return;
        }

        Saved = true;

        if (trouble is { Length: > 0 } partial)
        {
            // Saved, but as an address rather than as an article. Said here rather than in the
            // status bar, because the reader is looking at this window.
            _note.Text = $"Saved as “{headline}”. The page itself could not be read — {partial}";
            _ok.Content = "Close";
            _ok.IsEnabled = true;
            _url.IsEnabled = false;
            return;
        }

        Close();
    }

    // ---- Small helpers ------------------------------------------------------------------------

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    private static TextBlock Label(string text, bool bold = false, double size = 12)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private static Button Push(string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 80 };
        button.Click += (_, _) => onClick();
        return button;
    }
}
