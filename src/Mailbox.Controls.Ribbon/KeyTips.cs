using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Mailbox.Controls.Ribbon;

/// <summary>One thing Alt can reach, and what its letters do.</summary>
public sealed record KeyTipTarget
{
    /// <summary>1–3 characters, per the published ribbon framework spec.</summary>
    public required string Tip { get; init; }

    /// <summary>The control the badge is pinned over.</summary>
    public required Control Target { get; init; }

    public required Action Activate { get; init; }

    /// <summary>
    /// Set for a tab. Activating replaces the badges with these rather than dismissing them,
    /// which is the spec's two-level traversal: Alt reveals the tabs, picking one reveals its
    /// controls.
    /// </summary>
    /// <remarks>
    /// Deferred rather than a list, because a tab's controls do not exist until it is the active
    /// tab — selecting it is what builds them.
    /// </remarks>
    public Func<IReadOnlyList<KeyTipTarget>>? Children { get; init; }
}

/// <summary>The little bordered box showing a command's KeyTip letters.</summary>
internal sealed class KeyTipBadge : Border
{
    internal KeyTipBadge(string tip)
    {
        var label = new TextBlock
        {
            Text = tip,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.primary.brush");
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("type.ui.size.small.value");

        Child = label;
        Padding = new Thickness(4, 0);
        MinWidth = 16;
        Height = 16;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(2);

        // Pinned to the bottom of whatever it adorns, hanging just past its edge, so it marks
        // the control without hiding the icon that identifies it.
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, -6);
        IsHitTestVisible = false;

        this[!BackgroundProperty] = new DynamicResourceExtension("surface.overlay.brush");
        this[!BorderBrushProperty] = new DynamicResourceExtension("border.strong.brush");
    }

    /// <summary>Dims a badge whose tip no longer matches what has been typed.</summary>
    internal void SetReachable(bool reachable) => Opacity = reachable ? 1 : 0.35;
}

/// <summary>
/// Alt traversal: the state machine behind the KeyTip badges.
/// </summary>
/// <remarks>
/// Almost every ribbon clone skips this, and power users notice within a minute. It is also the
/// piece that owns the gesture table — a shortcut belongs in the command catalogue so the
/// keyboard editor can rebind it, not in a window's key handler.
/// <para>
/// Badges are adorners rather than a hand-positioned overlay, so each one tracks the control it
/// marks through resizes, ribbon collapse and a tab rebuild without any coordinate arithmetic
/// here.
/// </para>
/// </remarks>
public sealed class KeyTipSession
{
    private readonly List<(AdornerLayer Layer, KeyTipBadge Badge, KeyTipTarget Target)> _shown = [];
    private readonly Stack<IReadOnlyList<KeyTipTarget>> _ancestors = new();

    private IReadOnlyList<KeyTipTarget> _targets = [];
    private string _typed = string.Empty;

    public bool IsActive { get; private set; }

    /// <summary>Raised whenever the session opens or closes, so a host can mark its state.</summary>
    public event EventHandler? ActiveChanged;

    /// <summary>Opens the first level — the tabs, the QAT and the application menu.</summary>
    public void Begin(IReadOnlyList<KeyTipTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) return;

        _ancestors.Clear();
        Show(targets);
        IsActive = true;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public void End()
    {
        if (!IsActive && _shown.Count == 0) return;

        Clear();
        _ancestors.Clear();
        _targets = [];
        _typed = string.Empty;
        IsActive = false;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Feeds a key to the traversal. Returns true when it was consumed, which is the whole of
    /// what the host needs to know — while KeyTips are up, the keyboard belongs to them.
    /// </summary>
    public bool HandleKey(Key key)
    {
        if (!IsActive) return false;

        switch (key)
        {
            // Back a level, then out. Unwinding rather than dismissing is what makes a
            // mistyped tab recoverable without starting over.
            case Key.Escape:
                Ascend();
                return true;

            case Key.LeftAlt or Key.RightAlt or Key.System:
                End();
                return true;
        }

        if (CharacterFor(key) is not { } character) return false;

        _typed += character;

        if (Match(_typed) is { } exact)
        {
            Activate(exact);
            return true;
        }

        // Still a prefix of something reachable — wait for the rest, dimming what it rules out.
        if (_targets.Any(t => t.Tip.StartsWith(_typed, StringComparison.OrdinalIgnoreCase)))
        {
            Refilter();
            return true;
        }

        // Nothing by that name. Drop the buffer rather than the session, so one stray key does
        // not throw the user out of a traversal they are halfway through.
        _typed = string.Empty;
        Refilter();
        return true;
    }

    private KeyTipTarget? Match(string typed)
        => _targets.FirstOrDefault(t => t.Tip.Equals(typed, StringComparison.OrdinalIgnoreCase));

    private void Activate(KeyTipTarget target)
    {
        target.Activate();

        if (target.Children is not { } children)
        {
            End();
            return;
        }

        _ancestors.Push(_targets);
        Clear();

        // Activating a tab rebuilds the ribbon, so its controls have no bounds until the next
        // layout pass. Adorning them before that pins every badge to the top-left corner.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsActive) return;
            Show(children());
        }, DispatcherPriority.Loaded);
    }

    private void Ascend()
    {
        if (_ancestors.Count == 0)
        {
            End();
            return;
        }

        Show(_ancestors.Pop());
    }

    private void Show(IReadOnlyList<KeyTipTarget> targets)
    {
        Clear();
        _targets = targets;
        _typed = string.Empty;

        foreach (var target in targets)
        {
            // A control that is not on screen cannot be marked. This is how commands inside a
            // collapsed group drop out of the traversal at narrow widths.
            if (!target.Target.IsEffectivelyVisible) continue;
            if (AdornerLayer.GetAdornerLayer(target.Target) is not { } layer) continue;

            var badge = new KeyTipBadge(target.Tip);
            AdornerLayer.SetAdornedElement(badge, target.Target);
            layer.Children.Add(badge);
            _shown.Add((layer, badge, target));
        }
    }

    private void Refilter()
    {
        foreach (var (_, badge, target) in _shown)
        {
            badge.SetReachable(
                _typed.Length == 0
                || target.Tip.StartsWith(_typed, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void Clear()
    {
        foreach (var (layer, badge, _) in _shown) layer.Children.Remove(badge);
        _shown.Clear();
    }

    /// <summary>
    /// KeyTips are letters and digits only, so this is a deliberate whitelist rather than a
    /// text-input hook — the traversal must not be steered by a dead key or a compose sequence.
    /// </summary>
    private static string? CharacterFor(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        _ => null,
    };
}
