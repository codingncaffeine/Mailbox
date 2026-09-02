using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Contacts.Directory;
using static Mailbox.App.Views.SystemDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// One LDAP directory: where it is, who to sign in as, and how far down it to look.
/// </summary>
/// <remarks>
/// A system dialog, like the Account Settings window it opens from — light in every theme,
/// drawn with the desktop's own controls, by the same instruction its siblings are under.
/// <para>
/// <b>Check Settings tests before OK saves.</b> A directory is the one thing here that can be
/// filled in entirely plausibly and still be wrong in four different ways — the host, the port,
/// the search base, the credentials — and each of those failures looks the same from the address
/// book afterwards: nobody found. So the dialog binds and searches on the spot and says which of
/// the four it was, and it does that with the password still in the box rather than after it has
/// gone to the keyring.
/// </para>
/// </remarks>
public sealed class DirectoryDialog : Window
{
    private readonly TextBox _name = Field();
    private readonly TextBox _host = Field("ldap.example.com");
    private readonly TextBox _port = Field();
    private readonly TextBox _baseDn = Field("ou=people,dc=example,dc=com");
    private readonly TextBox _bindDn = Field("Leave empty to connect anonymously");
    private readonly TextBox _password = new() { PasswordChar = '●' };
    private readonly TextBox _max = Field();
    private readonly CheckBox _tls = Tick("Encrypt the connection", isChecked: true);
    private readonly CheckBox _subtree = Tick("Search everything below the base", isChecked: true);
    private readonly TextBlock _message = Label(string.Empty);
    private readonly Func<string, bool> _taken;

    /// <summary>What was entered, or null when the window was cancelled.</summary>
    public LdapDirectory? Result { get; private set; }

    /// <summary>The password as typed, for the caller to file in the keyring. Null when unchanged.</summary>
    public string? Password { get; private set; }

    /// <param name="directory">What to edit, or a blank one for a new directory.</param>
    /// <param name="password">The password already stored, so an edit need not retype it.</param>
    /// <param name="taken">Whether a name is already in use by another directory.</param>
    public DirectoryDialog(LdapDirectory directory, string? password, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _taken = taken ?? throw new ArgumentNullException(nameof(taken));

        Title = directory.Name.Length > 0 ? "Change Directory" : "New Directory";
        Width = 468;
        Height = 396;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = directory.Name;
        _host.Text = directory.Host;
        _port.Text = directory.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _baseDn.Text = directory.BaseDn;
        _bindDn.Text = directory.BindDn;
        _password.Text = password ?? string.Empty;
        _max.Text = directory.MaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _tls.IsChecked = directory.UseTls;
        _subtree.IsChecked = directory.Scope == DirectoryScope.Subtree;

        SystemDialogKit.Bind(_password, TextBox.ForegroundProperty, "systemdialog.foreground.brush");
        SystemDialogKit.Bind(_password, TextBox.BackgroundProperty, "systemdialog.field.brush");
        Avalonia.Automation.AutomationProperties.SetName(_password, "Password");

        _message.Margin = new Thickness(12, 6, 12, 0);
        _message.TextWrapping = Avalonia.Media.TextWrapping.Wrap;

        var grid = new Grid
        {
            Margin = new Thickness(12, 12, 12, 0),
            ColumnDefinitions = new ColumnDefinitions("128,*,54"),
            RowDefinitions = new RowDefinitions("28,28,28,28,28"),
        };

        Row(grid, 0, "Name:", _name, "Name");
        Row(grid, 1, "Server:", _host, "Server");
        Row(grid, 2, "Search base:", _baseDn, "Search base");
        Row(grid, 3, "User name:", _bindDn, "User name");
        Row(grid, 4, "Password:", _password, "Password");

        // Port sits beside the server, which is where somebody reads it — and it is the field
        // that decides whether "encrypted" means LDAPS or StartTLS.
        var portLabel = Label("Port:");
        portLabel.VerticalAlignment = VerticalAlignment.Center;
        portLabel.Margin = new Thickness(8, 0, 4, 0);
        var portRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { portLabel, _port },
        };
        _port.Width = 54;
        Avalonia.Automation.AutomationProperties.SetName(_port, "Port");
        Grid.SetRow(portRow, 1);
        Grid.SetColumn(portRow, 2);
        grid.Children.Add(portRow);

        var maxRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { Label("Show at most"), _max, Label("people") },
        };
        _max.Width = 54;
        Avalonia.Automation.AutomationProperties.SetName(_max, "Show at most");

        var options = new StackPanel
        {
            Spacing = 6,
            Children = { _tls, _subtree, maxRow },
        };

        var check = PushButton("Check Settings", CheckAsync, 118);
        var ok = PushButton("OK", Save);
        ok.IsDefault = true;
        var cancel = PushButton("Cancel", Close);
        cancel.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 7, 9),
            Children = { check, ok, cancel },
        };

        var stack = new StackPanel
        {
            Children = { grid, GroupBox("Searching", options, top: 8), _message },
        };

        SystemDialogChrome.Apply(this, new Panel { Children = { stack, buttons } });

        _name.AttachedToVisualTree += (_, _) => _name.Focus();
    }

    private static void Row(Grid grid, int row, string label, TextBox field, string automationName)
    {
        var caption = Label(label);
        caption.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);

        field.VerticalAlignment = VerticalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetName(field, automationName);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);

        // Everything but the server line spans the port column too.
        if (row != 1) Grid.SetColumnSpan(field, 2);
        grid.Children.Add(field);
    }

    /// <summary>What the boxes say, or null with <see cref="_message"/> filled in.</summary>
    private LdapDirectory? Validate()
    {
        var name = (_name.Text ?? string.Empty).Trim();
        if (name.Length == 0) return Fail("A directory needs a name.", _name);
        if (_taken(name)) return Fail($"There is already a directory called “{name}”.", _name);

        var host = (_host.Text ?? string.Empty).Trim();
        if (host.Length == 0) return Fail("A directory needs a server to ask.", _host);

        if (!int.TryParse(_port.Text, System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return Fail("The port must be a number between 1 and 65535.", _port);
        }

        var baseDn = (_baseDn.Text ?? string.Empty).Trim();
        if (baseDn.Length == 0)
        {
            // Not optional, and it is worth saying why rather than just refusing: a search with
            // no base searches the whole server, which every real directory forbids outright.
            return Fail(
                "A directory needs a search base — where in its tree the people are, "
                + "like ou=people,dc=example,dc=com.",
                _baseDn);
        }

        if (!int.TryParse(_max.Text, System.Globalization.CultureInfo.InvariantCulture, out var max) || max < 1)
        {
            return Fail("Show at most must be a number of people, one or more.", _max);
        }

        Say(string.Empty, wrong: false);

        return new LdapDirectory
        {
            Name = name,
            Host = host,
            Port = port,
            UseTls = _tls.IsChecked == true,
            BaseDn = baseDn,
            BindDn = (_bindDn.Text ?? string.Empty).Trim(),
            Scope = _subtree.IsChecked == true ? DirectoryScope.Subtree : DirectoryScope.OneLevel,
            MaxResults = max,
        };
    }

    /// <summary>
    /// Binds and searches, on the spot, and says what happened.
    /// </summary>
    /// <remarks>
    /// Off the interface thread: a directory that has stopped answering takes the timeout to say
    /// so, and a window frozen for eight seconds is one somebody force-quits.
    /// </remarks>
    private async Task CheckAsync()
    {
        if (Validate() is not { } directory) return;

        if (!LdapDirectorySearch.IsAvailable)
        {
            Say(LdapDirectorySearch.Missing, wrong: true);
            return;
        }

        var password = _password.Text ?? string.Empty;
        Say("Checking…", wrong: false);

        var refusal = await Task.Run(() => LdapDirectorySearch.Check(directory, password));

        Say(
            refusal.Length == 0 ? "The directory answered." : refusal,
            wrong: refusal.Length > 0);
    }

    private void Save()
    {
        if (Validate() is not { } directory) return;

        Result = directory;
        Password = _password.Text ?? string.Empty;
        Close();
    }

    private LdapDirectory? Fail(string message, TextBox field)
    {
        Say(message, wrong: true);
        field.Focus();
        return null;
    }

    /// <summary>
    /// One line under the fields, red for a refusal and ordinary ink for anything else — so
    /// "the directory answered" does not read as a warning.
    /// </summary>
    private void Say(string message, bool wrong)
    {
        _message.Text = message;
        SystemDialogKit.Bind(
            _message,
            TextBlock.ForegroundProperty,
            wrong ? "systemdialog.icon.red.brush" : "systemdialog.foreground.brush");
    }

    /// <summary>
    /// One posed step against this window, for the harness — which cannot type into a modal.
    /// <c>name=…</c>, <c>host=…</c>, <c>port=…</c>, <c>base=…</c>, <c>user=…</c>,
    /// <c>password=…</c>, <c>max=…</c>, <c>tls=on|off</c>, <c>subtree=on|off</c>, then
    /// <c>check</c>, <c>ok</c> or <c>cancel</c>. Answers what it did, so a step that named
    /// nothing is not read as a step that worked.
    /// </summary>
    internal async Task<string> PoseAsync(string step)
    {
        var text = (step ?? string.Empty).Trim();
        if (text.Length == 0) return "nothing to do";

        var equals = text.IndexOf('=', StringComparison.Ordinal);
        var verb = (equals > 0 ? text[..equals] : text).Trim().ToLowerInvariant();
        var value = equals > 0 ? text[(equals + 1)..].Trim() : string.Empty;
        var on = value.Length == 0 || value is "1" or "on" or "true" or "yes";

        switch (verb)
        {
            case "name": _name.Text = value; return $"name box says “{_name.Text}”";
            case "host": _host.Text = value; return $"server box says “{_host.Text}”";
            case "port": _port.Text = value; return $"port box says “{_port.Text}”";
            case "base": _baseDn.Text = value; return $"search base box says “{_baseDn.Text}”";
            case "user": _bindDn.Text = value; return $"user name box says “{_bindDn.Text}”";
            case "password": _password.Text = value; return $"password box holds {value.Length} character(s)";
            case "max": _max.Text = value; return $"show at most says “{_max.Text}”";
            case "tls": _tls.IsChecked = on; return $"encryption is {(on ? "ticked" : "clear")}";
            case "subtree": _subtree.IsChecked = on; return $"subtree is {(on ? "ticked" : "clear")}";

            case "check":
                await CheckAsync();
                return $"Check Settings says “{_message.Text}”";

            case "ok":
                Save();
                return Result is null
                    ? $"OK refused: “{_message.Text}”"
                    : $"OK saved “{Result.Name}” at {Result.Where()}";

            case "cancel": Close(); return "Cancel pressed";
            default: return $"“{verb}” is not a field or a button on this window";
        }
    }
}
