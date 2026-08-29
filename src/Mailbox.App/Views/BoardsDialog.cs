using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mailbox.Core.Diagnostics;
using Mailbox.Store;
using Mailbox.Theming.Icons;

namespace Mailbox.App.Views;

/// <summary>
/// The boards a reader keeps: what each one is for, how much is on it, and the box for making
/// another.
/// </summary>
/// <remarks>
/// The shape is the Mute Filters dialog's, which is the shape Feedly's own board management has:
/// an add box over a list, each row carrying its name, what it is for and its count, with the
/// destructive action at the end of the row rather than behind a menu.
/// <para>
/// Two things are said on the page rather than left to be found out. That deleting a board keeps
/// its articles — it is a collection, and emptying it is not a licence to delete somebody's
/// reading. And that a board holds any address, not only what a subscription delivered, because
/// that is the whole difference between a board and a heading and it is not visible from
/// looking at one.
/// </para>
/// <para>
/// The reader this is measured against allows three boards on its free tier and charges for the
/// rest. There is no cap here, and nothing in the code to remove if there ever were: a
/// collection is a row in a table.
/// </para>
/// </remarks>
public sealed class BoardsDialog : Window
{
    private readonly MailRepository _mail;
    private readonly DateTimeOffset _now;

    private readonly TextBox _name = new();
    private readonly TextBox _purpose = new();
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _note = new();
    private readonly Button _add;

    /// <summary>True when a board was made, renamed or removed.</summary>
    public bool Changed { get; private set; }

    /// <summary>A name to start the box with, from "New Board…". Empty for none.</summary>
    public string Suggested { get; init; } = string.Empty;

    /// <summary>Run with a board that has just been made, so the caller can save onto it.</summary>
    public Action<Board>? Made { get; init; }

    public BoardsDialog(MailRepository mail, DateTimeOffset now)
    {
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _now = now;

        Title = "Boards";
        Width = 620;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _add = Push("Add", Commit);
        _add.IsDefault = true;
        _add.IsEnabled = false;

        var close = Push("Close", Close);
        close.IsCancel = true;

        DialogChrome.Apply(this, Layout(close));
        Fill();

        Opened += (_, _) =>
        {
            if (Suggested is { Length: > 0 } seed)
            {
                _name.Text = seed;
                _name.SelectAll();
            }

            _name.Focus();

            // The harness's way in, on the pass after this one so the suggested name is already in
            // the box. Its own verbs rather than the store's calls: what had no evidence was that
            // this page reaches the store — that the box refuses a name another board has, that a
            // rename which clashes puts the old name back, and that removing a board keeps what
            // was on it, which is the sentence printed under the list.
            Dispatcher.UIThread.Post(
                () =>
                {
                    Report("as opened");
                    if (Environment.GetEnvironmentVariable("MAILBOX_BOARD_DIALOG") is { Length: > 0 } posed) Harness(posed);
                },
                DispatcherPriority.Background);
        };
    }

    /// <summary>What the dialog is showing, for a run to read back.</summary>
    private void Report(string when)
    {
        Log.Info($"Harness: boards dialog {when} — name “{_name.Text}”, purpose “{_purpose.Text}”, "
            + $"Add is {(_add.IsEnabled ? "enabled" : "greyed")}"
            + $"{(_note.Text is { Length: > 0 } note ? $", note “{note}”" : string.Empty)}.");

        foreach (var board in _mail.Boards())
        {
            Log.Info($"Harness: boards dialog   · “{board.Name}” — {board.Count} article(s)"
                + (board.Description.Length > 0 ? $", for {board.Description}" : string.Empty));
        }
    }

    /// <summary>
    /// Presses the dialog's own controls:
    /// <c>MAILBOX_BOARD_DIALOG=add:Reading:Things to read;rename:Reading:Later;delete:Later</c>.
    /// </summary>
    /// <remarks>
    /// A dispatcher turn between the steps, because Avalonia raises <c>TextChanged</c> on a later
    /// pass: a pose that filled the box and pressed Add in the same pass found Add still greyed
    /// and reported that a perfectly good name had been refused, which is a bug in the pose that
    /// reads exactly like a bug in the dialog. Focus is moved for the same reason it is by a
    /// reader — the purpose box saves what it holds when it loses the focus, and a hand-raised
    /// <c>LostFocus</c> is not that event.
    /// </remarks>
    private void Harness(string spec)
    {
        foreach (var step in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = step.Split(':', StringSplitOptions.TrimEntries);
            var verb = parts[0].ToLowerInvariant();
            string Arg(int at) => parts.Length > at ? parts[at] : string.Empty;

            switch (verb)
            {
                case "add":
                    _name.Text = Arg(1);
                    _purpose.Text = Arg(2);
                    Dispatcher.UIThread.RunJobs();
                    Report($"with “{Arg(1)}” typed");
                    if (_add.IsEnabled) _add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else Log.Info($"Harness: boards dialog — Add is greyed for “{Arg(1)}”, so nothing was made.");
                    break;

                case "rename" when Box(Arg(1), first: true) is { } box:
                    box.Text = Arg(2);
                    Dispatcher.UIThread.RunJobs();
                    box.RaiseEvent(new Avalonia.Input.KeyEventArgs
                    {
                        RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                        Source = box,
                        Key = Key.Enter,
                    });
                    Log.Info($"Harness: boards dialog — “{Arg(1)}” renamed to “{Arg(2)}”; the box now reads “{box.Text}”.");
                    break;

                case "describe" when Box(Arg(1), first: false) is { } purpose:
                    purpose.Focus();
                    purpose.Text = Arg(2);
                    Dispatcher.UIThread.RunJobs();

                    // Away again, because what the box holds is saved when it loses the focus.
                    _name.Focus();
                    Dispatcher.UIThread.RunJobs();
                    Log.Info($"Harness: boards dialog — “{Arg(1)}” described as “{Arg(2)}”.");
                    break;

                case "delete" when RowOf(Arg(1)) is { } row:
                    if (row.GetVisualDescendants().OfType<Button>().LastOrDefault() is not { } remove) break;
                    remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Dispatcher.UIThread.RunJobs();
                    Log.Info($"Harness: boards dialog — the delete button on “{Arg(1)}” was pressed.");
                    break;

                default:
                    Log.Info($"Harness: boards dialog — “{step}” names nothing on this page.");
                    break;
            }
        }

        Report("after");
    }

    /// <summary>The row of the list standing for a board, by name.</summary>
    private Control? RowOf(string name)
    {
        var at = _mail.Boards().ToList().FindIndex(b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? null : _list.Children.ElementAtOrDefault(at);
    }

    /// <summary>A row's name box, or its purpose box — the two are edited where they are read.</summary>
    private TextBox? Box(string name, bool first)
    {
        var boxes = RowOf(name)?.GetVisualDescendants().OfType<TextBox>().ToList() ?? [];
        return first ? boxes.FirstOrDefault() : boxes.ElementAtOrDefault(1);
    }

    private Control Layout(Button close)
    {
        var heading = Label("Boards", bold: true, size: 15);

        var explain = Label(
            "A board is a collection you save articles into — and unlike a heading, anything with "
            + "an address can go on one, not only what you are subscribed to. An article stays in "
            + "its feed when you save it, and can be on as many boards as you like.");
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 4, 0, 14);
        Bind(explain, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        _name.PlaceholderText = "Board name";
        _name.MinWidth = 180;
        _name.TextChanged += (_, _) => Validate();
        _name.KeyDown += (_, e) =>
        {
            if (e.Key is not Key.Enter || !_add.IsEnabled) return;
            e.Handled = true;
            Commit();
        };

        _purpose.PlaceholderText = "What it is for (optional)";

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_name, 0);
        row.Children.Add(_name);
        Grid.SetColumn(_purpose, 1);
        row.Children.Add(_purpose);
        Grid.SetColumn(_add, 2);
        row.Children.Add(_add);

        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 8, 0, 0);
        Bind(_note, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var caveat = Label(
            "Removing a board does not delete what is on it — the articles stay where they are, "
            + "and only the collection goes.");
        caveat.TextWrapping = TextWrapping.Wrap;
        caveat.FontSize = 11;
        caveat.Margin = new Thickness(0, 14, 0, 0);
        Bind(caveat, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var top = new StackPanel { Children = { heading, explain, row, _note } };
        DockPanel.SetDock(top, Dock.Top);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { close },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(caveat, Dock.Bottom);

        var scroll = new ScrollViewer
        {
            Content = _list,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        return new DockPanel
        {
            Margin = new Thickness(18),
            Children = { top, buttons, caveat, scroll },
        };
    }

    private void Validate()
    {
        var typed = _name.Text?.Trim() ?? string.Empty;

        if (typed.Length == 0)
        {
            _add.IsEnabled = false;
            _note.Text = string.Empty;
            return;
        }

        if (_mail.BoardNamed(typed) is not null)
        {
            _add.IsEnabled = false;
            _note.Text = $"There is already a board called “{typed}”.";
            return;
        }

        _add.IsEnabled = true;
        _note.Text = string.Empty;
    }

    private void Commit()
    {
        var typed = _name.Text?.Trim() ?? string.Empty;
        if (typed.Length == 0 || _mail.BoardNamed(typed) is not null) return;

        var board = _mail.AddBoard(typed, _now, _purpose.Text?.Trim() ?? string.Empty);
        Changed = true;
        Log.Info($"Boards: “{board.Name}” created.");

        Made?.Invoke(board);

        _name.Text = string.Empty;
        _purpose.Text = string.Empty;
        Fill();
        _name.Focus();
    }

    private void Fill()
    {
        _list.Children.Clear();

        var boards = _mail.Boards();
        if (boards.Count == 0)
        {
            var empty = Label("No boards yet. Make one above, then save an article onto it.");
            empty.Margin = new Thickness(0, 8, 0, 0);
            Bind(empty, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            _list.Children.Add(empty);
            return;
        }

        foreach (var board in boards) _list.Children.Add(Row(board));
    }

    private Control Row(Board board)
    {
        // The name is edited where it is read. A rename is the commonest thing done to a board
        // and a dialog inside a dialog for one word is a dialog too many.
        var name = new TextBox
        {
            Text = board.Name,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontWeight = FontWeight.SemiBold,
        };
        Bind(name, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");

        name.LostFocus += (_, _) => Rename(board, name);
        name.KeyDown += (_, e) =>
        {
            if (e.Key is not Key.Enter) return;
            e.Handled = true;
            Rename(board, name);
        };

        var purpose = new TextBox
        {
            Text = board.Description,
            PlaceholderText = "What it is for",
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Bind(purpose, TemplatedControl.ForegroundProperty, "dialog.foreground.subtle.brush");
        purpose.LostFocus += (_, _) =>
        {
            var written = purpose.Text?.Trim() ?? string.Empty;
            if (written == board.Description) return;

            _mail.DescribeBoard(board.Id, written);
            Changed = true;
        };

        var count = Label(board.Count == 1 ? "1 article" : $"{board.Count} articles");
        count.FontSize = 11;
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(10, 0, 6, 0);
        Bind(count, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var remove = new Button
        {
            Classes = { "flat" },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontFamily = IconFont.Family,
            Content = IconGlyphs.GetOrEmpty("delete", 16),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, "Remove this board. What is on it stays where it is.");
        remove.Click += (_, _) =>
        {
            _mail.DeleteBoard(board.Id);
            Changed = true;
            Log.Info($"Boards: “{board.Name}” removed; its {board.Count} article(s) kept.");

            // A name that clashed a moment ago may not clash any more, and the note saying so
            // would otherwise sit under the box refusing a name nothing is using.
            Fill();
            Validate();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var text = new StackPanel { Children = { name, purpose } };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        var row = new Border { Padding = new Thickness(10, 8, 6, 8), Child = grid };
        row[!BackgroundProperty] = new DynamicResourceExtension("list.row.hover.brush");
        return row;
    }

    /// <summary>
    /// Puts a typed name on a board, or puts the old one back.
    /// </summary>
    /// <remarks>
    /// The store refuses a name another board already has — the column is unique — and a box that
    /// silently keeps showing a name that was not saved is the exact shape of a change a reader
    /// thinks they made.
    /// </remarks>
    private void Rename(Board board, TextBox box)
    {
        var typed = box.Text?.Trim() ?? string.Empty;
        if (typed.Length == 0 || typed == board.Name)
        {
            box.Text = board.Name;
            return;
        }

        if (!_mail.RenameBoard(board.Id, typed))
        {
            box.Text = board.Name;
            _note.Text = $"There is already a board called “{typed}”.";
            return;
        }

        Changed = true;
        Log.Info($"Boards: “{board.Name}” renamed to “{typed}”.");
        Fill();
    }

    // ---- Small helpers ------------------------------------------------------------------------

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <summary>A line of the dialog's text, in the dialog's own ink — not the content ink.</summary>
    private static TextBlock Label(string text, bool bold = false, double size = 12)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
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
