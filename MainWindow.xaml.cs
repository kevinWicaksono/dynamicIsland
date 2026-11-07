using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using System.Windows.Documents;

namespace DynamicIsland;

/// <summary>
/// A Dynamic-Island-style overlay for Windows.
///  - Offline: contains no networking code.
///  - Media info comes live from Windows' media-session service and is kept in memory only.
///  - The only thing saved to disk is the user's own appearance settings.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private SettingsWindow? _settingsWindow;

    private bool _expanded;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _syncingVolume;
    private bool _prevDown;
    private readonly DispatcherTimer _outsideTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _vizTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private System.Windows.Controls.Border[] _bars = Array.Empty<System.Windows.Controls.Border>();      // compact view
    private System.Windows.Controls.Border[] _barsExp = Array.Empty<System.Windows.Controls.Border>();   // expanded view
    private readonly double[] _barH = new double[7];
    private double _level;
    private double _olX0, _olY0, _olX1, _olY1, _olR;   // the border outline (used for click-to-seek)
    private long _seekHoldUntil;             // after a jump, ignore stale player timeline for a moment
    private bool _scrubbing;                 // dragging on the progress border
    private double _scrubFraction;
    private System.Windows.Controls.ToolTip? _hintTip;   // small "1:23 / 3:45" bubble
    private DispatcherTimer? _hintTimer;
    private static readonly double[] BarSpeed = { 7.0, 9.5, 6.0, 11.0, 8.0, 10.0, 6.5 };
    private static readonly double[] BarPhase = { 0.0, 1.3, 2.6, 3.9, 5.2, 0.7, 2.0 };

    private bool _hasProgress;
    private double _progress;       // 0..1
    private double _perimeter;      // length of the border outline

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public AppSettings Prefs { get; set; } = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5, Bar6 };
        _barsExp = new[] { ExpBar0, ExpBar1, ExpBar2, ExpBar3, ExpBar4, ExpBar5, ExpBar6 };
        ApplySettings();

        UpdateClock();
        _clock.Tick += (_, _) =>
        {
            UpdateClock();
            if (_expanded && !VolumeSlider.IsMouseCaptureWithin) SyncVolume();
        };
        _clock.Start();

        _outsideTimer.Tick += (_, _) => CheckOutsideClick();
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();

        _vizTimer.Tick += (_, _) => UpdateVisualizer();
        _vizTimer.Start();

        Loaded += async (_, _) =>
        {
            SyncVolume();
            await InitMediaAsync();
        };
    }

    // ───────────────────────── Settings ─────────────────────────

    /// <summary>Applies Prefs to the live UI (called at startup and by the Settings window).</summary>
    public void ApplySettings()
    {
        var s = Prefs;

        var text = ParseColor(s.TextColor, Colors.White);
        Resources["TextBrush"] = Frozen(text);
        Resources["SubBrush"] = Frozen(Color.FromArgb(0xB3, text.R, text.G, text.B));

        Island.Background = Frozen(ParseColor(s.IslandColor, Colors.Black));

        try { TextElement.SetFontFamily(Island, new FontFamily(s.FontFamily)); }
        catch { TextElement.SetFontFamily(Island, new FontFamily("Segoe UI")); }

        Island.LayoutTransform = new ScaleTransform(s.Scale, s.Scale);
        Width = 520 * s.Scale;
        Height = 230 * s.Scale;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) * (s.PositionX / 100.0);
        Top = Math.Min(s.OffsetY, Math.Max(0, area.Bottom - Height));

        var border = Frozen(ParseColor(s.BorderColor, Color.FromRgb(0x30, 0xD1, 0x58)));
        TrackPath.Stroke = border;
        ProgressPath.Stroke = border;
        foreach (var b in _bars.Concat(_barsExp)) b.Background = border;
        TrackPath.StrokeThickness = s.BorderThickness;
        ProgressPath.StrokeThickness = s.BorderThickness;

        UpdateOutline();
        UpdateProgressVisual();
        Island.BeginAnimation(OpacityProperty, null);   // stop any running fade, then apply the new value
        Island.Opacity = _expanded ? s.ExpandedOpacity : s.CompactOpacity;

        UpdateClock();   // apply the clock format right away
        if (!_expanded) UpdateCompactWidth();
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // ───────────────────────── Clock ─────────────────────────

    private void UpdateClock()
    {
        var now = DateTime.Now;
        CompactDate.Text = now.ToString("ddd, d MMM");
        // 24-hour: 14:05 / 14:05:09     12-hour: 2:05 PM / 2:05:09 PM
        CompactTime.Text = now.ToString(Prefs.Use24Hour ? "HH:mm" : "h:mm tt");
        TimeText.Text = now.ToString(Prefs.Use24Hour ? "HH:mm:ss" : "h:mm:ss tt");
        DateText.Text = now.ToString("dddd, d MMMM");
        if (!_expanded) UpdateCompactWidth();
    }

    // ───────────────────────── Expand / collapse ─────────────────────────

    // ───────────────────────── Click behaviour ─────────────────────────
    //  • click the collapsed island                       → expands
    //  • click empty space on the expanded island,
    //    or anywhere outside it                            → collapses
    //  • click a button / the slider / the album art       → does its own job, island stays open

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void Island_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Pressing on the border (or the thin halo just outside it): jump / scrub in the track.
        // Works when the island is collapsed or expanded.
        if (Prefs.BorderThickness > 0.05 &&
            TryGetProgressFraction(e.GetPosition(Island), out double fraction))
        {
            if (_hasProgress)
            {
                BeginScrub(fraction);
                e.Handled = true;
                return;
            }

            if (_expanded && _hasMedia)
            {
                ShowHint("This player doesn't report the track length", 2500);
                e.Handled = true;
                return;
            }
        }

        bool onHalo = ReferenceEquals(e.OriginalSource, Halo);   // the invisible ring around the island

        if (!_expanded)
        {
            if (onHalo || CompactPlayBtn.IsMouseOver) return;     // only the island itself expands; play/pause just toggles
            Expand();
            return;
        }

        if (onHalo || !IsInteractive(e.OriginalSource as DependencyObject))
            Collapse();                                           // clicked empty space, or outside the island
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        _prevDown = true;          // the press that expanded it must not count as an "outside" click
        SyncVolume();
        ApplySize();
        _outsideTimer.Start();
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _outsideTimer.Stop();
        ApplySize();
    }

    // True when the clicked element is (inside) a button, the volume slider or the album art.
    private bool IsInteractive(DependencyObject? d)
    {
        while (d != null && !ReferenceEquals(d, Island))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase ||
                d is System.Windows.Controls.Primitives.RangeBase ||
                d is System.Windows.Controls.Primitives.Thumb ||
                ReferenceEquals(d, AlbumHost))
                return true;

            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // Only runs while expanded: it just asks "is a mouse button down, and is the pointer outside the island?"
    // Nothing is recorded and no other app's content is read.
    private void CheckOutsideClick()
    {
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (_scrubbing) { _prevDown = down; return; }
        if (down && !_prevDown && !Halo.IsMouseOver) Collapse();
        _prevDown = down;
    }

    // Scroll over the expanded island → volume in 2% steps
    private void Island_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!_expanded) return;
        double step = e.Delta > 0 ? 2 : -2;
        double v = Math.Round(VolumeSlider.Value / 2) * 2 + step;
        VolumeSlider.Value = Math.Clamp(v, 0, 100);
        e.Handled = true;
    }

    // ───────────────────────── Album art click → bring the player to the front ─────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    private void Album_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        BringPlayerToFront();
        e.Handled = true;
    }

    /// <summary>
    /// Finds the window of the app that is playing the media and brings it to the front.
    /// Best effort: it matches the media session's app id to a running process, and prefers the
    /// window whose title contains the track title (handy for browsers with many windows).
    /// Runs only when the album art is clicked; nothing is stored.
    /// </summary>
    private void BringPlayerToFront()
    {
        var session = _session;
        if (session == null) return;

        string appId = session.SourceAppUserModelId ?? "";
        string trackTitle = TitleText.Text ?? "";

        // 1) Which processes belong to the player?
        string? exactName = null;
        var keywords = new List<string>();
        void AddKeyword(string k)
        {
            if (k.Length >= 4 && !keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) keywords.Add(k);
        }

        string id = appId;
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^4];
            exactName = id;                                   // e.g. "Spotify" / "vlc"
        }

        int bang = id.IndexOf('!');
        if (bang >= 0)                                        // packaged app, e.g. "SpotifyAB.SpotifyMusic_xxx!Spotify"
        {
            AddKeyword(id[(bang + 1)..]);
            string pkg = id[..bang];
            int us = pkg.IndexOf('_');
            if (us >= 0) pkg = pkg[..us];
            AddKeyword(pkg[(pkg.LastIndexOf('.') + 1)..]);
        }
        else
        {
            AddKeyword(id);                                   // e.g. "Chrome", "MSEdge"
            AddKeyword(id[(id.LastIndexOf('.') + 1)..]);
        }

        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                bool hit = (exactName != null && n.Equals(exactName, StringComparison.OrdinalIgnoreCase))
                           || keywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (hit) pids.Add((uint)p.Id);
            }
            catch { /* protected process – ignore */ }
            finally { p.Dispose(); }
        }

        // 2) Pick the best visible top-level window
        IntPtr procAndTitle = IntPtr.Zero, titleOnly = IntPtr.Zero, procOnly = IntPtr.Zero;
        uint self = (uint)Environment.ProcessId;

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true;   // skip tool windows
            int len = GetWindowTextLength(h);
            if (len == 0) return true;

            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;                           // not our own windows

            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            string t = sb.ToString();

            bool byProc = pids.Contains(pid);
            bool byTitle = trackTitle.Length > 0 && t.Contains(trackTitle, StringComparison.OrdinalIgnoreCase);

            if (byProc && byTitle) { if (procAndTitle == IntPtr.Zero) procAndTitle = h; }
            else if (byTitle)      { if (titleOnly == IntPtr.Zero) titleOnly = h; }
            else if (byProc)       { if (procOnly == IntPtr.Zero) procOnly = h; }
            return true;
        }, IntPtr.Zero);

        IntPtr target = procAndTitle != IntPtr.Zero ? procAndTitle
                      : titleOnly != IntPtr.Zero ? titleOnly
                      : procOnly;
        if (target == IntPtr.Zero) return;                          // couldn't find it – do nothing

        // 3) Bring it forward
        if (IsIconic(target)) ShowWindow(target, 9);                // SW_RESTORE
        if (!SetForegroundWindow(target) || GetForegroundWindow() != target)
            SwitchToThisWindow(target, true);
    }

    private const double CompactPadding = 14;    // padding inside the collapsed island – the same on all four sides

    /// <summary>Collapsed size = size of its content + the same padding on every side.</summary>
    private double CompactWidth()
    {
        CompactPanel.Visibility = Visibility.Visible;   // must be visible to be measured
        CompactPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(CompactPanel.DesiredSize.Width) + CompactPadding * 2;
    }

    // Call right after CompactWidth (which measures the panel): same padding on top and bottom
    private double CompactHeight()
    {
        return Math.Ceiling(CompactPanel.DesiredSize.Height) + CompactPadding * 2;
    }

    // Text (time, date) can change width, so keep the 20 px padding exact
    private void UpdateCompactWidth()
    {
        double w = CompactWidth();
        double h = CompactHeight();
        if (Math.Abs(w - Island.Width) > 1.5) Animate(Island, WidthProperty, w, 250);
        if (Math.Abs(h - Island.Height) > 1.5) Animate(Island, HeightProperty, h, 250);
    }

    private void ApplySize()
    {
        double w, h;
        if (_expanded) { w = 460; h = _hasMedia ? 200 : 90; }
        else           { w = CompactWidth(); h = CompactHeight(); }

        Animate(Island, WidthProperty, w, 350);
        Animate(Island, HeightProperty, h, 350);
        Animate(Island, OpacityProperty, _expanded ? Prefs.ExpandedOpacity : Prefs.CompactOpacity, 250);

        if (_expanded)
        {
            ExpandedPanel.Visibility = Visibility.Visible;
            CompactPanel.Visibility = Visibility.Collapsed;
            ExpandedPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)) { BeginTime = TimeSpan.FromMilliseconds(120) });
        }
        else
        {
            ExpandedPanel.BeginAnimation(OpacityProperty, null);
            ExpandedPanel.Opacity = 0;
            ExpandedPanel.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = Visibility.Visible;
        }
    }

    private static void Animate(FrameworkElement el, DependencyProperty prop, double to, int ms)
    {
        el.BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
    }

    // Keep the pill shape and the border outline in sync while the size animates
    private void Island_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Island.CornerRadius = new CornerRadius(Math.Min(e.NewSize.Height / 2, 36));
        UpdateOutline();
    }

    // ───────────────────────── Border outline + progress ─────────────────────────

    /// <summary>
    /// Builds a rounded outline (starting at top-centre, running clockwise) that is used both
    /// for the static border and for the playback-progress arc.
    /// </summary>
    private void UpdateOutline()
    {
        double t = Prefs.BorderThickness;
        double w = Island.ActualWidth, h = Island.ActualHeight;
        if (w <= 0 || h <= 0 || t <= 0) return;

        double radius = Math.Min(h / 2, 36);
        double inset = t / 2;
        double r = Math.Max(0.5, radius - inset);
        double x0 = inset, y0 = inset, x1 = w - inset, y1 = h - inset;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(w / 2, y0), false, true);
            c.LineTo(new Point(x1 - r, y0), true, false);
            c.ArcTo(new Point(x1, y0 + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x1, y1 - r), true, false);
            c.ArcTo(new Point(x1 - r, y1), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0 + r, y1), true, false);
            c.ArcTo(new Point(x0, y1 - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0, y0 + r), true, false);
            c.ArcTo(new Point(x0 + r, y0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        _olX0 = x0; _olY0 = y0; _olX1 = x1; _olY1 = y1; _olR = r;

        TrackPath.Data = g;
        ProgressPath.Data = g;

        _perimeter = 2 * ((x1 - x0) - 2 * r) + 2 * ((y1 - y0) - 2 * r) + 2 * Math.PI * r;
        UpdateDash();
    }

    private void UpdateDash()
    {
        double t = Prefs.BorderThickness;
        if (t <= 0 || _perimeter <= 0) return;

        double p = Math.Clamp(_progress, 0, 1);
        // Dash lengths are measured in multiples of the stroke thickness.
        ProgressPath.StrokeDashArray = new DoubleCollection { p * _perimeter / t, _perimeter / t + 2 };
    }

    private void UpdateProgressVisual()
    {
        bool show = Prefs.BorderThickness > 0.05;
        TrackPath.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // With media progress: the full outline becomes a dim track behind the progress arc.
        TrackPath.Opacity = _hasProgress ? 0.3 : 1.0;
        ProgressPath.Visibility = (show && _hasProgress) ? Visibility.Visible : Visibility.Collapsed;
        UpdateDash();
    }

    private void SetProgress(bool has, double value)
    {
        bool changed = has != _hasProgress;
        _hasProgress = has;
        _progress = value;
        if (changed) UpdateProgressVisual();
        else if (has) UpdateDash();
    }

    private void UpdateProgress()
    {
        var session = _session;
        if (session == null || !_hasMedia) return;
        if (Environment.TickCount64 < _seekHoldUntil) return;   // just jumped: wait for the player to report the new position

        try
        {
            var tl = session.GetTimelineProperties();
            double span = (tl.EndTime - tl.StartTime).TotalSeconds;
            if (span < 1) { SetProgress(false, 0); return; }   // player doesn't report a length

            var pos = tl.Position;
            if (_isPlaying) pos += DateTimeOffset.Now - tl.LastUpdatedTime;

            double p = (pos - tl.StartTime).TotalSeconds / span;
            SetProgress(true, Math.Clamp(p, 0, 1));
        }
        catch
        {
            SetProgress(false, 0);
        }
    }

    // ───────────────────────── Media ─────────────────────────

    // ───────────────────────── Audio visualizer ─────────────────────────

    private readonly double[] _hist = new double[4];   // output level history: newest → oldest
    private double _peakRef = 0.1;                      // slow auto-gain reference
    private long _lastShiftMs;

    // How "picky" the bars are. Higher = bars stay lower and only the loudest hits reach the top.
    // 1.0 = linear (easy to reach the top), 2.0 = default, 3.0 = very picky.
    private const double VisualizerContrast = 3.0;

    /// <summary>
    /// Seven bars next to the play/pause icon (and next to the album art when expanded), driven by the live output level of the default playback
    /// device (a single 0..1 number – no audio is captured). The centre bar shows the level right now
    /// and the outer bars show slightly older levels, so every beat visibly ripples outward.
    /// An auto-gain keeps the bars lively on quiet and loud tracks alike. Bars are flat while paused.
    /// </summary>
    private void UpdateVisualizer()
    {
        if (!_hasMedia) return;

        const double minH = 2, maxH = 13;
        long nowMs = Environment.TickCount64;
        double t = nowMs / 1000.0;

        double lvl = 0;
        bool simulated = false;

        if (_isPlaying)
        {
            var peak = AudioVolume.GetPeak();
            if (peak.HasValue)
            {
                double raw = peak.Value;
                // auto-gain: the reference follows the loudest recent peak and decays slowly
                _peakRef = Math.Max(0.08, Math.Max(raw, _peakRef * 0.997));
                lvl = Math.Pow(Math.Min(1.0, raw / _peakRef), VisualizerContrast);
            }
            else
            {
                simulated = true;       // level meter unavailable → gentle idle animation
            }
        }

        // newest level updates every frame; history shifts every ~70 ms
        _hist[0] = lvl;
        if (nowMs - _lastShiftMs >= 70)
        {
            _hist[3] = _hist[2];
            _hist[2] = _hist[1];
            _hist[1] = _hist[0];
            _lastShiftMs = nowMs;
        }

        // mirrored layout: oldest · older · old · NEWEST · old · older · oldest
        double[] levels = { _hist[3], _hist[2], _hist[1], _hist[0], _hist[1], _hist[2], _hist[3] };

        for (int i = 0; i < _bars.Length; i++)
        {
            double level = simulated
                ? 0.35 + 0.25 * Math.Sin(t * BarSpeed[i] + BarPhase[i])
                : levels[i];

            double target = minH + (maxH - minH) * level;
            double k = target > _barH[i] ? 0.85 : 0.35;          // fast attack, quick release
            _barH[i] += (target - _barH[i]) * k;
            _bars[i].Height = Math.Max(minH, _barH[i]);

            // same movement, taller, for the bars in the expanded view
            double frac = (Math.Max(minH, _barH[i]) - minH) / (maxH - minH);
            _barsExp[i].Height = 2 + 12 * frac;
        }
    }

    // ───────────────────────── Click the border to jump in the track ─────────────────────────

    // While dragging: follow the pointer around the border. Otherwise: hand cursor over the border.
    private void Island_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(Island);

        if (_scrubbing)
        {
            if (TryGetProgressFraction(pos, out double f, anyDistance: true)) UpdateScrub(f);
            return;
        }

        bool onBar = _hasProgress && Prefs.BorderThickness > 0.05 && TryGetProgressFraction(pos, out _);
        Halo.Cursor = onBar ? System.Windows.Input.Cursors.Hand : null;
    }

    /// <summary>
    /// If the point (in island coordinates) is on, or within a few pixels of, the border outline, returns how far
    /// around the outline it is: 0..1, starting at the top centre and running clockwise (the progress arc's scale).
    /// With anyDistance = true it returns the nearest point however far away (used while dragging).
    /// </summary>
    private bool TryGetProgressFraction(Point p, out double fraction, bool anyDistance = false)
    {
        fraction = 0;
        if (_olR <= 0 || _perimeter <= 0) return false;

        double x0 = _olX0, y0 = _olY0, x1 = _olX1, y1 = _olY1, r = _olR;
        double cx = (x0 + x1) / 2;
        double bestD = double.MaxValue, bestLen = 0, cum = 0;

        // nearest point on a straight piece of the outline
        void Straight(double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0)
            {
                double t = Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / (len * len), 0, 1);
                double qx = ax + t * dx, qy = ay + t * dy;
                double d = (p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy);
                if (d < bestD) { bestD = d; bestLen = cum + t * len; }
            }
            cum += len;
        }

        // nearest point on a quarter-circle corner (angles in radians, a0 < a1, clockwise on screen)
        void Arc(double ccx, double ccy, double a0, double a1)
        {
            double ang = Math.Atan2(p.Y - ccy, p.X - ccx);
            if (ang < a0 - Math.PI) ang += 2 * Math.PI;
            else if (ang > a0 + Math.PI) ang -= 2 * Math.PI;
            double a = Math.Clamp(ang, a0, a1);
            double qx = ccx + r * Math.Cos(a), qy = ccy + r * Math.Sin(a);
            double d = (p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy);
            if (d < bestD) { bestD = d; bestLen = cum + r * (a - a0); }
            cum += r * (a1 - a0);
        }

        double q = Math.PI / 2;
        Straight(cx, y0, x1 - r, y0);                 // top edge, right half
        Arc(x1 - r, y0 + r, -q, 0);                   // top-right corner
        Straight(x1, y0 + r, x1, y1 - r);             // right edge
        Arc(x1 - r, y1 - r, 0, q);                    // bottom-right corner
        Straight(x1 - r, y1, x0 + r, y1);             // bottom edge
        Arc(x0 + r, y1 - r, q, 2 * q);                // bottom-left corner
        Straight(x0, y1 - r, x0, y0 + r);             // left edge
        Arc(x0 + r, y0 + r, 2 * q, 3 * q);            // top-left corner
        Straight(x0 + r, y0, cx, y0);                 // top edge, left half

        if (cum <= 0) return false;

        // The line is thin, so accept clicks in a wider band around it (and on the halo just outside it)
        double tolerance = Math.Max(10, Prefs.BorderThickness / 2 + 6);
        if (!anyDistance && bestD > tolerance * tolerance) return false;

        fraction = Math.Clamp(bestLen / cum, 0, 1);
        return true;
    }

    private void BeginScrub(double fraction)
    {
        _scrubbing = true;
        _prevDown = true;
        Island.CaptureMouse();          // keep receiving mouse moves even if the pointer leaves the island
        UpdateScrub(fraction);
    }

    private void UpdateScrub(double fraction)
    {
        _scrubFraction = Math.Clamp(fraction, 0, 1);
        _seekHoldUntil = long.MaxValue;     // while dragging, ignore the player's own timeline updates
        SetProgress(true, _scrubFraction);

        try
        {
            var tl = _session?.GetTimelineProperties();
            if (tl != null)
            {
                var span = tl.EndTime - tl.StartTime;
                ShowHint($"{FormatTime(TimeSpan.FromTicks((long)(span.Ticks * _scrubFraction)))} / {FormatTime(span)}");
            }
        }
        catch { /* no timeline – the arc still moves */ }
    }

    private void Island_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        Island.ReleaseMouseCapture();
        e.Handled = true;
        CommitScrub();
    }

    private void Island_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;                 // capture lost (for example Alt+Tab): cancel without jumping
        _seekHoldUntil = 0;
        CloseHint();
    }

    private async void CommitScrub()
    {
        bool ok = await SeekToFractionAsync(_scrubFraction);
        if (ok) CloseHint();
        else ShowHint("This player doesn't allow jumping in the track", 2500);
    }

    private async Task<bool> SeekToFractionAsync(double fraction)
    {
        var session = _session;
        if (session == null) return false;
        fraction = Math.Clamp(fraction, 0, 1);

        try
        {
            var tl = session.GetTimelineProperties();
            var span = tl.EndTime - tl.StartTime;
            if (span.TotalSeconds < 1) { _seekHoldUntil = 0; return false; }

            // show the jump right away; the player reports the new position a moment later
            _seekHoldUntil = Environment.TickCount64 + 1200;
            SetProgress(true, fraction);

            var target = tl.StartTime + TimeSpan.FromTicks((long)(span.Ticks * fraction));
            bool ok = await session.TryChangePlaybackPositionAsync(target.Ticks);
            if (!ok)
            {
                _seekHoldUntil = 0;         // the player refused: go back to its real position
                UpdateProgress();
            }
            return ok;
        }
        catch
        {
            _seekHoldUntil = 0;
            return false;
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // A small bubble under the island: shows the time while dragging, or a short message
    private void ShowHint(string text, int autoCloseMs = 0)
    {
        _hintTip ??= new System.Windows.Controls.ToolTip
        {
            PlacementTarget = Island,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 6,
            Background = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 5, 10, 5),
            StaysOpen = true,
            Focusable = false,
            IsHitTestVisible = false
        };

        _hintTip.Content = text;

        // centre it under the island
        _hintTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _hintTip.HorizontalOffset = (Island.ActualWidth * Prefs.Scale - _hintTip.DesiredSize.Width) / 2;
        _hintTip.IsOpen = true;

        if (_hintTimer == null)
        {
            _hintTimer = new DispatcherTimer();
            _hintTimer.Tick += (_, _) => CloseHint();
        }
        _hintTimer.Stop();
        if (autoCloseMs > 0)
        {
            _hintTimer.Interval = TimeSpan.FromMilliseconds(autoCloseMs);
            _hintTimer.Start();
        }
    }

    private void CloseHint()
    {
        _hintTimer?.Stop();
        if (_hintTip != null) _hintTip.IsOpen = false;
    }

    private async Task InitMediaAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Dispatcher.InvokeAsync(AttachSession);
            AttachSession();
        }
        catch
        {
            SetNoMedia();
        }
    }

    private void AttachSession()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnMediaChanged;
            _session.TimelinePropertiesChanged -= OnMediaChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session == null)
        {
            SetNoMedia();
            return;
        }

        _session.MediaPropertiesChanged += OnMediaChanged;
        _session.PlaybackInfoChanged += OnMediaChanged;
        _session.TimelinePropertiesChanged += OnMediaChanged;
        _ = RefreshMediaAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object args)
        => Dispatcher.InvokeAsync(RefreshMediaAsync);

    private async Task RefreshMediaAsync()
    {
        var session = _session;
        if (session == null) { SetNoMedia(); return; }

        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            var props = await session.TryGetMediaPropertiesAsync();

            bool active =
                status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

            if (!active || string.IsNullOrWhiteSpace(props.Title))
            {
                SetNoMedia();
                return;
            }

            _isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            TitleText.Text = props.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(props.Artist) ? "Unknown artist" : props.Artist;
            PlayPauseBtn.Content = _isPlaying ? "\uE103" : "\uE102";
            CompactPlayBtn.Content = _isPlaying ? "\uE103" : "\uE102";   // pause icon while playing, play icon while paused
            CompactMedia.Visibility = Visibility.Visible;
            MediaPanel.Visibility = Visibility.Visible;

            await LoadAlbumArtAsync(props);

            if (!_hasMedia)
            {
                _hasMedia = true;
                ApplySize();
            }

            UpdateProgress();
        }
        catch
        {
            SetNoMedia();
        }
    }

    // Album art is decoded in memory only – never written anywhere.
    private async Task LoadAlbumArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        AlbumArt.Source = null;
        if (props.Thumbnail == null) return;

        try
        {
            using var winrtStream = await props.Thumbnail.OpenReadAsync();
            var size = (uint)winrtStream.Size;
            var bytes = new byte[size];
            using (var reader = new Windows.Storage.Streams.DataReader(winrtStream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(bytes);
            }
            var ms = new System.IO.MemoryStream(bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            AlbumArt.Source = bmp;
        }
        catch { /* no artwork – placeholder note is shown */ }
    }

    private void SetNoMedia()
    {
        _isPlaying = false;
        MediaPanel.Visibility = Visibility.Collapsed;
        CompactMedia.Visibility = Visibility.Collapsed;
        AlbumArt.Source = null;

        _hasProgress = false;
        _progress = 0;
        UpdateProgressVisual();

        if (_hasMedia)
        {
            _hasMedia = false;
            ApplySize();
        }
    }

    // ───────────────────────── Media controls ─────────────────────────

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null) await _session.TryTogglePlayPauseAsync();
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null) await _session.TrySkipPreviousAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null) await _session.TrySkipNextAsync();
    }

    // ───────────────────────── Volume ─────────────────────────

    private void SyncVolume()
    {
        var v = AudioVolume.GetVolume();
        var muted = AudioVolume.GetMute();

        _syncingVolume = true;
        if (v.HasValue) VolumeSlider.Value = v.Value * 100;
        VolumeLabel.Text = $"{Math.Round(VolumeSlider.Value):0}%";
        VolumeBtn.Content = (muted == true || v == 0) ? "\uE74F" : "\uE767";
        _syncingVolume = false;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeLabel != null) VolumeLabel.Text = $"{Math.Round(e.NewValue):0}%";
        if (_syncingVolume || !IsLoaded) return;

        AudioVolume.SetVolume((float)(e.NewValue / 100.0));
        if (e.NewValue > 0) AudioVolume.SetMute(false);
        VolumeBtn.Content = e.NewValue <= 0 ? "\uE74F" : "\uE767";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        bool muted = AudioVolume.GetMute() == true;
        AudioVolume.SetMute(!muted);
        SyncVolume();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}