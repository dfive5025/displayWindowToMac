using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OpenDisplay.Core.Discovery;
using OpenDisplay.Core.Session;
using OpenDisplay.Core.Video;
using OpenDisplay.Core.Wire;

namespace OpenDisplay.Windows;

/// <summary>
/// The receiver window: shows the Mac's extended display, draws the Mac cursor on top,
/// and turns this window's mouse into touch/scroll input for the Mac.
/// F11 toggles fullscreen, Esc leaves it, F1 shows the log.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly FileLog _log = new();
    private readonly ReceiverServer _server;
    private BonjourAdvertiser? _bonjour;

    private readonly object _frameGate = new();
    private VideoFrame? _pendingFrame;
    private bool _paintQueued;
    /// <summary>The frame on screen. Held (not released) until a newer one replaces it.</summary>
    private VideoFrame? _current;

    private CursorState _cursor;
    private Bitmap? _cursorImage;
    private CursorImage? _cursorInfo;

    private SessionState _state = SessionState.Waiting;
    private string _stateDetail = "waiting for a sender";
    private bool _showLog;
    private bool _mouseDown;
    private bool _windowsCursorHidden;
    private Screen? _lastScreen;

    private FormWindowState _windowedState;
    private Rectangle _windowedBounds;
    private bool _fullscreen;
    private bool _userLeftFullscreen;

    /// <summary>Do not switch to fullscreen when streaming starts (--windowed).</summary>
    public bool StayWindowed
    {
        get => _userLeftFullscreen;
        set => _userLeftFullscreen = value;
    }

    public MainForm(AppSettings settings, bool startFullscreen)
    {
        _settings = settings;
        Text = $"OpenDisplay Receiver: {settings.Name}";
        BackColor = Color.Black;
        ForeColor = Color.White;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        KeyPreview = true;
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 800);

        _server = new ReceiverServer(BuildHello, settings.Port) { OfferHevc = settings.OfferHevc };
        _server.Log += _log.Write;
        _server.FrameDecoded += OnFrameDecoded;
        _server.CursorChanged += c => UI(() =>
        {
            _cursor = c;
            Invalidate();
        });
        _server.CursorImageChanged += OnCursorImage;
        _server.StateChanged += (s, detail) => UI(() =>
        {
            bool startedStreaming = s == SessionState.Streaming && _state != SessionState.Streaming;
            _state = s;
            _stateDetail = detail;
            // Fullscreen shows the Mac desktop 1:1, with no scaling cost and sharp text.
            if (startedStreaming && !_fullscreen && !_userLeftFullscreen) ToggleFullscreen();
            if (s == SessionState.Waiting) ClearVideo();
            UpdateWindowsCursor();
            Invalidate();
        });

        if (startFullscreen) Shown += (_, _) => ToggleFullscreen();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _lastScreen = Screen.FromControl(this);
        RefreshPanel();
        _server.MaxOutput = (ClientSize.Width, ClientSize.Height);
        _log.Write($"OpenDisplay Receiver for Windows, id {_settings.Id}");
        try
        {
            _log.Write($"FFmpeg: {FfmpegLoader.Version()} from {FfmpegLoader.LoadedFrom ?? "default search path"}");
        }
        catch (Exception ex)
        {
            _log.Write($"FFmpeg failed to load: {ex.Message}");
            _stateDetail = "FFmpeg DLLs missing. Re-download the app.";
        }

        try
        {
            _server.Start();
        }
        catch (SocketException ex)
        {
            _log.Write($"cannot listen on port {_settings.Port}: {ex.Message}");
            _stateDetail = $"Port {_settings.Port} is busy. Is another receiver running?";
            Invalidate();
            return;
        }

        try
        {
            _bonjour = new BonjourAdvertiser(_settings.Name, _settings.Id, _server.Port, Outgoing.ProtocolVersion);
            _log.Write($"advertising \"{_settings.Name}\" as {BonjourAdvertiser.ServiceType}");
        }
        catch (Exception ex)
        {
            _log.Write($"Bonjour advertising failed: {ex.Message}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _bonjour?.Dispose();
        _server.Shutdown();
        if (_windowsCursorHidden) Cursor.Show();
        ClearVideo();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- hello

    private volatile HelloInfo? _hello;

    /// <summary>Called from the network thread; reads the panel cached on the UI thread.</summary>
    private HelloInfo BuildHello() =>
        _hello ?? new HelloInfo(1920, 1080, 1, _settings.Id, "Windows", 60, _settings.OfferHevc);

    /// <summary>The panel is the monitor this window is on, in physical pixels (PROTOCOL.md 6.7).</summary>
    private void RefreshPanel()
    {
        var screen = Screen.FromControl(this);
        _hello = new HelloInfo(screen.Bounds.Width, screen.Bounds.Height, DeviceDpi / 96.0, _settings.Id,
            "Windows", RefreshRate(screen), _settings.OfferHevc);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        var screen = Screen.FromControl(this);
        if (_lastScreen != null && !screen.Equals(_lastScreen))
        {
            _lastScreen = screen;
            RefreshPanel();
            _log.Write($"window moved to {screen.DeviceName}, re-announcing the panel");
            _server.PanelChanged();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RefreshPanel();
        _server.PanelChanged();
    }

    // ---------------------------------------------------------------- video

    private void OnFrameDecoded(VideoFrame frame)
    {
        bool queue;
        lock (_frameGate)
        {
            _pendingFrame?.Release(); // drop a frame the UI never got to: newest wins
            _pendingFrame = frame;
            queue = !_paintQueued;
            _paintQueued = true;
        }
        if (queue) UI(() => Invalidate());
    }

    private void ClearVideo()
    {
        lock (_frameGate)
        {
            _pendingFrame?.Release();
            _pendingFrame = null;
        }
        _current?.Release();
        _current = null;
    }

    /// <summary>Swaps in the newest decoded frame (UI thread). No copy: the frame is drawn straight from its buffer.</summary>
    private void TakePendingFrame()
    {
        VideoFrame? frame;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _paintQueued = false;
        }
        if (frame == null) return;
        _current?.Release();
        _current = frame;
    }

    /// <summary>Where the video sits in the window: as large as fits, aspect kept, centered.</summary>
    private RectangleF VideoRect()
    {
        if (_current == null) return RectangleF.Empty;
        var client = ClientRectangle;
        float scale = Math.Min((float)client.Width / _current.Width, (float)client.Height / _current.Height);
        float w = _current.Width * scale, h = _current.Height * scale;
        return new RectangleF(client.X + (client.Width - w) / 2, client.Y + (client.Height - h) / 2, w, h);
    }

    // Render stats for the F1 overlay.
    private readonly System.Diagnostics.Stopwatch _statsClock = System.Diagnostics.Stopwatch.StartNew();
    private int _shownThisSecond;
    private double _paintMsMax;
    private string _renderStats = "";
    private int _statsSeconds;

    protected override void OnPaint(PaintEventArgs e)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var before = _current;
        TakePendingFrame();
        if (_current != before) _shownThisSecond++;
        PaintScene(e);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _paintMsMax = Math.Max(_paintMsMax, ms);
        if (_statsClock.ElapsedMilliseconds >= 1000)
        {
            _renderStats = $"shown {_shownThisSecond} fps, slowest paint {_paintMsMax:0.0} ms, video {_current?.Width}x{_current?.Height}, window {ClientSize.Width}x{ClientSize.Height}";
            if (_state == SessionState.Streaming && ++_statsSeconds % 5 == 0) _log.Write($"render: {_renderStats}");
            _shownThisSecond = 0;
            _paintMsMax = 0;
            _statsClock.Restart();
        }
    }

    private void PaintScene(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        if (_current != null && _state == SessionState.Streaming)
        {
            var rect = Rectangle.Round(VideoRect());
            BlitFrame(g, _current, rect);
            DrawCursor(g, rect);
        }
        else
        {
            DrawStatus(g);
        }

        if (_showLog) DrawLog(g);
    }

    private void DrawCursor(Graphics g, RectangleF video)
    {
        if (!_cursor.Visible) return;
        float x = video.X + (float)_cursor.X * video.Width;
        float y = video.Y + (float)_cursor.Y * video.Height;
        if (_cursorImage != null && _cursorInfo != null && _cursorInfo.NormalizedWidth > 0)
        {
            float w = (float)_cursorInfo.NormalizedWidth * video.Width;
            float h = (float)_cursorInfo.NormalizedHeight * video.Height;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_cursorImage, x - (float)_cursorInfo.HotspotX * w, y - (float)_cursorInfo.HotspotY * h, w, h);
        }
        else
        {
            // No sprite yet: a plain arrow-ish marker so the pointer is never invisible.
            using var brush = new SolidBrush(Color.White);
            using var pen = new Pen(Color.Black, 1.5f);
            PointF[] arrow = [new(x, y), new(x, y + 18), new(x + 5, y + 13), new(x + 12, y + 13)];
            g.FillPolygon(brush, arrow);
            g.DrawPolygon(pen, arrow);
        }
    }

    private void OnCursorImage(CursorImage img)
    {
        Bitmap? bmp = null;
        try
        {
            using var ms = new MemoryStream(img.Png);
            using var decoded = Image.FromStream(ms);
            bmp = new Bitmap(decoded);
        }
        catch (ArgumentException)
        {
            return;
        }
        UI(() =>
        {
            _cursorImage?.Dispose();
            _cursorImage = bmp;
            _cursorInfo = img;
            Invalidate();
        });
    }

    private void DrawStatus(Graphics g)
    {
        var lines = new List<string>
        {
            "OpenDisplay Receiver for Windows",
            "",
            $"Name: {_settings.Name}",
            $"Address: {string.Join(", ", LocalAddresses())}  (port {_server.Port})",
            $"Status: {_stateDetail}",
            "",
            "On the Mac: open OpenDisplay and pick this PC from the Connection menu.",
            "Both computers must be on the same network. Allow this app through Windows Firewall when asked.",
            "",
            "F11: fullscreen    Esc: leave fullscreen    F1: show log",
        };
        using var title = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Point);
        using var body = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Point);
        float y = ClientRectangle.Height / 2f - 140;
        for (int i = 0; i < lines.Count; i++)
        {
            var font = i == 0 ? title : body;
            var size = g.MeasureString(lines[i], font);
            g.DrawString(lines[i], font, i == 0 ? Brushes.White : Brushes.Gainsboro, (ClientRectangle.Width - size.Width) / 2, y);
            y += size.Height + 2;
        }
    }

    private void DrawLog(Graphics g)
    {
        using var font = new Font("Consolas", 9, FontStyle.Regular, GraphicsUnit.Point);
        using var bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
        var lines = _log.Recent();
        float lineH = font.GetHeight(g) + 1;
        var box = new RectangleF(8, 8, Math.Min(ClientRectangle.Width - 16, 900), lineH * (lines.Length + 2) + 8);
        g.FillRectangle(bg, box);
        float y = 12;
        g.DrawString($"Log file: {_log.FilePath}", font, Brushes.LightSkyBlue, 12, y);
        y += lineH;
        g.DrawString(_renderStats, font, Brushes.LightGreen, 12, y);
        foreach (var line in lines)
        {
            y += lineH;
            g.DrawString(line, font, Brushes.White, 12, y);
        }
    }

    private static IEnumerable<string> LocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) result.Add(a.Address.ToString());
            }
        }
        catch (NetworkInformationException)
        {
        }
        return result.Count > 0 ? result : ["no network"];
    }

    // ---------------------------------------------------------------- input

    private bool TryNormalize(Point p, out double nx, out double ny)
    {
        nx = ny = 0;
        var rect = VideoRect();
        if (_state != SessionState.Streaming || rect.Width <= 0 || rect.Height <= 0) return false;
        nx = (p.X - rect.X) / rect.Width;
        ny = (p.Y - rect.Y) / rect.Height;
        return nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !TryNormalize(e.Location, out var x, out var y)) return;
        _mouseDown = true;
        Capture = true;
        _server.SendTouch("began", x, y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateWindowsCursor();
        bool inside = TryNormalize(e.Location, out var x, out var y);
        if (_mouseDown)
        {
            _server.SendTouch("moved", Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }
        else if (inside)
        {
            // A "moved" without a press is a hover: the Mac moves its pointer without clicking.
            _server.SendTouch("moved", x, y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_mouseDown) return;
        _mouseDown = false;
        Capture = false;
        TryNormalize(e.Location, out var x, out var y);
        _server.SendTouch("ended", Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_mouseDown && !Capture)
        {
            _mouseDown = false;
            _server.SendTouch("cancelled", 0, 0);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_state != SessionState.Streaming || _current == null) return;
        // One wheel notch (120) scrolls about three lines. Wheel up shows content above,
        // which is the natural-scrolling positive direction on the wire.
        double pixels = e.Delta / 120.0 * 3 * 20 * (_current.SourceHeight / 1080.0 * 2);
        if ((ModifierKeys & Keys.Shift) != 0) _server.SendScroll(pixels, 0);
        else _server.SendScroll(0, pixels);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetWindowsCursorHidden(false);
    }

    /// <summary>Hide the Windows pointer over the video so only the Mac's pointer shows.</summary>
    private void UpdateWindowsCursor()
    {
        var p = PointToClient(MousePosition);
        bool over = TryNormalize(p, out _, out _) && ClientRectangle.Contains(p);
        SetWindowsCursorHidden(over && _cursor.Visible);
    }

    private void SetWindowsCursorHidden(bool hidden)
    {
        if (hidden == _windowsCursorHidden) return;
        _windowsCursorHidden = hidden;
        if (hidden) Cursor.Hide();
        else Cursor.Show();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.F11:
                if (_fullscreen) _userLeftFullscreen = true;
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Keys.Escape when _fullscreen:
                _userLeftFullscreen = true;
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Keys.F1:
                _showLog = !_showLog;
                Invalidate();
                e.Handled = true;
                break;
            case Keys.F5:
                _server.RequestKeyframe();
                e.Handled = true;
                break;
        }
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _windowedState = WindowState;
            _windowedBounds = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            TopMost = true;
            _fullscreen = true;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _windowedBounds;
            WindowState = _windowedState;
            _fullscreen = false;
        }
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Have the decoder shrink frames to the window, so painting never scales big frames.
        if (_server != null) _server.MaxOutput = (ClientSize.Width, ClientSize.Height);
        Invalidate();
    }

    // ---------------------------------------------------------------- helpers

    private void UI(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    /// <summary>
    /// Draws the BGRA frame with GDI's StretchDIBits: no Bitmap copy, and far cheaper than
    /// GDI+ DrawImage, whose scaling cost tens of milliseconds per 1080p frame.
    /// </summary>
    private static unsafe void BlitFrame(Graphics g, VideoFrame frame, Rectangle dest)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = frame.Width,
            biHeight = -frame.Height, // negative: rows are top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };
        IntPtr hdc = g.GetHdc();
        try
        {
            bool oneToOne = dest.Width == frame.Width && dest.Height == frame.Height;
            // HALFTONE filters nicely but costs ~35 ms per 1440p frame on a real PC, which halves the
            // frame rate. Use it only for big shrinks where COLORONCOLOR would drop whole rows of text.
            bool bigShrink = dest.Width * 2 < frame.Width;
            SetStretchBltMode(hdc, !oneToOne && bigShrink ? HALFTONE : COLORONCOLOR);
            SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
            fixed (byte* pixels = frame.Pixels)
            {
                StretchDIBits(hdc, dest.X, dest.Y, dest.Width, dest.Height, 0, 0, frame.Width, frame.Height,
                    (IntPtr)pixels, ref bmi, 0 /* DIB_RGB_COLORS */, 0x00CC0020 /* SRCCOPY */);
            }
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    private const int COLORONCOLOR = 3;
    private const int HALFTONE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc,
        int wSrc, int hSrc, IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);

    private static int RefreshRate(Screen screen)
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(screen.DeviceName, -1, ref mode) && mode.dmDisplayFrequency > 1)
            return mode.dmDisplayFrequency;
        return 60;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
