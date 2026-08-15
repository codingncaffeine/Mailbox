using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Diagnostics;
using Mailbox.Protocols;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// Replaces the stored password for an account.
/// </summary>
/// <remarks>
/// Its own dialog rather than a field in Server Settings, because it is the thing people come
/// back for: a provider expires an app password and every other setting is still correct. One
/// field and a button beats hunting through a form of eight.
/// </remarks>
public sealed class UpdatePasswordDialog : Window
{
    private readonly Account _account;
    private readonly TextBox _password = new() { PasswordChar = '•', Width = 300 };
    private readonly CheckBox _sameForSending = new()
    {
        Content = "Use this for sending as well",
        IsChecked = true,
    };

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };

    public bool Saved { get; private set; }

    public UpdatePasswordDialog(Account account)
    {
        _account = account;
        Title = "Update Password";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += async (_, _) => await SaveAsync();

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var heading = new TextBlock { Text = _account.Address, FontWeight = FontWeight.SemiBold };
        Bind(heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var where = new TextBlock
        {
            Text = $"The password is kept in {App.Secrets.Description}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 14),
            MaxWidth = 380,
        };
        Bind(where, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
        Bind(_status, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");

        var body = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                heading, where,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { Caption("Password"), _password },
                },
                new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { _sameForSending } },
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0),
                    Children = { cancel, save },
                },
            },
        };

        DialogChrome.Apply(this, body);

        Bind(this, BackgroundProperty, "surface.ground.brush");
    }

    private TextBlock Caption(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Width = 80,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }

    private async Task SaveAsync()
    {
        var password = _password.Text ?? string.Empty;
        if (password.Length == 0)
        {
            _status.Text = "Type the new password first.";
            return;
        }

        var ok = await App.Secrets.SaveAsync(_account.Address, Credentials.Incoming, password);

        if (_sameForSending.IsChecked == true)
        {
            await App.Secrets.SaveAsync(_account.Address, Credentials.Outgoing, password);
        }
        else
        {
            // Otherwise the old sending password would silently keep being used.
            await App.Secrets.DeleteAsync(_account.Address, Credentials.Outgoing);
        }

        Log.Info($"Password updated for {_account.Address}.");
        Saved = true;

        if (!ok)
        {
            _status.Text = $"Saved for this session only — {App.Secrets.Description}.";
            return;
        }

        Close();
    }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);
}
