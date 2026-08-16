using Avalonia.Controls;
using Avalonia.Layout;
using Mailbox.Core.Settings;
using Mailbox.Store;
using static Mailbox.App.Views.ViewDialogKit;

namespace Mailbox.App.Views;

/// <summary>
/// "Set Quick Click", from the foot of the Categorize menu: which category a single click in
/// the list's Categories column applies.
/// </summary>
/// <remarks>
/// The reference's dialog is one sentence, one dropdown and OK/Cancel. "No Category" is offered
/// as the first entry, which is how the quick click is turned off again — the reference words
/// it the same way, and without it there is no way back once one is chosen.
/// </remarks>
public sealed class SetQuickClickCategoryDialog : Window
{
    private readonly ComboBox _choice = new() { Width = 240 };
    private readonly QuickClickSettings _quickClick;

    /// <summary>True when the choice changed.</summary>
    public bool Changed { get; private set; }

    private const string None = "No Category";

    public SetQuickClickCategoryDialog(QuickClickSettings quickClick, IReadOnlyList<Category> categories)
    {
        ArgumentNullException.ThrowIfNull(quickClick);
        ArgumentNullException.ThrowIfNull(categories);
        _quickClick = quickClick;

        var names = new List<string> { None };
        names.AddRange(categories.Select(c => c.Name));
        _choice.ItemsSource = names;
        _choice.SelectedIndex = Math.Max(0, names.FindIndex(
            n => string.Equals(n, quickClick.Category, StringComparison.OrdinalIgnoreCase)));

        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 10,
            Children =
            {
                Label("When single-clicking on the Categories column, add this category:", subtle: true),
                _choice,
                Buttons(Ok(Save), Cancel(this)),
            },
        };

        Title = "Set Quick Click";
        Width = 400;
        Height = 176;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        DialogChrome.Apply(this, body);
    }

    private void Save()
    {
        var chosen = _choice.SelectedItem as string ?? None;
        var value = chosen == None ? string.Empty : chosen;

        Changed = !string.Equals(value, _quickClick.Category, StringComparison.Ordinal);
        _quickClick.Category = value;
        Close();
    }
}

/// <summary>
/// "Set Quick Click", from the foot of the Follow Up menu: which flag a single click in the
/// list's Flag column sets.
/// </summary>
public sealed class SetQuickClickFlagDialog : Window
{
    private readonly ComboBox _choice = new() { Width = 240 };
    private readonly QuickClickSettings _quickClick;

    public bool Changed { get; private set; }

    private static readonly QuickFlag[] Flags =
        [QuickFlag.Today, QuickFlag.Tomorrow, QuickFlag.ThisWeek, QuickFlag.NextWeek, QuickFlag.NoDate, QuickFlag.Complete];

    public SetQuickClickFlagDialog(QuickClickSettings quickClick)
    {
        ArgumentNullException.ThrowIfNull(quickClick);
        _quickClick = quickClick;

        _choice.ItemsSource = Flags.Select(QuickClickSettings.Label).ToList();
        _choice.SelectedIndex = Math.Max(0, Array.IndexOf(Flags, quickClick.Flag));

        var body = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 10,
            Children =
            {
                Label("When single-clicking on the Flag column, set this flag:", subtle: true),
                _choice,
                Buttons(Ok(Save), Cancel(this)),
            },
        };

        Title = "Set Quick Click";
        Width = 400;
        Height = 176;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        DialogChrome.Apply(this, body);
    }

    private void Save()
    {
        var chosen = Flags[Math.Max(0, _choice.SelectedIndex)];
        Changed = chosen != _quickClick.Flag;
        _quickClick.Flag = chosen;
        Close();
    }
}
