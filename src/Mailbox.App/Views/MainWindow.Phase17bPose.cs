using Avalonia.Threading;
using Mailbox.App.Theming;
using Mailbox.App.ViewModels;
using Mailbox.Core.Commands;
using Mailbox.Core.Diagnostics;

namespace Mailbox.App.Views;

/// <summary>
/// The door onto the menus a pointer opens: the ones no ribbon command reaches, walked so every
/// context menu in the tree can be read back through <see cref="MenuProbe"/>.
/// </summary>
/// <remarks>
/// A popup is not a window in the application's window list, so the in-process capture
/// photographs the shell behind it — the one class of surface the audit found with no automated
/// verification at all, and the class that had already shipped two menus presenting nothing.
/// Command-opened menus log themselves the moment they open, because every show site goes
/// through <see cref="MenuProbe"/> now; this door exists for the rest — the flag menu behind a
/// glyph, a contact's right-click, an open message window's chevrons — and for stepping several
/// menus in one run.
/// <para>
/// <c>MAILBOX_MENUS=run:mail.followup;hide;run:mail.move;hide</c>. Steps:
/// <c>run:&lt;command-id&gt;</c> presses a command through the real dispatcher (a command whose
/// answer is a menu logs it); <c>taskflag</c> opens the Tasks module's flag menu over the first
/// row; <c>openmsg</c> opens the selected message's own window; <c>msgwin:&lt;verb&gt;</c> opens
/// that window's menus (<c>delete</c>, <c>move</c>, <c>categorize</c>, <c>followup</c>,
/// <c>more</c>, <c>apps</c>); <c>hide</c> closes whatever menu is open so the next can present;
/// <c>wait</c> is a beat.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Wires this file's door. Called once, from the constructor.</summary>
    private void WirePhase17bDoors()
    {
        if (Environment.GetEnvironmentVariable("MAILBOX_MENUS") is not { Length: > 0 } steps) return;

        var hold = WindowCapture.IsRequested ? WindowCapture.Hold() : null;
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => _ = PoseMenusAsync(steps, hold),
            DispatcherPriority.Background);
    }

    private async Task PoseMenusAsync(string steps, IDisposable? hold)
    {
        try
        {
            if (DataContext is not ShellViewModel shell) return;

            foreach (var step in steps.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = step.IndexOf(':');
                var verb = (colon > 0 ? step[..colon] : step).ToLowerInvariant();
                var arg = colon > 0 ? step[(colon + 1)..].Trim() : string.Empty;

                switch (verb)
                {
                    case "run":
                        RunCommand(new CommandId(arg));
                        break;

                    case "taskselect":
                        var nth = int.TryParse(arg, out var wanted) ? wanted : 0;
                        Log.Info($"Harness: menus — task selected: {EnsureTasks(shell).PoseSelect(nth)}.");
                        break;

                    case "noteselect":
                        Log.Info($"Harness: menus — note selected: {EnsureNotes(shell).PoseSelect(0)}.");
                        break;

                    case "taskflag":
                        var tasks = EnsureTasks(shell);
                        if (tasks.Selected is null) Log.Info($"Harness: menus — task selected: {tasks.PoseSelect(0)}.");
                        ShowTaskFlagMenu(shell);
                        break;

                    case "findrelated":
                        MenuProbe.Show(
                            "the find-related menu", FindRelatedMenu(shell, SelectedRows()),
                            _ribbon ?? (Avalonia.Controls.Control)this, atPointer: true);
                        break;

                    case "quicksteps":
                        MenuProbe.Show(
                            "the quick-steps menu", QuickStepsMenu(shell, SelectedRows()),
                            _ribbon ?? (Avalonia.Controls.Control)this, atPointer: true);
                        break;

                    case "read":
                        // The 15A recipe: both selection properties, so the reading pane loads
                        // the row the way a click would.
                        if (shell.Messages.FirstOrDefault(
                                m => m.Subject.Contains(arg, StringComparison.OrdinalIgnoreCase)) is not { } row)
                        {
                            Log.Info($"Harness: menus — nothing in the list reads “{arg}”.");
                            break;
                        }

                        shell.SelectedMessage = row;
                        shell.SelectedRow = row;
                        break;

                    case "openmsg":
                        RunCommand(MailCommands.OpenItem.Id);
                        break;

                    case "msgwin":
                        if (OwnedWindows.OfType<MessageWindow>().LastOrDefault() is not { } opened)
                        {
                            Log.Info("Harness: menus — no message window is open; pose openmsg first.");
                            break;
                        }

                        switch (arg)
                        {
                            case "delete": opened.PressChevron(MailCommands.Delete.Id); break;
                            case "move": opened.Press(MailCommands.MoveTo.Id); break;
                            case "categorize": opened.Press(MailCommands.Categorize.Id); break;
                            case "followup": opened.Press(MailCommands.FollowUp.Id); break;
                            case "more": opened.PressMore(); break;
                            case "apps": opened.Press(ViewCommands.Apps.Id); break;
                            default:
                                Log.Info($"Harness: menus — “{arg}” is not a message-window menu.");
                                break;
                        }

                        break;

                    case "hide":
                        MenuProbe.Last?.Menu.Hide();
                        break;

                    case "wait":
                        break;

                    default:
                        Log.Info($"Harness: menus — “{step}” is not a step this door knows.");
                        break;
                }

                // Every step gets the beat, so a menu's presenter has laid out — and logged
                // itself — before the next step hides it.
                await Task.Delay(300);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Harness: the menus pose failed.", ex);
        }
        finally
        {
            hold?.Dispose();
        }
    }
}
