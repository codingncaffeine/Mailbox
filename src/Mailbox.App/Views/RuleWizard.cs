using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.Core.Rules;
using Mailbox.Store;

namespace Mailbox.App.Views;

/// <summary>
/// The Rules Wizard: a template, then conditions, actions and exceptions, then a name — the
/// reference's five pages, Back and Next between them, Finish at the end.
/// </summary>
/// <remarks>
/// Each page is a checklist of kinds above the rule description, and ticking a kind adds a
/// clause whose value is edited by clicking it in the description, as the reference has it. The
/// rule is built up as a <see cref="MailRule"/> and handed back on Finish; the caller stores it.
/// </remarks>
public sealed class RuleWizard : Window
{
    private readonly MailRepository _mail;
    private readonly long _accountId;
    private readonly ContentControl _page = new();
    private readonly RuleDescriptionView _description = new();
    private readonly Button _back = new() { Content = "< Back", Width = 84 };
    private readonly Button _next = new() { Content = "Next >", Width = 84 };
    private readonly Button _finish = new() { Content = "Finish", Width = 84 };
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold };

    private int _step;
    private MailRule _rule;

    /// <summary>The rule as finished, or null when the wizard was cancelled.</summary>
    public MailRule? Result { get; private set; }

    /// <summary>Whether Finish asked for the rule to be run on the Inbox now.</summary>
    public bool RunNow { get; private set; }

    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new DynamicResourceExtension(key);

    /// <param name="existing">A rule to edit, or null for a new one — which starts at the template page.</param>
    public RuleWizard(MailRepository mail, long accountId, MailRule? existing = null)
    {
        _mail = mail;
        _accountId = accountId;
        _rule = existing ?? new MailRule { Name = string.Empty };
        _step = existing is null ? 0 : 1;

        // The harness: MAILBOX_WIZARD_STEP opens on a page (0–4), with a sample rule to show
        // when there is none to edit — a capture of the finish page has something to finish.
        if (Theming.WindowCapture.IsRequested
            && int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_WIZARD_STEP"), out var posed))
        {
            _step = Math.Clamp(posed, 0, 4);
            if (existing is null && _step > 0)
            {
                _rule = new MailRule
                {
                    Name = "Newsletters",
                    Conditions = [new RuleCondition(RuleConditionKind.From) { Values = ["@example.org"] }],
                    Actions = [new RuleAction(RuleActionKind.MarkAsRead), new RuleAction(RuleActionKind.Delete)],
                };
            }
        }

        Title = "Rules Wizard";
        Width = 620;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _description.ValueClicked += async (_, index) => await EditClauseAsync(index);

        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => Go(_step + 1);
        _finish.Click += (_, _) => Finish();

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        cancel.Click += (_, _) => Close();

        Bind(_heading, TextBlock.ForegroundProperty, "dialog.foreground.brush");

        var body = new DockPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { cancel, _back, _next, _finish },
                },
                _page,
            },
        };

        DialogChrome.Apply(this, body);
        Bind(this, BackgroundProperty, "dialog.background.brush");
        Go(_step);
    }

    // ---- Pages -----------------------------------------------------------------------------

    private void Go(int step)
    {
        _step = Math.Clamp(step, 0, 4);
        _page.Content = _step switch
        {
            0 => TemplatePage(),
            1 => ConditionsPage(),
            2 => ActionsPage(),
            3 => ExceptionsPage(),
            _ => FinishPage(),
        };

        _back.IsEnabled = _step > 0;
        _next.IsEnabled = _step < 4;
        _finish.IsEnabled = _step >= 2;
        _description.Show(_rule);
    }

    /// <summary>The templates the first page offers, in the reference's groups.</summary>
    private static readonly (string Group, string Label, Func<MailRule> Make)[] Templates =
    [
        ("Stay Organized", "Move messages from someone to a folder", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder), new RuleAction(RuleActionKind.StopProcessing)],
        }),
        ("Stay Organized", "Move messages with specific words in the subject to a folder", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.SubjectContains)],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder), new RuleAction(RuleActionKind.StopProcessing)],
        }),
        ("Stay Organized", "Move messages sent to a public group to a folder", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.SentTo)],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder), new RuleAction(RuleActionKind.StopProcessing)],
        }),
        ("Stay Organized", "Flag messages from someone for follow-up", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.FlagForFollowUp) { Level = 0 }],
        }),
        ("Stay Up to Date", "Display mail from someone in the New Item Alert Window", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.DisplayAlert)],
        }),
        ("Stay Up to Date", "Play a sound when I get messages from someone", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.PlaySound)],
        }),
        ("Start from a blank rule", "Apply rule on messages I receive", () => new MailRule()),
    ];

    private Control TemplatePage()
    {
        _heading.Text = "Step 1: Select a template";

        var list = new ListBox { Height = 250 };
        Bind(list, TemplatedControl.BackgroundProperty, "dialog.surface.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "dialog.border.brush");

        var items = new List<object>();
        string? group = null;
        foreach (var template in Templates)
        {
            if (template.Group != group)
            {
                group = template.Group;
                var header = new TextBlock { Text = group, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 6, 0, 2) };
                Bind(header, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
                items.Add(new ListBoxItem { Content = header, IsEnabled = false });
            }

            var label = new TextBlock { Text = template.Label, Margin = new Thickness(16, 1, 0, 1) };
            Bind(label, TextBlock.ForegroundProperty, "dialog.surface.text.brush");
            items.Add(new ListBoxItem { Content = label, Tag = template });
        }

        list.ItemsSource = items;
        list.SelectionChanged += (_, _) =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is ValueTuple<string, string, Func<MailRule>> chosen)
            {
                _rule = chosen.Item3() with { Name = _rule.Name };
                _description.Show(_rule);
            }
        };
        list.SelectedIndex = 1;

        return Page(list, "Step 2: Edit the rule description (click an underlined value)");
    }

    private Control ConditionsPage()
    {
        _heading.Text = "Step 1: Select condition(s)";
        return Page(ConditionChecklist(_rule.Conditions, c => _rule = _rule with { Conditions = c }),
            "Step 2: Edit the rule description (click an underlined value)");
    }

    private Control ExceptionsPage()
    {
        _heading.Text = "Are there any exceptions?  Step 1: Select exception(s) (if necessary)";
        return Page(ConditionChecklist(_rule.Exceptions, e => _rule = _rule with { Exceptions = e }, exceptions: true),
            "Step 2: Edit the rule description (click an underlined value)");
    }

    /// <summary>The kinds the wizard offers as conditions, in the reference's order.</summary>
    private static readonly RuleConditionKind[] ConditionKinds =
    [
        RuleConditionKind.From, RuleConditionKind.SubjectContains, RuleConditionKind.SentTo,
        RuleConditionKind.SubjectOrBodyContains, RuleConditionKind.BodyContains, RuleConditionKind.HeaderContains,
        RuleConditionKind.RecipientAddressContains, RuleConditionKind.SenderAddressContains,
        RuleConditionKind.SentOnlyToMe, RuleConditionKind.MyNameInTo, RuleConditionKind.MyNameInCc,
        RuleConditionKind.MyNameInToOrCc, RuleConditionKind.MyNameNotInTo, RuleConditionKind.Importance,
        RuleConditionKind.Sensitivity, RuleConditionKind.HasAttachment, RuleConditionKind.SizeBetween,
        RuleConditionKind.ReceivedBetween, RuleConditionKind.AssignedToCategory, RuleConditionKind.Flagged,
    ];

    /// <summary>The kinds the wizard offers as actions, in the reference's order.</summary>
    private static readonly RuleActionKind[] ActionKinds =
    [
        RuleActionKind.MoveToFolder, RuleActionKind.AssignCategory, RuleActionKind.Delete, RuleActionKind.PermanentlyDelete,
        RuleActionKind.CopyToFolder, RuleActionKind.ForwardTo, RuleActionKind.ForwardAsAttachmentTo, RuleActionKind.RedirectTo,
        RuleActionKind.FlagForFollowUp, RuleActionKind.ClearFlag, RuleActionKind.ClearCategories, RuleActionKind.MarkAsRead,
        RuleActionKind.DisplayAlert, RuleActionKind.DesktopAlert, RuleActionKind.PlaySound, RuleActionKind.StopProcessing,
    ];

    private Control ConditionChecklist(IReadOnlyList<RuleCondition> current, Action<IReadOnlyList<RuleCondition>> set, bool exceptions = false)
    {
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6, 4) };
        foreach (var kind in ConditionKinds)
        {
            var box = new CheckBox
            {
                Content = (exceptions ? "except if " : string.Empty) + RuleDescription.Template(kind),
                IsChecked = current.Any(c => c.Kind == kind),
            };
            Bind(box, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");
            box.IsCheckedChanged += async (_, _) =>
            {
                var list = (exceptions ? _rule.Exceptions : _rule.Conditions).ToList();
                if (box.IsChecked == true)
                {
                    if (list.All(c => c.Kind != kind))
                    {
                        var added = new RuleCondition(kind);
                        list.Add(added);
                        set(list);
                        _description.Show(_rule);

                        // A kind that needs a value asks for it at once, as the reference does
                        // on the click of the underlined placeholder — one step fewer.
                        if (RuleValues.NeedsValue(kind) && await RuleValues.EditAsync(this, added, _mail) is { } edited)
                        {
                            list[list.IndexOf(added)] = edited;
                            set(list);
                        }
                    }
                }
                else
                {
                    list.RemoveAll(c => c.Kind == kind);
                    set(list);
                }

                _description.Show(_rule);
            };
            rows.Children.Add(box);
        }

        return Boxed(rows);
    }

    private Control ActionsPage()
    {
        _heading.Text = "What do you want to do with the message?  Step 1: Select action(s)";

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6, 4) };
        foreach (var kind in ActionKinds)
        {
            var box = new CheckBox
            {
                Content = RuleDescription.Template(kind),
                IsChecked = _rule.Actions.Any(a => a.Kind == kind),
            };
            Bind(box, TemplatedControl.ForegroundProperty, "dialog.surface.text.brush");
            box.IsCheckedChanged += async (_, _) =>
            {
                var list = _rule.Actions.ToList();
                if (box.IsChecked == true)
                {
                    if (list.All(a => a.Kind != kind))
                    {
                        var added = new RuleAction(kind);
                        // Stop processing stays last, whatever order the boxes were ticked in.
                        var stop = list.FindIndex(a => a.Kind == RuleActionKind.StopProcessing);
                        if (kind != RuleActionKind.StopProcessing && stop >= 0) list.Insert(stop, added);
                        else list.Add(added);
                        _rule = _rule with { Actions = list };
                        _description.Show(_rule);

                        if (RuleValues.NeedsValue(kind) && await RuleValues.EditAsync(this, added, _mail, _accountId) is { } edited)
                        {
                            list[list.IndexOf(added)] = edited;
                            _rule = _rule with { Actions = list };
                        }
                    }
                }
                else
                {
                    list.RemoveAll(a => a.Kind == kind);
                    _rule = _rule with { Actions = list };
                }

                _description.Show(_rule);
            };
            rows.Children.Add(box);
        }

        return Page(Boxed(rows), "Step 2: Edit the rule description (click an underlined value)");
    }

    private TextBox? _name;
    private CheckBox? _runNow;
    private CheckBox? _turnOn;
    private CheckBox? _onServer;
    private TextBlock? _serverNote;

    private Control FinishPage()
    {
        _heading.Text = "Finish rule setup.";

        _name = new TextBox { Text = _rule.Name.Length > 0 ? _rule.Name : SuggestedName(), Width = 360 };
        _runNow = new CheckBox { Content = "Run this rule now on messages already in \"Inbox\"" };
        _turnOn = new CheckBox { Content = "Turn on this rule", IsChecked = _rule.Enabled };
        Bind(_runNow, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
        Bind(_turnOn, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Label("Step 1: Specify a name for this rule"));
        stack.Children.Add(_name);
        stack.Children.Add(new Panel { Height = 6 });
        stack.Children.Add(Label("Step 2: Setup rule options"));
        stack.Children.Add(_runNow);
        stack.Children.Add(_turnOn);

        // Server-side rules: an IMAP account whose server speaks ManageSieve can run the rule
        // itself, so it works while Mailbox is closed. The checkbox asks the server what it can
        // do the first time, and says — in the rule's own words — when a rule has to stay here.
        if (App.Accounts.Find(_mail.OwnAddress() ?? string.Empty) is { } account && SieveSync.Supports(account))
        {
            _onServer = new CheckBox { Content = "Run this rule on the mail server, so it works while Mailbox is closed", IsChecked = _rule.ServerSide };
            _serverNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, -2, 0, 0), MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left };
            Bind(_onServer, TemplatedControl.ForegroundProperty, "dialog.foreground.brush");
            Bind(_serverNote, TextBlock.ForegroundProperty, "dialog.foreground.subtle.brush");
            _onServer.IsCheckedChanged += async (_, _) => await ServerCheckedAsync(account);
            stack.Children.Add(_onServer);
            stack.Children.Add(_serverNote);
            _ = ServerCheckedAsync(account, quiet: true);
        }

        return Page(stack, "Step 3: Review rule description (click an underlined value to edit)");
    }

    private bool _probing;

    /// <summary>
    /// The server checkbox's answer: with the server's abilities known (asked once and
    /// remembered), whether this rule compiles — and why not, when it does not. Ticking it for
    /// the first time asks the server; a server that cannot be reached is said so, and the box
    /// clears.
    /// </summary>
    private async Task ServerCheckedAsync(OpenAccount account, bool quiet = false)
    {
        if (_onServer is null || _serverNote is null || _probing) return;

        var extensions = SieveSync.KnownExtensions(account);
        if (extensions is null)
        {
            if (_onServer.IsChecked != true)
            {
                _serverNote.Text = quiet ? string.Empty : "The server will be asked what it can do when this is ticked.";
                return;
            }

            _probing = true;
            _serverNote.Text = "Asking the server what it can do…";
            try
            {
                extensions = await SieveSync.ProbeAsync(account);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _serverNote.Text = $"The server could not be reached for rules ({ex.Message}). This rule runs on this computer.";
                _onServer.IsChecked = false;
                return;
            }
            finally
            {
                _probing = false;
            }
        }

        var compiled = SieveCompiler.Compile(_rule, SieveSync.ContextFor(account, extensions));
        if (compiled.Compiles)
        {
            _serverNote.Text = _onServer.IsChecked == true
                ? "The server will run this rule as mail arrives."
                : "The server can run this rule.";
            return;
        }

        _serverNote.Text = "This rule stays on this computer: " + string.Join("; ", compiled.Reasons) + ".";
        if (_onServer.IsChecked == true) _onServer.IsChecked = false;
    }

    /// <summary>A name from the first condition's value, as the reference suggests one.</summary>
    private string SuggestedName()
    {
        var first = _rule.Conditions.FirstOrDefault(c => c.Values.Count > 0);
        return first?.Values[0] ?? "New rule";
    }

    private void Finish()
    {
        if (_step == 4 && _name is not null)
        {
            _rule = _rule with
            {
                Name = _name.Text?.Trim() is { Length: > 0 } typed ? typed : SuggestedName(),
                Enabled = _turnOn?.IsChecked != false,
                ServerSide = _onServer?.IsChecked == true,
            };
            RunNow = _runNow?.IsChecked == true;
        }
        else if (_rule.Name.Length == 0)
        {
            _rule = _rule with { Name = SuggestedName() };
        }

        // The reference warns twice: a rule with no conditions applies to every message, and a
        // condition or action left without its value cannot run. Neither is a reason to refuse,
        // but the reader is told rather than left to find out.
        var incomplete = _rule.Conditions.Where(c => !RuleValues.IsComplete(c)).Select(c => RuleDescription.Template(c.Kind))
            .Concat(_rule.Actions.Where(a => !RuleValues.IsComplete(a)).Select(a => RuleDescription.Template(a.Kind)))
            .Concat(_rule.Exceptions.Where(e => !RuleValues.IsComplete(e)).Select(e => "except if " + RuleDescription.Template(e.Kind)))
            .ToList();

        if (incomplete.Count > 0)
        {
            _ = Confirm.AskAsync(this, "Rules Wizard",
                "Please specify a value for: " + string.Join("; ", incomplete) + ".", "OK", destructive: false);
            return;
        }

        if (_rule.Actions.Count == 0)
        {
            _ = Confirm.AskAsync(this, "Rules Wizard", "Please choose at least one action for this rule.", "OK", destructive: false);
            return;
        }

        FinishAsync();
    }

    private async void FinishAsync()
    {
        if (_rule.Conditions.Count == 0)
        {
            var go = await Confirm.AskAsync(this, "Rules Wizard",
                "This rule will be applied to every message you receive. Is this correct?", "Yes", destructive: false);
            if (!go) return;
        }

        Result = _rule;
        Close();
    }

    // ---- Editing a clause --------------------------------------------------------------------

    /// <summary>
    /// A click on an underlined value: the clause index maps onto the conditions, then the
    /// actions, then the exceptions, in the order the description lists them.
    /// </summary>
    private async Task EditClauseAsync(int index)
    {
        var i = index - 1;
        if (i < 0) return;

        if (i < _rule.Conditions.Count)
        {
            if (await RuleValues.EditAsync(this, _rule.Conditions[i], _mail) is { } edited)
            {
                var list = _rule.Conditions.ToList();
                list[i] = edited;
                _rule = _rule with { Conditions = list };
            }
        }
        else if ((i -= _rule.Conditions.Count) < _rule.Actions.Count)
        {
            if (await RuleValues.EditAsync(this, _rule.Actions[i], _mail, _accountId) is { } edited)
            {
                var list = _rule.Actions.ToList();
                list[i] = edited;
                _rule = _rule with { Actions = list };
            }
        }
        else if ((i -= _rule.Actions.Count) < _rule.Exceptions.Count)
        {
            if (await RuleValues.EditAsync(this, _rule.Exceptions[i], _mail) is { } edited)
            {
                var list = _rule.Exceptions.ToList();
                list[i] = edited;
                _rule = _rule with { Exceptions = list };
            }
        }

        _description.Show(_rule);
    }

    // ---- Building blocks -------------------------------------------------------------------

    private Control Page(Control top, string descriptionHeading)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,220") };

        Grid.SetRow(_heading, 0);
        _heading.Margin = new Thickness(0, 0, 0, 6);
        grid.Children.Add(_heading);

        Grid.SetRow(top, 1);
        grid.Children.Add(top);

        var label = Label(descriptionHeading);
        label.Margin = new Thickness(0, 10, 0, 6);
        Grid.SetRow(label, 2);
        grid.Children.Add(label);

        Grid.SetRow(_description, 3);
        grid.Children.Add(_description);

        return grid;
    }

    private static Border Boxed(Control content)
    {
        var box = new Border { BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = content } };
        Bind(box, Border.BackgroundProperty, "dialog.surface.brush");
        Bind(box, Border.BorderBrushProperty, "dialog.border.brush");
        return box;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Bind(block, TextBlock.ForegroundProperty, "dialog.foreground.brush");
        return block;
    }
}
