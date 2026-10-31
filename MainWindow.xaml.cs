using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

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
    private bool _pinned;           // clicked → stays expanded until a click outside
    private bool _prevDown;
    private readonly DispatcherTimer _outsideTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _vizTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private System.Windows.Controls.Border[] _bars = Array.Empty<System.Windows.Controls.Border>();
    private readonly double[] _barH = new double[5];
    private double _level;
    private static readonly double[] BarSpeed = { 7.0, 9.5, 6.0, 11.0, 8.0 };
    private static readonly double[] BarPhase = { 0.0, 1.3, 2.6, 3.9, 5.2 };

    private bool _hasProgress;
    private double _progress;       // 0..1
    private double _perimeter;      // length of the border outline

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public AppSettings Prefs { get; set; } = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4 };
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
        Height = 210 * s.Scale;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) * (s.PositionX / 100.0);
        Top = Math.Min(s.OffsetY, Math.Max(0, area.Bottom - Height));

        var border = Frozen(ParseColor(s.BorderColor, Color.FromRgb(0x30, 0xD1, 0x58)));
        TrackPath.Stroke = border;
        ProgressPath.Stroke = border;
        foreach (var b in _bars) b.Background = border;
        TrackPath.StrokeThickness = s.BorderThickness;
        ProgressPath.StrokeThickness = s.BorderThickness;

        UpdateOutline();
        UpdateProgressVisual();
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

    // ───────────────────────── Click to pin ─────────────────────────

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // The island expands ONLY when clicked, and stays expanded until the next click outside it.
    private void Island_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_pinned) return;
        if (!_expanded && CompactPlayBtn.IsMouseOver) return;   // compact play/pause: just toggle, don't expand
        _pinned = true;
        _prevDown = true;
        if (!_expanded)
        {
            _expanded = true;
            SyncVolume();
            ApplySize();
        }
        _outsideTimer.Start();
    }

    // Only runs while pinned: it just asks "is a mouse button down, and is the pointer outside the island?"
    // Nothing is recorded and no other app's content is read.
    private void CheckOutsideClick()
    {
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (down && !_prevDown && !Island.IsMouseOver) Unpin();
        _prevDown = down;
    }

    private void Unpin()
    {
        _pinned = false;
        _outsideTimer.Stop();
        if (!Island.IsMouseOver)
        {
            _expanded = false;
            ApplySize();
        }
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

    private const double CompactPaddingX = 20;   // horizontal padding inside the compact island

    /// <summary>Compact width = width of its content + 20 px padding on the left and right.</summary>
    private double CompactWidth()
    {
        CompactPanel.Visibility = Visibility.Visible;   // must be visible to be measured
        CompactPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(CompactPanel.DesiredSize.Width) + CompactPaddingX * 2;
    }

    // Text (time, date) can change width, so keep the 20 px padding exact
    private void UpdateCompactWidth()
    {
        double w = CompactWidth();
        if (Math.Abs(w - Island.Width) > 1.5) Animate(Island, WidthProperty, w, 250);
    }

    private void ApplySize()
    {
        double w, h;
        if (_expanded) { w = 460; h = _hasMedia ? 180 : 108; }
        else           { w = CompactWidth(); h = 46; }

        Animate(Island, WidthProperty, w, 350);
        Animate(Island, HeightProperty, h, 350);
        Animate(Island, OpacityProperty, _expanded ? 1.0 : 0.8, 250);

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

    /// <summary>
    /// Five bars next to the play/pause icon. Heights follow the live output peak level of the
    /// default playback device (a single 0..1 number – no audio is captured).
    /// Bars are flat while paused.
    /// </summary>
    private void UpdateVisualizer()
    {
        if (!_hasMedia || CompactPanel.Visibility != Visibility.Visible) return;

        const double minH = 3, maxH = 20;

        double raw = 0;
        if (_isPlaying)
        {
            var peak = AudioVolume.GetPeak();
            raw = peak ?? 0.4;      // meter unavailable → gentle idle animation instead
        }

        // fast attack, slow decay
        _level = raw > _level ? raw : _level * 0.85 + raw * 0.15;
        double lvl = Math.Min(1.0, Math.Sqrt(_level) * 1.4);

        double t = Environment.TickCount64 / 1000.0;
        for (int i = 0; i < _bars.Length; i++)
        {
            double wave = 0.5 + 0.5 * Math.Sin(t * BarSpeed[i] + BarPhase[i]);
            double target = minH + (maxH - minH) * lvl * (0.35 + 0.65 * wave);
            _barH[i] += (target - _barH[i]) * 0.5;
            _bars[i].Height = Math.Max(minH, _barH[i]);
        }
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
