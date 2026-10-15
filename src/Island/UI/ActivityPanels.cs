using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Island.Core;
using Island.Services;

namespace Island.UI;

public sealed partial class IslandSurface
{
    private readonly RollingText timerValue = new(42, Theme.Orange);
    private readonly TextBlock timerSubtitle = Theme.Text("Timer", 13, Theme.Secondary);
    private readonly TextBox timerEntry = Theme.Input("25", "Timer duration in minutes");
    private Button timerPause = null!, timerStop = null!, timerExtend = null!, timerStart = null!;
    private readonly List<Button> presets = [];
    private void ConfigureTimer()
    {
        Canvas page = pages[1]; Theme.Place(page, Theme.Icon("timer", Theme.Orange), 0, 7, 20, 20); Theme.Place(page, timerSubtitle, 29, 8, 230, 20);
        Theme.Place(page, timerValue, 0, 31, 176, 57);
        timerPause = Theme.Button("Pause or resume timer", Model.PauseTimer, "pause", Theme.Orange, true);
        timerStop = Theme.Button("End timer", Model.StopTimer, "close", filled: true); timerExtend = Theme.Button("Add one minute", Model.ExtendTimer, "plus", filled: true);
        Theme.Place(page, timerPause, 176, 39, 38, 44); Theme.Place(page, timerStop, 226, 39, 38, 44); Theme.Place(page, timerExtend, 0, 102, 32, 32);
        for (int i = 0; i < 3; i++) { int minutes = new[] { 5, 15, 25 }[i]; var button = Theme.Button($"{minutes} min", () => Model.StartTimer(minutes), filled: true); presets.Add(button); Theme.Place(page, button, i * 50, 102, 44, 32); }
        timerEntry.TextAlignment = TextAlignment.Center; Theme.Place(page, timerEntry, 161, 103, 45, 30);
        timerStart = Theme.Button("Start timer", StartCustom, "play", Theme.Orange, true); Theme.Place(page, timerStart, 222, 102, 42, 32);
        timerEntry.KeyDown += (_, e) => { if (e.Key == Key.Enter) { StartCustom(); e.Handled = true; } };
    }
    private void StartCustom()
    { if (double.TryParse(timerEntry.Text, out double value)) Model.StartTimer(value); else Model.SetError("Enter 1–1,440 minutes."); Keyboard.ClearFocus(); }
    private bool? previousPaused;
    private void RefreshTimer()
    {
        timerValue.Set(Model.TimerText, ReducedMotion); bool active = Model.Timer != null;
        timerSubtitle.Text = Model.Error.Length > 0 ? Model.Error : Model.Finished ? "Time's up" : Model.Timer?.IsPaused == true ? "Paused" : "Timer";
        foreach (var button in presets) button.Visibility = active ? Visibility.Hidden : Visibility.Visible;
        timerEntry.Visibility = timerStart.Visibility = active ? Visibility.Hidden : Visibility.Visible;
        timerPause.Visibility = timerStop.Visibility = timerExtend.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        if (previousPaused != Model.Timer?.IsPaused) { previousPaused = Model.Timer?.IsPaused; timerPause.Content = Theme.Icon(Model.Timer?.IsPaused == true ? "play" : "pause", Theme.Orange); }
    }

    public readonly SpotifyService Spotify;
    public readonly VolumeService Volume = new();
    private readonly TextBlock musicTitle = Theme.Text("Not Playing", 15, bold: true), musicArtist = Theme.Text("Choose a player", 13, Theme.Secondary);
    private readonly RollingText musicTime = new(11, Theme.Secondary), musicRemaining = new(11, Theme.Secondary);
    private readonly Image artwork = new() { Stretch = Stretch.UniformToFill };
    private readonly Border artworkPlate = new() { Background = Theme.Control, CornerRadius = new(8), ClipToBounds = true };
    private Button musicPlay = null!, shuffle = null!, repeat = null!;
    private readonly PlaybackSlider progress = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas volumePanel = new() { Width = 264, Height = 44 };
    private SpotifyPanel tracksPanel = null!;
    private readonly PlaybackSlider volumeSlider = new() { Minimum = 0, Maximum = 1 };
    private bool scrubbing, settingVolume;
    private bool? wasPlaying;
    private bool? wasConnected;
    private string lastTitle = "", lastSource = "";
    private void ConfigureMedia()
    {
        Canvas page = pages[0]; artworkPlate.Child = artwork; Theme.Place(page, artworkPlate, 0, 4, 48, 48);
        Theme.Place(page, musicTitle, 60, 8, 168, 21); Theme.Place(page, musicArtist, 60, 30, 168, 20);
        Theme.Place(page, progress, 0, 61, 264, 22); Theme.Place(page, musicTime, 0, 81, 80, 17); Theme.Place(page, musicRemaining, 210, 81, 54, 17);
        Theme.Place(page, Theme.Button("Previous track", () => Run(() => Model.Media.Command("previous")), "previous"), 48, 95, 46, 36);
        musicPlay = Theme.Button("Play or pause", () => Run(() => Model.Media.Command("play")), "play"); Theme.Place(page, musicPlay, 109, 91, 46, 40);
        Theme.Place(page, Theme.Button("Next track", () => Run(() => Model.Media.Command("next")), "next"), 170, 95, 46, 36);
        shuffle = Theme.Button("Shuffle", () => Run(() => Model.Media.Command("shuffle")), "shuffle"); repeat = Theme.Button("Repeat", () => Run(() => Model.Media.Command("repeat")), "repeat");
        var actions = new[] {
            Theme.Button("Favourite Spotify track", () => Run(Spotify.ToggleSaved), "star"), shuffle, repeat,
            Theme.HoverNavigation(Theme.Button("System volume", OpenVolume, "volume"), () => { if (Detail != 1) OpenVolume(); }),
            Theme.HoverNavigation(Theme.Button("Spotify library, queue and devices", () => { if (Detail != 2) SetDetail(2); }, "list"), () => { if (Detail != 2) SetDetail(2); }) };
        for (int i = 0; i < actions.Length; i++) Theme.Place(page, actions[i], i * 45, 137, 30, 30);
        progress.ScrubStarted += () => scrubbing = true;
        progress.ValueCommitted += value => { Run(() => Model.Media.Command("seek", value * Model.Media.Duration)); scrubbing = false; };
        System.Windows.Automation.AutomationProperties.SetName(progress, "Playback position");
        Theme.Place(volumePanel, Theme.Icon("volume", Theme.Secondary), 0, 8, 20, 20); Theme.Place(volumePanel, volumeSlider, 30, 4, 220, 28);
        System.Windows.Automation.AutomationProperties.SetName(volumeSlider, "System volume");
        volumeSlider.ValueChanged += (_, _) => { if (!settingVolume) { try { Volume.Set(volumeSlider.Value); } catch (Exception e) { Model.SetError(e.Message); } } };
        tracksPanel = new(Spotify, Model.Media, () => SettingsRequested?.Invoke(), () => ReducedMotion);
    }
    private void OpenVolume()
    {
        if (Detail == 1) return;
        try { settingVolume = true; volumeSlider.Value = Volume.Get(); SetDetail(1); }
        catch (Exception e) { Model.SetError("Audio output unavailable: " + e.Message); }
        finally { settingVolume = false; }
    }
    public async void Run(Func<Task> operation)
    { try { Model.SetError(""); await operation(); } catch (Exception e) { Model.SetError(e.Message); } }
    private void RefreshMedia()
    {
        musicTitle.Text = Model.Media.Title; musicArtist.Text = Model.Error.Length > 0 ? Model.Error : Model.Media.Artist; musicArtist.ToolTip = musicArtist.Text; artwork.Source = Model.Media.Artwork;
        artworkPlate.Background = Model.Media.Artwork == null ? Theme.Control : Brushes.Transparent;
        if (wasPlaying != Model.Media.Playing) { wasPlaying = Model.Media.Playing; musicPlay.Content = Theme.Icon(Model.Media.Playing ? "pause" : "play"); }
        progress.IsEnabled = Model.Media.CanSeek || Storage.Diagnostic; shuffle.IsEnabled = Model.Media.CanShuffle; repeat.IsEnabled = Model.Media.CanRepeat;
        if (!scrubbing) UpdateProgress();
        musicTime.Set(TimerSession.Label(Model.Media.Position), ReducedMotion); musicRemaining.Set(TimerSession.Label(Math.Max(0, Model.Media.Duration - Model.Media.Position)), ReducedMotion);
        if (!Model.Media.Connected && !Storage.Diagnostic) { if (Model.Error.Length == 0) musicArtist.Text = "Connect from Settings"; if (wasConnected != false) artworkPlate.Child = Theme.Button("Connect Windows media", () => SettingsRequested?.Invoke(), "music"); }
        else if (lastSource != Model.Media.Source || lastTitle != Model.Media.Title) { artworkPlate.Child = Model.Media.Artwork == null ? Theme.Icon("music", Theme.Secondary, 28) : artwork; }
        else if (Model.Media.Artwork != null && artworkPlate.Child != artwork) artworkPlate.Child = artwork;
        lastSource = Model.Media.Source; lastTitle = Model.Media.Title;
        wasConnected = Model.Media.Connected;
        musicTitle.ToolTip = Model.Media.Status;
    }
    private void UpdateProgress() => progress.Value = Model.Media.Duration <= 0 ? 0 : Model.Media.Position / Model.Media.Duration;

    private readonly TextBlock clipPreview = Theme.Text("Your clipboard stays private until you enable it.", 13), clipStatus = Theme.Text("", 11, Theme.Secondary);
    private readonly Image clipImage = new() { Stretch = Stretch.Uniform };
    private int clipIndex;
    private Button clipEnable = null!;
    private readonly TextBlock clipPosition = Theme.Text("", 11, Theme.Secondary);
    private Button clipOlder = null!, clipNewer = null!, clipCopy = null!;
    private void ConfigureClipboard()
    {
        Canvas page = pages[2]; Theme.Place(page, Theme.Text("Clipboard", 15, bold: true), 0, 6, 264, 24);
        Theme.Place(page, clipPosition, 105, 9, 90, 20);
        Theme.Place(page, Theme.Button("Clear", Model.ClearClipboard), 213, 3, 51, 28);
        clipPreview.TextWrapping = TextWrapping.Wrap; Theme.Place(page, clipPreview, 0, 39, 264, 63); Theme.Place(page, clipImage, 0, 36, 264, 70);
        clipEnable = Theme.Button("Enable history", Model.EnableClipboard, filled: true); Theme.Place(page, clipEnable, 0, 111, 108, 32);
        clipOlder = Theme.Button("Older copy", () => { clipIndex++; RefreshClipboard(); }, "previous"); Theme.Place(page, clipOlder, 116, 111, 28, 32);
        clipNewer = Theme.Button("Newer copy", () => { clipIndex--; RefreshClipboard(); }, "next"); Theme.Place(page, clipNewer, 146, 111, 28, 32);
        Theme.Place(page, Theme.Button("Capture clipboard", () => { Model.CaptureClipboard(true); clipIndex = 0; }, "clipboard"), 176, 111, 28, 32);
        clipCopy = Theme.Button("Copy again", () => Model.CopyClipboard(clipIndex), "copy"); Theme.Place(page, clipCopy, 206, 111, 28, 32);
        Theme.Place(page, Theme.Button("Pause capture (keep history)", Model.PauseClipboard, "pause"), 236, 111, 28, 32);
        Theme.Place(page, clipStatus, 0, 151, 264, 18);
    }
    private void RefreshClipboard()
    {
        clipIndex = Math.Clamp(clipIndex, 0, Math.Max(0, Model.ClipboardItems.Count - 1));
        var item = Model.ClipboardItems.ElementAtOrDefault(clipIndex);
        clipPreview.Text = item?.Text ?? item?.Summary ?? (Model.ClipboardEnabled ? "Copy text, an image, or files to get started." : "Your clipboard stays private until you enable it.");
        clipImage.Source = item?.Image; clipPreview.Visibility = item?.Image == null ? Visibility.Visible : Visibility.Hidden;
        clipEnable.Visibility = Model.ClipboardEnabled ? Visibility.Hidden : Visibility.Visible;
        clipPosition.Text = Model.ClipboardItems.Count == 0 ? "No copies yet" : $"{clipIndex + 1} / {Model.ClipboardItems.Count} copies";
        clipOlder.IsEnabled = clipIndex < Model.ClipboardItems.Count - 1; clipNewer.IsEnabled = clipIndex > 0; clipCopy.IsEnabled = item != null;
        clipStatus.Text = Model.ClipboardStatus; clipStatus.ToolTip = Model.ClipboardStatus;
    }

}
