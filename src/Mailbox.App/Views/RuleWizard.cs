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
    private readonly Button _back = SystemDialogKit.PushButton("< Back", () => { }, 84);
    private readonly Button _next = SystemDialogKit.PushButton("Next >", () => { }, 84);
    private readonly Button _finish = SystemDialogKit.PushButton("Finish", () => { }, 84);

    /// <summary>
    /// The line above Step 1, which is the only thing that says a rule need not start from a
    /// template at all.
    /// </summary>
    private readonly TextBlock _intro = new()
    {
        Text = "Start from a template or from a blank rule",
        Margin = new Thickness(0, 0, 0, 6),
    };

    /// <summary>
    /// The reference's example under Step 2, in the same place and the same weight.
    /// </summary>
    /// <remarks>
    /// It reads as decoration and is not: a description written in slots — "from people or public
    /// group / move it to the specified folder" — is hard to picture until one is shown filled
    /// in, and this is the only filled-in one anywhere in the wizard.
    /// </remarks>
    private readonly TextBlock _example = new()
    {
        Text = "Example: Move mail from my manager to my High Importance folder",
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 8, 0, 0),
        IsVisible = false,
    };
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

        _description.UseSystemPalette();
        _description.ValueClicked += async (_, index) => await EditClauseAsync(index);

        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => Go(_step + 1);
        _finish.Click += (_, _) => Finish();

        var cancel = SystemDialogKit.PushButton("Cancel", Close, 84);
        cancel.IsCancel = true;

        Bind(_heading, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        Bind(_intro, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        Bind(_example, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

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

        SystemDialogChrome.Apply(this, body);
        Go(_step);
    }

    // ---- Pages -----------------------------------------------------------------------------

    private void Go(int step)
    {
        _step = Math.Clamp(step, 0, 4);

        // The page that is leaving lets go of its children first. Every page is built by
        // <see cref="Page"/> out of the same four instance controls — the intro, the heading, the
        // description and the example — and Avalonia refuses a control two visual parents, so
        // building the next grid while the last one still holds them throws and the wizard never
        // moves off the page it opened on.
        if (_page.Content is Panel leaving) leaving.Children.Clear();

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

        // Finish is live from the first page, as the reference's is — a template is a whole rule
        // and somebody who wants the default of it should not have to walk three pages to say so.
        // What stops an unfinishable rule is Finish itself, which names the clause with no value
        // and the rule with no action rather than being greyed with nothing said.
        _finish.IsEnabled = true;
        _description.Show(_rule);
    }

    /// <summary>The templates the first page offers, in the reference's groups.</summary>
    private static readonly (string Group, string Icon, string Label, Func<MailRule> Make)[] Templates =
    [
        ("Stay Organized", "move-to-folder", "Move messages from someone to a folder", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder), new RuleAction(RuleActionKind.StopProcessing)],
        }),
        ("Stay Organized", "move-to-folder", "Move messages with specific words in the subject to a folder", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.SubjectContains)],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder), new RuleAction(RuleActionKind.StopProcessing)],
        }),
        ("Stay Organized", "move-to-folder", "Move messages sent to a public group to a folder", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.SentTo)],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder), new RuleAction(RuleActionKind.StopProcessing)],
        }),
        ("Stay Organized", "flag", "Flag messages from someone for follow-up", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.FlagForFollowUp) { Level = 0 }],
        }),
        ("Stay Organized", "move-to-folder", "Move RSS items from a specific RSS Feed to a folder", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.FromFeed)],
            Actions = [new RuleAction(RuleActionKind.MoveToFolder), new RuleAction(RuleActionKind.StopProcessing)],
        }),
        ("Stay Up to Date", "alert-star", "Display mail from someone in the New Item Alert Window", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.DisplayAlert)],
        }),
        ("Stay Up to Date", "sound", "Play a sound when I get messages from someone", () => new MailRule
        {
            Conditions = [new RuleCondition(RuleConditionKind.From)],
            Actions = [new RuleAction(RuleActionKind.PlaySound)],
        }),
        // The reference's third entry here — an alert to a mobile device — is absent rather than
        // greyed. It reaches a paging service scope puts out of reach, and a button that cannot do
        // what it says is worse than one that is not there. Same reasoning as Send to OneNote.
        ("Start from a blank rule", "envelope", "Apply rule on messages I receive", () => new MailRule()),
        ("Start from a blank rule", "send", "Apply rule on messages I send", () => new MailRule { AppliesToSent = true }),
    ];

    private Control TemplatePage()
    {
        _heading.Text = "Step 1: Select a template";

        var list = new ListBox { Height = 250 };
        Bind(list, TemplatedControl.BackgroundProperty, "systemdialog.list.background.brush");
        Bind(list, TemplatedControl.BorderBrushProperty, "systemdialog.field.border.brush");

        var items = new List<object>();
        string? group = null;
        foreach (var template in Templates)
        {
            if (template.Group != group)
            {
                group = template.Group;
                var header = new TextBlock { Text = group, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Bind(header, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");

                // The reference runs a hairline from the header's last word to the edge.
                var rule = new Border { Height = 1, Margin = new Thickness(6, 1, 2, 0), VerticalAlignment = VerticalAlignment.Center };
                Bind(rule, Border.BackgroundProperty, "systemdialog.border.brush");

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 6, 0, 2) };
                Grid.SetColumn(header, 0);
                row.Children.Add(header);
                Grid.SetColumn(rule, 1);
                row.Children.Add(rule);
                items.Add(new ListBoxItem { Content = row, IsEnabled = false });
            }

            var label = new TextBlock { Text = template.Label, VerticalAlignment = VerticalAlignment.Center };
            Bind(label, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
            items.Add(new ListBoxItem
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Margin = new Thickness(10, 0, 0, 0),
                    Children = { new ClassicIcon(template.Icon) { VerticalAlignment = VerticalAlignment.Center }, label },
                },
                Tag = template,
            });
        }

        list.ItemsSource = items;
        list.SelectionChanged += (_, _) =>
        {
            if ((list.SelectedItem as ListBoxItem)?.Tag is ValueTuple<string, string, string, Func<MailRule>> chosen)
            {
                _rule = chosen.Item4() with { Name = _rule.Name };
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
            Bind(box, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");
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
            Bind(box, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");
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
        Bind(_runNow, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");
        Bind(_turnOn, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");

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
            Bind(_onServer, TemplatedControl.ForegroundProperty, "systemdialog.foreground.brush");
            Bind(_serverNote, TextBlock.ForegroundProperty, "systemdialog.foreground.disabled.brush");
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
            _ = Confirm.SayAsync(this, "Rules Wizard",
                "Please specify a value for: " + string.Join("; ", incomplete) + ".");
            return;
        }

        if (_rule.Actions.Count == 0)
        {
            _ = Confirm.SayAsync(this, "Rules Wizard", "Please choose at least one action for this rule.");
            return;
        }

        FinishAsync();
    }

    private async void FinishAsync()
    {
        // A handler body in all but signature — and an async void observes its own faults, or
        // they land on the dispatcher instead of in the log.
        try
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Mailbox.Core.Diagnostics.Log.Warn("Finishing the rule failed.", ex);
        }
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
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,180,Auto") };

        // The intro belongs to the template page alone; every later step is a step, not a start.
        _intro.IsVisible = _step == 0;
        Grid.SetRow(_intro, 0);
        grid.Children.Add(_intro);

        Grid.SetRow(_heading, 1);
        _heading.Margin = new Thickness(0, 0, 0, 6);
        grid.Children.Add(_heading);

        Grid.SetRow(top, 2);
        grid.Children.Add(top);

        var label = Label(descriptionHeading);
        label.Margin = new Thickness(0, 10, 0, 6);
        Grid.SetRow(label, 3);
        grid.Children.Add(label);

        Grid.SetRow(_description, 4);
        grid.Children.Add(_description);

        _example.IsVisible = _step == 0;
        Grid.SetRow(_example, 5);
        grid.Children.Add(_example);

        return grid;
    }

    private static Border Boxed(Control content)
    {
        var box = new Border { BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = content } };
        Bind(box, Border.BackgroundProperty, "systemdialog.list.background.brush");
        Bind(box, Border.BorderBrushProperty, "systemdialog.field.border.brush");
        return box;
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Bind(block, TextBlock.ForegroundProperty, "systemdialog.foreground.brush");
        return block;
    }
}
