using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Mailbox.Theming.Tokens;

namespace Mailbox.App.Views;

/// <summary>
/// The decorative layer behind the title bar's controls: nothing, one of the shipped patterns,
/// or an image — whatever <c>titlebar.backdrop</c> resolves to. Behind everything in the band
/// and, outside an align session, invisible to the pointer so the window drag and the caption
/// buttons behave exactly as they do without it.
/// </summary>
/// <remarks>
/// In an align session the layer becomes the one thing the pointer talks to: a drag moves the
/// image's alignment live and each release reports the position as <c>x% y%</c>, which is the
/// direct-manipulation door — the value a settings writer then keeps is exactly what the hand
/// placed. Escape and the session's end are the owner window's to run; this control only says
/// what the drag did.
/// </remarks>
internal sealed class CaptionBackdrop : Control
{
    private string _backdrop = string.Empty;
    private (double X, double Y) _alignment = (1, 0);
    private string _tiling = "no-repeat";
    private string _size = "auto";
    private double _opacity = 0.16;

    private bool _aligning;
    private Point _dragStart;
    private (double X, double Y) _dragFrom;
    private (double X, double Y)? _sessionInitial;

    /// <summary>Raised on each release of an align drag, with the alignment as "x% y%".</summary>
    internal event Action<string>? AlignmentCommitted;

    public CaptionBackdrop()
    {
        IsHitTestVisible = false;
        AttachedToVisualTree += (_, _) =>
        {
            App.Themes.Changed += OnThemeChanged;
            ReadTokens();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            App.Themes.Changed -= OnThemeChanged;
            _timer?.Stop();
            _timer = null;
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ReadTokens();

    private void ReadTokens()
    {
        var tokens = App.Themes.Tokens;
        _backdrop = tokens.TryGetString(TokenKeys.TitleBar.Backdrop, out var b) ? b.Trim() : string.Empty;
        _alignment = ParseAlignment(tokens.TryGetString(TokenKeys.TitleBar.BackdropAlignment, out var a) ? a : "right top");
        _tiling = tokens.TryGetString(TokenKeys.TitleBar.BackdropTiling, out var t) ? t.Trim().ToLowerInvariant() : "no-repeat";
        _size = tokens.TryGetString(TokenKeys.TitleBar.BackdropSize, out var s) ? s.Trim().ToLowerInvariant() : "auto";
        _opacity = tokens.TryGetString(TokenKeys.TitleBar.BackdropOpacity, out var o)
                   && double.TryParse(o, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 1)
            : 0.16;

        // A changed backdrop starts its animation from its first frame; playback re-syncs on
        // the next render pass against whatever the new value turns out to be.
        _frame = 0;
        _timer?.Stop();
        _timer = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------------------------

    private static readonly bool HarnessAnnounce =
        Environment.GetEnvironmentVariable(Theming.BackdropChoice.Variable) is not null
        || Environment.GetEnvironmentVariable("MAILBOX_BACKDROP_FRAME") is not null;

    private bool _announced;

    public override void Render(DrawingContext context)
    {
        // The harness's claim is the drawn layer, not the token: one line, from the render
        // pass. A frame pose applies its theme after the first paint, so the announcement
        // waits for a backdrop unless the pose explicitly asked about the empty state — and
        // an image path announces after playback has synced, so the frame it names is the
        // frame it drew.
        void Announce()
        {
            if (!HarnessAnnounce || _announced) return;
            _announced = true;
            Log.Info($"Harness: caption backdrop — {Describe()}.");
        }

        if (_backdrop.Length == 0 || _backdrop.StartsWith("pattern:", StringComparison.OrdinalIgnoreCase))
        {
            if (_backdrop.Length > 0 || Environment.GetEnvironmentVariable(Theming.BackdropChoice.Variable) is not null)
            {
                Announce();
            }
        }

        if (_backdrop.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var bounds = new Rect(Bounds.Size);

        using var clip = context.PushClip(bounds);
        using var opacity = context.PushOpacity(_opacity);

        if (_backdrop.StartsWith("pattern:", StringComparison.OrdinalIgnoreCase))
        {
            var name = _backdrop["pattern:".Length..].Trim();
            if (!CaptionPatterns.IsKnown(name)) { WarnOnce($"No pattern named \"{name}\"."); return; }
            CaptionPatterns.Draw(name, context, bounds, App.Themes.Tokens.GetBrush(TokenKeys.TitleBar.Foreground));
            return;
        }

        var loaded = LoadImage(_backdrop);
        SyncPlayback(loaded);
        Announce();
        if (loaded is null) return;
        var image = loaded.Frames[_frame % loaded.Frames.Length];

        var (destW, destH) = _size switch
        {
            "cover" => Scale(image, bounds, Math.Max),
            "contain" => Scale(image, bounds, Math.Min),
            _ => (image.PixelSize.Width, (double)image.PixelSize.Height),
        };
        if (destW < 1 || destH < 1) return;

        var x = bounds.Left + ((bounds.Width - destW) * _alignment.X);
        var y = bounds.Top + ((bounds.Height - destH) * _alignment.Y);
        var source = new Rect(image.Size);

        var tileX = _tiling is "repeat" or "repeat-x";
        var tileY = _tiling is "repeat" or "repeat-y";
        var startX = tileX ? x - (Math.Ceiling((x - bounds.Left) / destW) * destW) : x;
        var startY = tileY ? y - (Math.Ceiling((y - bounds.Top) / destH) * destH) : y;

        for (var dy = startY; dy < bounds.Bottom; dy += destH)
        {
            for (var dx = startX; dx < bounds.Right; dx += destW)
            {
                context.DrawImage(image, source, new Rect(dx, dy, destW, destH));
                if (!tileX) break;
            }
            if (!tileY) break;
        }
    }

    private static (double, double) Scale(Bitmap image, Rect bounds, Func<double, double, double> pick)
    {
        var scale = pick(bounds.Width / image.PixelSize.Width, bounds.Height / image.PixelSize.Height);
        return (image.PixelSize.Width * scale, image.PixelSize.Height * scale);
    }

    // ------------------------------------------------------------------------------------
    // The image cache: a handful of decoded backdrops by path and write time, so a theme
    // swap does not re-decode and an edited file shows its new self. An animated backdrop is
    // its frames and their delays, read from the timing file the importer wrote beside them.
    // ------------------------------------------------------------------------------------

    private sealed record LoadedBackdrop(DateTime Written, Bitmap[] Frames, int[] Delays)
    {
        public bool Animated => Frames.Length > 1;
    }

    private static readonly Dictionary<string, LoadedBackdrop> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    private LoadedBackdrop? LoadImage(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved is null) { WarnOnce($"The backdrop path \"{path}\" is not usable."); return null; }

        try
        {
            var written = File.GetLastWriteTimeUtc(resolved);
            if (Cache.TryGetValue(resolved, out var kept) && kept.Written == written) return kept;
            if (!File.Exists(resolved)) { WarnOnce($"The backdrop image \"{resolved}\" is not there; the caption stays plain."); return null; }

            var loaded = LoadFrames(resolved, written);
            if (Cache.Count >= 4 && !Cache.ContainsKey(resolved))
            {
                var oldest = Cache.OrderBy(p => p.Value.Written).First();
                foreach (var frame in oldest.Value.Frames) frame.Dispose();
                Cache.Remove(oldest.Key);
            }

            Cache[resolved] = loaded;
            Warned.Remove(resolved);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WarnOnce($"The backdrop image \"{resolved}\" could not be read: {ex.Message}");
            return null;
        }
    }

    private static LoadedBackdrop LoadFrames(string resolved, DateTime written)
    {
        var directory = Path.GetDirectoryName(resolved)!;
        var manifest = Path.Combine(directory, Mailbox.Theming.Import.ImportedThemes.AnimationManifest);
        if (File.Exists(manifest))
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest)) is System.Text.Json.Nodes.JsonObject timing
                    && timing["frames"] is System.Text.Json.Nodes.JsonArray names
                    && timing["delays"] is System.Text.Json.Nodes.JsonArray delays
                    && names.Count == delays.Count && names.Count > 1)
                {
                    var frames = new List<Bitmap>();
                    var frameDelays = new List<int>();
                    foreach (var (name, delay) in names.Zip(delays))
                    {
                        var file = Path.GetFileName(name?.GetValue<string>() ?? string.Empty);
                        var framePath = Path.Combine(directory, file);
                        if (file.Length == 0 || !File.Exists(framePath)) continue;
                        frames.Add(new Bitmap(framePath));
                        frameDelays.Add(Math.Max(20, delay?.GetValue<int>() ?? 100));
                    }

                    if (frames.Count > 1) return new LoadedBackdrop(written, [.. frames], [.. frameDelays]);
                    foreach (var frame in frames) frame.Dispose();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A timing file that does not parse leaves a perfectly good still image.
            }
        }

        return new LoadedBackdrop(written, [new Bitmap(resolved)], [0]);
    }

    // ------------------------------------------------------------------------------------
    // Playback: one timer while an animated backdrop is on show — and stillness for the
    // harness, whose photographs must not depend on when the shutter fell.
    // ------------------------------------------------------------------------------------

    private DispatcherTimer? _timer;
    private int _frame;

    private static readonly bool Frozen = Mailbox.App.Theming.WindowCapture.IsRequested;

    private static readonly int FrozenFrame =
        int.TryParse(Environment.GetEnvironmentVariable("MAILBOX_BACKDROP_FRAME"), out var posed) ? posed : 0;

    private void SyncPlayback(LoadedBackdrop? loaded)
    {
        if (loaded is not { Animated: true } || Frozen)
        {
            _timer?.Stop();
            _timer = null;
            if (Frozen && loaded is { Animated: true }) _frame = Math.Clamp(FrozenFrame, 0, loaded.Frames.Length - 1);
            return;
        }

        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(loaded.Delays[_frame % loaded.Delays.Length]) };
        _timer.Tick += (_, _) =>
        {
            if (_timer is null) return;
            _frame++;
            _timer.Interval = TimeSpan.FromMilliseconds(loaded.Delays[_frame % loaded.Delays.Length]);
            InvalidateVisual();
        };
        _timer.Start();
    }

    /// <summary>Absolute stays; relative resolves against the themes directory; a path that climbs out is refused.</summary>
    private static string? ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var root = Mailbox.Theming.Files.ThemeLibrary.DefaultDirectory();
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private static void WarnOnce(string message)
    {
        if (Warned.Add(message)) Log.Warn($"Caption backdrop: {message}");
    }

    // ------------------------------------------------------------------------------------
    // The align session
    // ------------------------------------------------------------------------------------

    /// <summary>Whether a drag currently moves the image instead of the window.</summary>
    internal bool Aligning
    {
        get => _aligning;
        set
        {
            _aligning = value;
            IsHitTestVisible = value;
            Cursor = value ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
            if (value) _sessionInitial = _alignment;
            else _sessionInitial = null;
        }
    }

    /// <summary>Ctrl+Z inside the session: back to where the image was when the session began.</summary>
    internal void RevertAlign()
    {
        if (_sessionInitial is not { } initial) return;
        _alignment = initial;
        InvalidateVisual();
        AlignmentCommitted?.Invoke(AlignmentText());
    }

    internal string AlignmentText()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Math.Round(_alignment.X * 100, 1)}% {Math.Round(_alignment.Y * 100, 1)}%");

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!_aligning) return;
        _dragStart = e.GetPosition(this);
        _dragFrom = _alignment;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_aligning || !Equals(e.Pointer.Captured, this)) return;
        if (LoadImage(_backdrop) is not { } loaded) return;
        var image = loaded.Frames[0];

        var (destW, destH) = _size switch
        {
            "cover" => Scale(image, new Rect(Bounds.Size), Math.Max),
            "contain" => Scale(image, new Rect(Bounds.Size), Math.Min),
            _ => (image.PixelSize.Width, (double)image.PixelSize.Height),
        };

        var position = e.GetPosition(this);
        var leftoverX = Bounds.Width - destW;
        var leftoverY = Bounds.Height - destH;
        _alignment = (
            Math.Abs(leftoverX) < 1 ? _dragFrom.X : Math.Clamp(_dragFrom.X + ((position.X - _dragStart.X) / leftoverX), 0, 1),
            Math.Abs(leftoverY) < 1 ? _dragFrom.Y : Math.Clamp(_dragFrom.Y + ((position.Y - _dragStart.Y) / leftoverY), 0, 1));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_aligning) return;
        e.Pointer.Capture(null);
        AlignmentCommitted?.Invoke(AlignmentText());
        e.Handled = true;
    }

    /// <summary>What a harness pose reads back: the layer as it is actually being drawn.</summary>
    internal string Describe()
    {
        if (_backdrop.Length == 0) return "backdrop: none";
        var animated = !_backdrop.StartsWith("pattern:", StringComparison.OrdinalIgnoreCase)
                       && LoadImage(_backdrop) is { Animated: true } loaded
            ? $"; animated {loaded.Frames.Length} frame(s), showing {_frame % loaded.Frames.Length}"
            : string.Empty;
        return $"backdrop: {_backdrop}; alignment {AlignmentText()}; tiling {_tiling}; size {_size}; "
               + $"opacity {_opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}{animated}";
    }

    // ------------------------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------------------------

    internal static (double X, double Y) ParseAlignment(string text)
    {
        double? x = null, y = null;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (word.ToLowerInvariant())
            {
                case "left": x = 0; break;
                case "right": x = 1; break;
                case "top": y = 0; break;
                case "bottom": y = 1; break;
                case "center" or "centre":
                    if (x is null) x = 0.5;
                    else y ??= 0.5;
                    break;
                default:
                    if (word.EndsWith('%')
                        && double.TryParse(word[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
                    {
                        var fraction = Math.Clamp(percent / 100, 0, 1);
                        if (x is null) x = fraction;
                        else y ??= fraction;
                    }
                    break;
            }
        }

        return (x ?? 0.5, y ?? 0.5);
    }
}
