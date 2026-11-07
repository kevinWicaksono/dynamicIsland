using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private bool _loading = true;   // true while the controls are being filled in (no live-apply)

    // A fixed list of common Windows fonts – the app never scans your computer for fonts.
    private static readonly string[] Fonts =
    {
        "Segoe UI", "Segoe UI Variable", "Arial", "Calibri", "Cambria", "Consolas",
        "Georgia", "Impact", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana", "Comic Sans MS"
    };

    private static readonly string[] Palette =
    {
        "#000000", "#1C1C1E", "#FFFFFF", "#30D158", "#0A84FF", "#FF453A",
        "#FF9F0A", "#FFD60A", "#BF5AF2", "#FF375F", "#64D2FF"
    };

    public SettingsWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;   // scrolls on small screens
        ShowAbout();

        foreach (var f in Fonts)
            FontBox.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f) });

        BuildSwatches(BorderSwatches, BorderHex);
        BuildSwatches(IslandSwatches, IslandHex);
        BuildSwatches(TextSwatches, TextHex);

        LoadControls();
        _loading = false;
    }

    private AppSettings P => _main.Prefs;

    // Version and author come from DynamicIsland.csproj (<Version> and <Company>)
    private void ShowAbout()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string version = asm.GetName().Version?.ToString(3) ?? "1.0.0";
        var company = (System.Reflection.AssemblyCompanyAttribute?)Attribute.GetCustomAttribute(
            asm, typeof(System.Reflection.AssemblyCompanyAttribute));

        AboutText.Text = string.IsNullOrWhiteSpace(company?.Company)
            ? $"Version {version}"
            : $"Version {version}  ·  by {company!.Company}";
    }

    private void LoadControls()
    {
        _loading = true;
        ScaleSlider.Value = P.Scale;
        ThicknessSlider.Value = P.BorderThickness;
        PosXSlider.Value = P.PositionX;
        PosYSlider.Value = P.OffsetY;
        CompactOpacitySlider.Value = P.CompactOpacity * 100;
        ExpandedOpacitySlider.Value = P.ExpandedOpacity * 100;
        Clock12.IsChecked = !P.Use24Hour;
        Clock24.IsChecked = P.Use24Hour;
        ShowLyricsCheckBox.IsChecked = P.ShowSpotifyLyrics;
        BorderHex.Text = P.BorderColor;
        IslandHex.Text = P.IslandColor;
        TextHex.Text = P.TextColor;

        FontBox.SelectedItem = FontBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string)i.Content, P.FontFamily, StringComparison.OrdinalIgnoreCase));
        if (FontBox.SelectedItem == null) FontBox.Text = P.FontFamily;

        ScaleLabel.Text = $"{P.Scale:0.00}×";
        ThicknessLabel.Text = $"{P.BorderThickness:0.0} px";
        PosXLabel.Text = PosXText(P.PositionX);
        PosYLabel.Text = $"{P.OffsetY:0} px";
        CompactOpacityLabel.Text = $"{P.CompactOpacity * 100:0}%";
        ExpandedOpacityLabel.Text = $"{P.ExpandedOpacity * 100:0}%";
        _loading = false;
    }

    private static void BuildSwatches(Panel panel, TextBox target)
    {
        foreach (var hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var btn = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x63, 0x66)),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex
            };
            btn.Click += (_, _) => target.Text = hex;   // triggers TextChanged → live apply
            panel.Children.Add(btn);
        }
    }

    // ── live-apply handlers ──

    private void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        P.Scale = Math.Round(e.NewValue, 2);
        ScaleLabel.Text = $"{P.Scale:0.00}×";
        _main.ApplySettings();
    }

    private void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        P.BorderThickness = e.NewValue;
        ThicknessLabel.Text = $"{P.BorderThickness:0.0} px";
        _main.ApplySettings();
    }

    private static string PosXText(double v) => v <= 0 ? "Left" : v >= 100 ? "Right" : Math.Abs(v - 50) < 0.5 ? "Center" : $"{v:0}%";

    private void PosX_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        P.PositionX = e.NewValue;
        PosXLabel.Text = PosXText(P.PositionX);
        _main.ApplySettings();
    }

    private void PosY_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        P.OffsetY = e.NewValue;
        PosYLabel.Text = $"{P.OffsetY:0} px";
        _main.ApplySettings();
    }

    private void PosLeft_Click(object sender, RoutedEventArgs e) => PosXSlider.Value = 0;
    private void PosCenter_Click(object sender, RoutedEventArgs e) => PosXSlider.Value = 50;
    private void PosRight_Click(object sender, RoutedEventArgs e) => PosXSlider.Value = 100;

    private void CompactOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        P.CompactOpacity = e.NewValue / 100.0;
        CompactOpacityLabel.Text = $"{e.NewValue:0}%";
        _main.ApplySettings();
    }

    private void ExpandedOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        P.ExpandedOpacity = e.NewValue / 100.0;
        ExpandedOpacityLabel.Text = $"{e.NewValue:0}%";
        _main.ApplySettings();
    }

    private void Clock_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        P.Use24Hour = Clock24.IsChecked == true;
        _main.ApplySettings();
    }

    private void ShowLyrics_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        P.ShowSpotifyLyrics = ShowLyricsCheckBox.IsChecked == true;
        _main.RefreshLyricsSetting();
    }

    private void Color_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        var tb = (TextBox)sender;
        var hex = tb.Text.Trim();
        if (!Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) return;   // ignore until it is a valid #RRGGBB

        switch ((string)tb.Tag)
        {
            case "border": P.BorderColor = hex; break;
            case "island": P.IslandColor = hex; break;
            case "text":   P.TextColor = hex;   break;
        }
        _main.ApplySettings();
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (FontBox.SelectedItem is ComboBoxItem item)
        {
            P.FontFamily = (string)item.Content;
            _main.ApplySettings();
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _main.Prefs = new AppSettings();
        _main.ApplySettings();
        LoadControls();
        _main.RefreshLyricsSetting();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        AppSettings.Save(_main.Prefs);
    }
}