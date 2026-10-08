using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mailbox.Core.Diagnostics;
using Xilium.CefGlue;

namespace Mailbox.App.Chromium;

/// <summary>
/// One message drawn by Chromium, off screen, into the pane like any other control.
/// </summary>
/// <remarks>
/// Chromium paints into memory on its own thread; the frame is copied there into whichever of two
/// bitmaps is not on screen, and the UI thread only swaps which one it draws. Nothing about a
/// message's layout or painting happens on the UI thread.
/// <para>
/// The document is served from <c>https://message.invalid/</c> — a name that by definition never
/// resolves — by this control, and every other request the page makes is cancelled before it
/// leaves: the pictures a reader allows arrive inside the document, fetched by Mailbox, so the
/// engine has nothing to fetch. JavaScript is off. A link is never followed here; it is handed
/// to the desktop.
/// </para>
/// </remarks>
internal sealed class ChromiumView : Control
{
    /// <summary>Where every document is served from. `.invalid` is reserved never to resolve (RFC 6761).</summary>
    internal const string Origin = "https://message.invalid/";

    private readonly ViewClient _client;
    private readonly object _gate = new();

    private CefBrowser? _browser;
    private bool _closed;

    // Two bitmaps: the one on screen, and the one Chromium writes the next frame into.
    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private WriteableBitmap? _pending;

    private string _document = string.Empty;
    private long _generation;
    private string? _queued;

    private double _scale = 1;
    private double _scrollY;

    public ChromiumView(Color background)
    {
        Focusable = true;
        ClipToBounds = true;
        Background = background;
        _client = new ViewClient(this);

        var info = CefWindowInfo.Create();
        info.SetAsWindowless(IntPtr.Zero, false);

        var settings = new CefBrowserSettings
        {
            JavaScript = CefState.Disabled,
            WindowlessFrameRate = 60,
            BackgroundColor = new CefColor(255, background.R, background.G, background.B),
        };

        CefBrowserHost.CreateBrowser(info, _client, settings, "about:blank");
    }

    /// <summary>What shows behind the document before its first frame.</summary>
    public Color Background { get; set; }

    /// <summary>The text the reader has selected, kept as Chromium reports it.</summary>
    public string SelectedText { get; private set; } = string.Empty;

    /// <summary>A load finished: true when the document arrived, false when it did not.</summary>
    public event EventHandler<bool>? LoadFinished;

    /// <summary>The reader activated a link; the pane decides what to do with it.</summary>
    public event EventHandler<Uri>? LinkActivated;

    /// <summary>Frames delivered since the view was made, for a harness run to read back.</summary>
    internal int Frames { get; private set; }

    /// <summary>Shows a document. A load already in flight is simply replaced.</summary>
    public void Navigate(string html)
    {
        if (_closed) return;

        if (_browser is null)
        {
            _queued = html;
            return;
        }

        lock (_gate)
        {
            _document = html;
            _generation++;
        }

        // A path of its own per load, so nothing about the previous document can be mistaken
        // for this one.
        _browser.GetMainFrame().LoadUrl($"{Origin}{_generation}");
    }

    /// <summary>The words of the document on show, as Chromium laid them out.</summary>
    public Task<string> TextAsync()
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_browser is null) done.SetResult(string.Empty);
        else _browser.GetMainFrame().GetText(new TextVisitor(done));
        return done.Task;
    }

    /// <summary>Selects the whole document.</summary>
    public void SelectAll() => _browser?.GetMainFrame().SelectAll();

    /// <summary>
    /// Scrolls a screen, and says whether there was anywhere to go: the offset Chromium reports
    /// is compared before and after, so the foot of a message answers false.
    /// </summary>
    public async Task<bool> ScrollPageAsync(bool down)
    {
        if (_browser is null) return false;

        var before = _scrollY;
        SendKey(down ? Key.PageDown : Key.PageUp, KeyModifiers.None);

        // The offset arrives on Chromium's thread once the scroll has been laid out.
        for (var i = 0; i < 10 && Math.Abs(_scrollY - before) < 0.5; i++) await Task.Delay(20);
        return Math.Abs(_scrollY - before) >= 0.5;
    }

    /// <summary>Writes the document to a PDF at <paramref name="path"/>.</summary>
    public Task<bool> PrintToPdfAsync(string path)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_browser is null)
        {
            done.SetResult(false);
            return done.Task;
        }

        _browser.GetHost().PrintToPdf(path, new CefPdfPrintSettings
        {
            PrintBackground = true,
            PreferCssPageSize = true,
            MarginType = CefPdfPrintMarginType.Default,
        }, new PdfDone(done));

        return done.Task;
    }

    /// <summary>Closes the browser behind this view. The view draws nothing afterwards.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _browser?.GetHost().CloseBrowser(true);
        _browser = null;
    }

    // ---- Chromium's side -----------------------------------------------------------------

    internal void Created(CefBrowser browser) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed)
        {
            browser.GetHost().CloseBrowser(true);
            return;
        }

        _browser = browser;

        // The view may have been placed on a scaled screen before Chromium finished making the
        // browser; it is told now, or it paints at 1x and the pane stretches the result.
        browser.GetHost().NotifyScreenInfoChanged();
        browser.GetHost().WasResized();

        if (_queued is { } html)
        {
            _queued = null;
            Navigate(html);
        }
    });

    /// <summary>The document for a request, or null when the request is not for the one on show.</summary>
    internal byte[]? DocumentFor(string url)
    {
        lock (_gate)
        {
            return url == $"{Origin}{_generation}" ? Encoding.UTF8.GetBytes(_document) : null;
        }
    }

    internal (int Width, int Height, double Scale) Viewport =>
        (Math.Max(1, (int)Math.Ceiling(Bounds.Width)), Math.Max(1, (int)Math.Ceiling(Bounds.Height)), _scale);

    /// <summary>On Chromium's thread: the new frame goes into the bitmap that is not on screen.</summary>
    internal void Paint(IntPtr buffer, int width, int height)
    {
        ChromiumRuntime.Confirmed();

        lock (_gate)
        {
            var target = _pending ?? _back;
            if (target is null || target.PixelSize.Width != width || target.PixelSize.Height != height)
            {
                target = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            using (var locked = target.Lock())
            {
                var row = width * 4;
                if (locked.RowBytes == row)
                {
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)buffer, (void*)locked.Address, (long)row * height, (long)row * height);
                    }
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        unsafe
                        {
                            Buffer.MemoryCopy((void*)(buffer + y * row), (void*)(locked.Address + y * locked.RowBytes), row, row);
                        }
                    }
                }
            }

            _pending = target;
            _back = null;
        }

        Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
    }

    private void Present()
    {
        lock (_gate)
        {
            if (_pending is null) return;
            _back = _front;
            _front = _pending;
            _pending = null;
        }

        Frames++;
        InvalidateVisual();
    }

    internal void ScrolledTo(double y) => _scrollY = y;

    internal void SelectionChanged(string text) => Dispatcher.UIThread.Post(() => SelectedText = text);

    internal void LoadEnded(bool ok) => Dispatcher.UIThread.Post(() => LoadFinished?.Invoke(this, ok));

    internal void Activated(Uri uri) => Dispatcher.UIThread.Post(() => LinkActivated?.Invoke(this, uri));

    internal void CursorIs(CefCursorType type) => Dispatcher.UIThread.Post(() =>
        Cursor = type switch
        {
            CefCursorType.Hand => new Cursor(StandardCursorType.Hand),
            CefCursorType.IBeam => new Cursor(StandardCursorType.Ibeam),
            _ => Cursor.Default,
        });

    // ---- Avalonia's side -----------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Background), new Rect(Bounds.Size));

        WriteableBitmap? frame;
        lock (_gate) frame = _front;

        if (frame is not null)
        {
            var size = new Size(frame.PixelSize.Width / _scale, frame.PixelSize.Height / _scale);
            context.DrawImage(frame, new Rect(frame.Size), new Rect(size));
        }
    }

    private TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _top = TopLevel.GetTopLevel(this);
        if (_top is not null) _top.ScalingChanged += OnScalingChanged;
        OnScalingChanged(this, EventArgs.Empty);

        _browser?.GetHost().WasHidden(false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null) _top.ScalingChanged -= OnScalingChanged;
        _top = null;
    }

    /// <summary>The window's scale, followed: a window dragged to another monitor redraws sharp there.</summary>
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        var scale = _top?.RenderScaling ?? 1;
        if (Math.Abs(scale - _scale) < 0.001) return;

        _scale = scale;
        _browser?.GetHost().NotifyScreenInfoChanged();
        _browser?.GetHost().WasResized();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _browser?.GetHost().WasResized();
    }

    private static CefEventFlags Flags(KeyModifiers modifiers, PointerPointProperties? pointer = null)
    {
        var flags = CefEventFlags.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) flags |= CefEventFlags.ShiftDown;
        if (modifiers.HasFlag(KeyModifiers.Control)) flags |= CefEventFlags.ControlDown;
        if (modifiers.HasFlag(KeyModifiers.Alt)) flags |= CefEventFlags.AltDown;
        if (pointer is { IsLeftButtonPressed: true }) flags |= CefEventFlags.LeftMouseButton;
        if (pointer is { IsMiddleButtonPressed: true }) flags |= CefEventFlags.MiddleMouseButton;
        if (pointer is { IsRightButtonPressed: true }) flags |= CefEventFlags.RightMouseButton;
        return flags;
    }

    private CefMouseEvent Mouse(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        return new CefMouseEvent((int)point.Position.X, (int)point.Position.Y, Flags(e.KeyModifiers, point.Properties));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_browser is null) return;

        Focus();
        _browser.GetHost().SetFocus(true);

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            _browser.GetHost().SendMouseClickEvent(Mouse(e), CefMouseButtonType.Left, false, e.ClickCount);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_browser is null || e.InitialPressMouseButton != MouseButton.Left) return;

        _browser.GetHost().SendMouseClickEvent(Mouse(e), CefMouseButtonType.Left, true, 1);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _browser?.GetHost().SendMouseMoveEvent(Mouse(e), false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _browser?.GetHost().SendMouseMoveEvent(Mouse(e), true);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_browser is null) return;

        _browser.GetHost().SendMouseWheelEvent(Mouse(e), (int)(e.Delta.X * 120), (int)(e.Delta.Y * 120));
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _browser?.GetHost().SetFocus(true);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _browser?.GetHost().SetFocus(false);
    }

    /// <summary>
    /// The keys that move around a message reach Chromium; everything else stays the
    /// application's, so a shortcut means the same over the message as over the list.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && SendKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    private bool SendKey(Key key, KeyModifiers modifiers)
    {
        if (_browser is null || VirtualKey(key) is not { } code) return false;

        var host = _browser.GetHost();
        var flags = Flags(modifiers);
        host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.RawKeyDown, WindowsKeyCode = code, Modifiers = flags });
        host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.KeyUp, WindowsKeyCode = code, Modifiers = flags });
        return true;
    }

    /// <summary>Chromium speaks Windows key codes on every platform.</summary>
    private static int? VirtualKey(Key key) => key switch
    {
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Space => 0x20,
        _ => null,
    };

    // ---- Chromium's handlers -------------------------------------------------------------

    private sealed class ViewClient(ChromiumView view) : CefClient
    {
        private readonly Painter _render = new(view);
        private readonly LifeSpan _life = new(view);
        private readonly Loads _load = new(view);
        private readonly Requests _requests = new(view);
        private readonly NoMenu _menu = new();
        private readonly Display _display = new(view);

        protected override CefRenderHandler GetRenderHandler() => _render;
        protected override CefLifeSpanHandler GetLifeSpanHandler() => _life;
        protected override CefLoadHandler GetLoadHandler() => _load;
        protected override CefRequestHandler GetRequestHandler() => _requests;
        protected override CefContextMenuHandler GetContextMenuHandler() => _menu;
        protected override CefDisplayHandler GetDisplayHandler() => _display;
    }

    private sealed class Painter(ChromiumView view) : CefRenderHandler
    {
        protected override CefAccessibilityHandler? GetAccessibilityHandler() => null;

        protected override void GetViewRect(CefBrowser browser, out CefRectangle rect)
        {
            var (width, height, _) = view.Viewport;
            rect = new CefRectangle(0, 0, width, height);
        }

        protected override bool GetScreenInfo(CefBrowser browser, CefScreenInfo screenInfo)
        {
            screenInfo.DeviceScaleFactor = (float)view.Viewport.Scale;
            return true;
        }

        protected override void OnPopupSize(CefBrowser browser, CefRectangle rect)
        {
        }

        protected override void OnPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr buffer, int width, int height)
        {
            if (type == CefPaintElementType.View) view.Paint(buffer, width, height);
        }

        protected override void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, CefAcceleratedPaintInfo info)
        {
        }

        protected override void OnScrollOffsetChanged(CefBrowser browser, double x, double y) => view.ScrolledTo(y);

        protected override void OnImeCompositionRangeChanged(CefBrowser browser, CefRange selectedRange, CefRectangle[] characterBounds)
        {
        }

        protected override void OnTextSelectionChanged(CefBrowser browser, string selectedText, CefRange selectedRange)
            => view.SelectionChanged(selectedText ?? string.Empty);
    }

    private sealed class LifeSpan(ChromiumView view) : CefLifeSpanHandler
    {
        protected override void OnAfterCreated(CefBrowser browser) => view.Created(browser);

        /// <summary>No window ever opens from a message: the link goes to the desktop instead.</summary>
        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, int popupId, string targetUrl,
            string targetFrameName, CefWindowOpenDisposition targetDisposition, bool userGesture,
            CefPopupFeatures popupFeatures, CefWindowInfo windowInfo, ref CefClient client,
            CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            if (userGesture && Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)) view.Activated(uri);
            return true;
        }
    }

    private sealed class Loads(ChromiumView view) : CefLoadHandler
    {
        protected override void OnLoadEnd(CefBrowser browser, CefFrame frame, int httpStatusCode)
        {
            if (frame.IsMain && frame.Url.StartsWith(Origin, StringComparison.Ordinal)) view.LoadEnded(httpStatusCode == 200);
        }

        protected override void OnLoadError(CefBrowser browser, CefFrame frame, CefErrorCode errorCode, string errorText, string failedUrl)
        {
            // A load replaced by the next one is aborted, which is not a failure worth a word.
            if (frame.IsMain && errorCode != CefErrorCode.Aborted && failedUrl.StartsWith(Origin, StringComparison.Ordinal))
            {
                Log.Warn($"The reading pane could not load the message: {errorCode} {errorText}");
                view.LoadEnded(false);
            }
        }
    }

    /// <summary>The network, closed: the document is served from here, and nothing else is fetched.</summary>
    private sealed class Requests(ChromiumView view) : CefRequestHandler
    {
        private readonly Fetches _resources = new(view);

        /// <summary>
        /// Navigation. Our own document is allowed; a link the reader clicked goes to the desktop;
        /// anything else — a meta refresh, a form, a frame — is simply refused.
        /// </summary>
        protected override bool OnBeforeBrowse(CefBrowser browser, CefFrame frame, CefRequest request, bool userGesture, bool isRedirect)
        {
            var url = request.Url;
            if (url.StartsWith(Origin, StringComparison.Ordinal) || url == "about:blank") return false;

            if (userGesture && !isRedirect && Uri.TryCreate(url, UriKind.Absolute, out var uri)) view.Activated(uri);
            else if (Mailbox.App.Theming.WindowCapture.IsRequested) Log.Info($"Harness: chromium refused navigation to {url}");
            return true;
        }

        protected override bool OnOpenUrlFromTab(CefBrowser browser, CefFrame frame, string targetUrl,
            CefWindowOpenDisposition targetDisposition, bool userGesture)
        {
            if (userGesture && Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)) view.Activated(uri);
            return true;
        }

        protected override CefResourceRequestHandler GetResourceRequestHandler(CefBrowser browser, CefFrame frame,
            CefRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        {
            disableDefaultHandling = true;
            return _resources;
        }
    }

    private sealed class Fetches(ChromiumView view) : CefResourceRequestHandler
    {
        protected override CefCookieAccessFilter? GetCookieAccessFilter(CefBrowser browser, CefFrame frame, CefRequest request) => null;

        protected override CefReturnValue OnBeforeResourceLoad(CefBrowser browser, CefFrame frame, CefRequest request, CefCallback callback)
        {
            if (request.Url.StartsWith(Origin, StringComparison.Ordinal)) return CefReturnValue.Continue;

            if (Mailbox.App.Theming.WindowCapture.IsRequested) Log.Info($"Harness: chromium refused {request.Url}");
            return CefReturnValue.Cancel;
        }

        protected override CefResourceHandler? GetResourceHandler(CefBrowser browser, CefFrame frame, CefRequest request)
            => view.DocumentFor(request.Url) is { } bytes ? new Document(bytes) : null;

        protected override void OnProtocolExecution(CefBrowser browser, CefFrame frame, CefRequest request, ref bool allowOSExecution)
            => allowOSExecution = false;
    }

    /// <summary>The message, answered from memory.</summary>
    private sealed class Document(byte[] bytes) : CefResourceHandler
    {
        private int _offset;

        protected override bool Open(CefRequest request, out bool handleRequest, CefCallback callback)
        {
            handleRequest = true;
            return true;
        }

        protected override void GetResponseHeaders(CefResponse response, out long responseLength, out string? redirectUrl)
        {
            response.Status = 200;
            response.StatusText = "OK";
            response.MimeType = "text/html";
            response.Charset = "utf-8";
            responseLength = bytes.Length;
            redirectUrl = null;
        }

        protected override bool Skip(long bytesToSkip, out long bytesSkipped, CefResourceSkipCallback callback)
        {
            bytesSkipped = Math.Min(bytesToSkip, bytes.Length - _offset);
            _offset += (int)bytesSkipped;
            return bytesSkipped > 0;
        }

        protected override bool Read(Stream response, int bytesToRead, out int bytesRead, CefResourceReadCallback callback)
        {
            bytesRead = Math.Min(bytesToRead, bytes.Length - _offset);
            if (bytesRead <= 0)
            {
                bytesRead = 0;
                return false;
            }

            response.Write(bytes, _offset, bytesRead);
            _offset += bytesRead;
            return true;
        }

        protected override void Cancel()
        {
        }
    }

    /// <summary>Chromium's own menu has nowhere to draw off screen; the pane offers its own.</summary>
    private sealed class NoMenu : CefContextMenuHandler
    {
        protected override void OnBeforeContextMenu(CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
            => model.Clear();
    }

    private sealed class Display(ChromiumView view) : CefDisplayHandler
    {
        protected override bool OnCursorChange(CefBrowser browser, IntPtr cursorHandle, CefCursorType type, CefCursorInfo customCursorInfo)
        {
            view.CursorIs(type);
            return true;
        }
    }

    private sealed class TextVisitor(TaskCompletionSource<string> done) : CefStringVisitor
    {
        protected override void Visit(string value) => done.TrySetResult(value ?? string.Empty);
    }

    private sealed class PdfDone(TaskCompletionSource<bool> done) : CefPdfPrintCallback
    {
        protected override void OnPdfPrintFinished(string path, bool ok) => done.TrySetResult(ok);
    }
}
