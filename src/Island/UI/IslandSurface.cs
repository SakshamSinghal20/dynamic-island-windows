using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Island.Core;
using Island.Services;

namespace Island.UI;

public sealed partial class IslandSurface : Canvas
{
    public readonly ActivityModel Model;
    public readonly IslandMotion Motion;
    public event Action? NeedsMotion, DestinationChanged, SettingsRequested;
    public event Action<bool>? ExpansionRequested;
    public event Action<double, bool>? Dragged;
    public event Action? DragReleased;
    private readonly Canvas shell = new(), viewport = new() { Width = 264, Height = 168, ClipToBounds = true }, navigation = new() { Width = 32, Height = 164 };
    private readonly Canvas[] pages = [new(), new(), new(), new()];
    private readonly TranslateTransform viewportOffset = new(), navigationOffset = new(), indicatorOffset = new(), compactOffset = new(), detailOffset = new();
    private readonly TranslateTransform[] pageOffsets = [new(), new(), new(), new()];
    private readonly Border indicator = new() { Width = 32, Height = 32, CornerRadius = new(16), Background = Theme.Control, IsHitTestVisible = false };
    private readonly PathGeometry outline = new();
    private double lastShapeWidth = double.NaN, lastShapeHeight = double.NaN, lastShapeRadius = double.NaN, lastShapeLeft = double.NaN;
    private readonly PathFigure figure = new() { IsClosed = true, IsFilled = true };
    private readonly LineSegment[] lines = [new(), new(), new(), new()];
    private readonly BezierSegment[] curves = [new(), new(), new(), new()];
    private readonly Border header = new() { Background = Brushes.Transparent, Height = 18, Cursor = Cursors.Arrow };
    private readonly TranslateTransform headerOffset = new();
    private readonly ScaleTransform headerScale = new();
    private readonly TranslateTransform compactTimerOffset = new();
    private readonly Canvas compact = new() { Width = 264, Height = 32, IsHitTestVisible = false };
    private readonly Image compactArt = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock compactTitle = Theme.Text("", 12, bold: true);
    private readonly TextBlock compactIdle = Theme.Text("Island", 12, Theme.Secondary, bold: true);
    public bool ShowIdle { get; set; }
    private readonly Viewbox compactIcon = Theme.Icon("timer", Theme.Orange, 18);
    private readonly Viewbox compactMusicIcon = Theme.Icon("music", Theme.Secondary, 18);
    private double compactMusicWidth = 260;
    private string measuredTitle = "";
    public double CompactWidth => Model.Media.Playing ? compactMusicWidth + (Model.Timer != null ? 75 : 0) : 210;
    private readonly RollingText compactTimer = new(14, Theme.Orange);
    private readonly Canvas wave = new() { Width = 25, Height = 24, IsHitTestVisible = false };
    private readonly TranslateTransform waveOffset = new();
    private readonly ScaleTransform[] waveScales = new ScaleTransform[7];
    public int Page { get; private set; } = 1;
    public int Detail { get; private set; }
    public bool Roomy { get; private set; }
    public bool Expanded { get; private set; }
    public bool ReducedMotion { get; set; }
    public bool PreventsCollapse => IsMouseCaptureWithin;
    public bool NeedsFrames => !Motion.IsSettled || (Model.Media.Playing && !ReducedMotion && (Detail != 3 && (Page == 0 || !Expanded)));
    public double AvailableWidth = 852, AvailableHeight = 836;
    public double ExpandedWidth => Math.Min(AvailableWidth, Detail == 3 ? (Roomy ? 820 : 620) : Detail == 2 ? 430 : 348);
    public double ExpandedHeight => Math.Min(AvailableHeight, Detail == 3 ? (Roomy ? 720 : 560) : 208 + (Detail == 1 ? 44 : Detail == 2 ? 390 : 0));
    private Point? dragStart;
    private bool dragging;
    private readonly Canvas detailHost = new();
    public readonly ChatPanel Chat;
    public IslandSurface(ActivityModel model, IslandMotion motion, ChatService chat, SpotifyService spotify)
    {
        Model = model; Motion = motion; Spotify = spotify;
        Width = 900; Height = 860; Background = null; ClipToBounds = true;
        shell.Width = Width; shell.Height = Height; shell.Background = Brushes.Black; shell.Clip = outline;
        figure.Segments.Add(lines[0]); figure.Segments.Add(curves[0]); figure.Segments.Add(lines[1]); figure.Segments.Add(curves[1]);
        figure.Segments.Add(lines[2]); figure.Segments.Add(curves[2]); figure.Segments.Add(lines[3]); figure.Segments.Add(curves[3]); outline.Figures.Add(figure);
        Children.Add(shell); shell.Children.Add(viewport); shell.Children.Add(navigation); navigation.Children.Add(indicator);
        viewport.RenderTransform = viewportOffset; navigation.RenderTransform = navigationOffset; indicator.RenderTransform = indicatorOffset;
        for (int i = 0; i < 4; i++)
        {
            int index = i; pages[i].Width = 264; pages[i].Height = 168; pages[i].RenderTransform = pageOffsets[i]; viewport.Children.Add(pages[i]);
            string[] names = ["Music", "Timer", "Clipboard", "Scratchpad"], icons = ["music", "timer", "clipboard", "note"];
            var button = Theme.Button(names[i], () => { SelectPage(index); if (index == 3) ScratchpadFocusRequested?.Invoke(); }, icons[i]);
            Theme.HoverNavigation(button, () => { if (Page != index || Detail != 0) SelectPage(index); });
            Theme.Place(navigation, button, 0, i * 44, 32, 32);
        }
        ConfigureTimer(); ConfigureMedia(); ConfigureClipboard(); ConfigureScratchpad();
        compact.RenderTransform = compactOffset; shell.Children.Add(compact);
        Theme.Place(compact, compactIcon, 13, 7, 18, 18); Theme.Place(compact, compactArt, 12, 5, 22, 22); Theme.Place(compact, compactTimer, 130, 6, 85, 22);
        Theme.Place(compact, compactTitle, 42, 8, 120, 18);
        compactIdle.TextAlignment = TextAlignment.Center; Theme.Place(compact, compactIdle, 18, 8, 80, 18);
        Theme.Place(compact, compactMusicIcon, 14, 7, 18, 18);
        compactTimer.RenderTransform = compactTimerOffset;
        wave.RenderTransform = waveOffset; shell.Children.Add(wave);
        for (int i = 0; i < 7; i++)
        {
            var bar = new Rectangle { Fill = Brushes.White, RadiusX = 1.25, RadiusY = 1.25, RenderTransformOrigin = new(.5, .5) };
            waveScales[i] = new(1, .5); bar.RenderTransform = waveScales[i]; Theme.Place(wave, bar, i * 3.5, 0, 2.2, 22);
        }
        Chat = new(chat); Chat.CloseRequested += () => SetDetail(0); Chat.SizeRequested += () => { Roomy = !Roomy; LayoutDetails(); DestinationChanged?.Invoke(); };
        detailHost.RenderTransform = detailOffset; shell.Children.Add(detailHost); detailHost.Children.Add(volumePanel); detailHost.Children.Add(tracksPanel); detailHost.Children.Add(Chat);
        shell.Children.Add(header); header.Width = 900; header.Height = 860;
        var headerTransform = new TransformGroup(); headerTransform.Children.Add(headerScale); headerTransform.Children.Add(headerOffset); header.RenderTransform = headerTransform;
        header.MouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(this); dragging = false; header.CaptureMouse(); e.Handled = true; };
        header.MouseMove += (_, e) =>
        {
            if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            double delta = e.GetPosition(this).Y - start.Y;
            if (Math.Abs(delta) > 3 || dragging) { Dragged?.Invoke(delta, !dragging); dragging = true; }
        };
        header.MouseLeftButtonUp += (_, e) =>
        { header.ReleaseMouseCapture(); dragStart = null; if (dragging) DragReleased?.Invoke(); else ExpansionRequested?.Invoke(!Expanded); dragging = false; e.Handled = true; };
        header.LostMouseCapture += (_, _) => { if (dragging) { dragging = false; dragStart = null; DragReleased?.Invoke(); } };
        LayoutDetails(); Refresh();
    }
    public void ResizeHost(double width, double height)
    { Width = shell.Width = width; Height = shell.Height = height; AvailableWidth = width - 48; AvailableHeight = height - 24; LayoutDetails(); }
    public void SelectPage(int index)
    {
        if (index is < 0 or > 3) return;
        if (Page == 3 && index != 3) _ = SaveScratchpad();
        Page = index; Motion.Tab.Retarget(index); SetDetail(0);
        if (ReducedMotion) Motion.Tab.Snap(); Refresh(); NeedsMotion?.Invoke();
    }
    public void SetDetail(int detail)
    {
        bool openingLibrary = detail == 2 && Detail != 2;
        Detail = detail; if (detail > 0) { Page = 0; Motion.Tab.Retarget(0); }
        LayoutDetails(); DestinationChanged?.Invoke(); NeedsMotion?.Invoke();
        if (openingLibrary) tracksPanel.Open();
    }
    private void LayoutDetails()
    {
        Chat.Width = Math.Max(200, ExpandedWidth - 78); Chat.Height = Math.Max(200, ExpandedHeight - 40);
        volumePanel.Visibility = Detail == 1 ? Visibility.Visible : Visibility.Hidden;
        tracksPanel.Visibility = Detail == 2 ? Visibility.Visible : Visibility.Hidden;
        if (Detail == 2) { tracksPanel.Width = Math.Max(264, ExpandedWidth - 36); tracksPanel.Height = Math.Max(180, ExpandedHeight - 208); }
        Chat.Visibility = Detail == 3 ? Visibility.Visible : Visibility.Hidden;
    }
    public void Refresh()
    {
        RefreshTimer(); RefreshMedia(); RefreshClipboard();
        compactTimer.Set(Model.Timer == null ? "" : Model.TimerText, ReducedMotion);
        bool idle = ShowIdle && !Model.HasActivity;
        compactIdle.Visibility = idle ? Visibility.Visible : Visibility.Hidden;
        compactTimer.Visibility = idle ? Visibility.Hidden : Visibility.Visible;
        compactArt.Source = Model.Media.Artwork; compactArt.Visibility = Model.Media.Playing && Model.Media.Artwork != null ? Visibility.Visible : Visibility.Hidden;
        compactTitle.Text = Model.Media.Title;
        compactTitle.Visibility = Model.Media.Playing ? Visibility.Visible : Visibility.Hidden;
        compactIcon.Visibility = !idle && !Model.Media.Playing && Model.Timer != null ? Visibility.Visible : Visibility.Hidden;
        compactMusicIcon.Visibility = Model.Media.Playing && Model.Media.Artwork == null ? Visibility.Visible : Visibility.Hidden;
        if (measuredTitle != Model.Media.Title)
        {
            measuredTitle = Model.Media.Title;
            var text = new FormattedText(measuredTitle, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(compactTitle.FontFamily, compactTitle.FontStyle, compactTitle.FontWeight, compactTitle.FontStretch),
                compactTitle.FontSize, compactTitle.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            compactMusicWidth = Math.Clamp(Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 94, 260, 360);
        }
        Apply(Expanded); if (NeedsFrames) NeedsMotion?.Invoke();
    }
    public bool Contains(Point point) => Motion.Height.Value > .01 && outline.FillContains(point);
    public bool BezelConnected => Motion.Height.Value < .01 || outline.FillContains(new Point(Width / 2, 0));
    public void Apply(bool expanded)
    {
        if (Expanded && !expanded) _ = SaveScratchpad();
        Expanded = expanded;
        double w = Math.Max(1, Motion.Width.Value), h = Math.Max(0, Motion.Height.Value), left = (Width - w) / 2, right = left + w;
        double top = -2, bottom = h, shoulder = Math.Min(10, (h + 2) * .22), r = Math.Min(Math.Max(0, Motion.Radius.Value), Math.Min(w / 2, h + 2 - shoulder)), k = .5522847498;
        // Playback can keep frames running after the shell settles. Reuse its
        // retained geometry instead of invalidating the clipping path each frame.
        if (w != lastShapeWidth || h != lastShapeHeight || r != lastShapeRadius || left != lastShapeLeft)
        {
            lastShapeWidth = w; lastShapeHeight = h; lastShapeRadius = r; lastShapeLeft = left;
            figure.StartPoint = new(left - shoulder, top); lines[0].Point = new(right + shoulder, top);
            curves[0].Point1 = new(right + shoulder * (1 - k), top); curves[0].Point2 = new(right, top + shoulder * (1 - k)); curves[0].Point3 = new(right, top + shoulder);
            lines[1].Point = new(right, bottom - r); curves[1].Point1 = new(right, bottom - r + k * r); curves[1].Point2 = new(right - r + k * r, bottom); curves[1].Point3 = new(right - r, bottom);
            lines[2].Point = new(left + r, bottom); curves[2].Point1 = new(left + r - k * r, bottom); curves[2].Point2 = new(left, bottom - r + k * r); curves[2].Point3 = new(left, bottom - r);
            lines[3].Point = new(left, top + shoulder); curves[3].Point1 = new(left, top + shoulder * (1 - k)); curves[3].Point2 = new(left - shoulder * (1 - k), top); curves[3].Point3 = new(left - shoulder, top);
        }
        shell.Opacity = h < .01 ? 0 : 1;
        double reveal = Math.Clamp(Motion.Reveal.Value, 0, 1), opacity = Math.Min(reveal, Math.Clamp((h - 52) / 138, 0, 1));
        viewportOffset.X = left + 18; viewportOffset.Y = 20; navigationOffset.X = right - 50; navigationOffset.Y = 20;
        viewport.Opacity = Detail == 3 ? 0 : opacity; navigation.Opacity = opacity;
        viewport.IsHitTestVisible = Detail != 3 && expanded && opacity > .96 && Motion.Tab.IsSettled;
        navigation.IsHitTestVisible = expanded && opacity > .96;
        for (int i = 0; i < 4; i++) { pageOffsets[i].Y = (i - Motion.Tab.Value) * 188; pages[i].Opacity = Math.Abs(i - Motion.Tab.Value) > 1.05 ? 0 : 1; }
        indicatorOffset.Y = Motion.Tab.Value * 44;
        compactOffset.X = left; compact.Opacity = 1 - reveal; compactTimerOffset.X = w - 210 + (Model.Media.Playing ? 8 : 0);
        Canvas.SetLeft(compactIdle, Math.Max(0, (w - 80) / 2));
        detailOffset.X = left + 18; detailOffset.Y = Detail == 3 ? 20 : 190;
        detailHost.Opacity = Detail == 3 ? opacity : Math.Min(opacity, Math.Clamp((h - 208) / Math.Max(1, ExpandedHeight - 208), 0, 1));
        detailHost.IsHitTestVisible = expanded && detailHost.Opacity > .96 && h >= ExpandedHeight - 3;
        headerOffset.X = left; headerScale.ScaleX = w / 900; headerScale.ScaleY = (expanded ? 18 : Math.Max(1, h)) / 860;
        double musicWeight = Math.Max(0, 1 - Math.Abs(Motion.Tab.Value));
        wave.Opacity = Detail == 3 || !Model.Media.Playing ? 0 : (1 - reveal) + opacity * musicWeight;
        double compactX = Model.Timer == null ? w - 40 : w - 115;
        // Keep the title clear of both the waveform and an active timer.
        // Its retained TextBlock shares the compact content's transition.
        compactTitle.Width = Math.Max(0, compactX - 50);
        waveOffset.X = left + compactX + (w - 91 - compactX) * reveal; waveOffset.Y = 6 + 24 * reveal - Motion.Tab.Value * 188 * opacity;
    }
    public void Frame(double time)
    {
        if (wave.Opacity > .001) for (int i = 0; i < 7; i++) waveScales[i].ScaleY = ReducedMotion ? .4 : Waveform.Height(i, time);
        if (Page == 0 && Expanded && Detail != 3 && !scrubbing) UpdateProgress();
    }
}
