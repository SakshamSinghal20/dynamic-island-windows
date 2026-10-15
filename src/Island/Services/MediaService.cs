using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Island.Services;

public sealed class MediaService(Dispatcher dispatcher) : IDisposable
{
    public event Action? Changed;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private bool refreshing, refreshAgain;
    private int revision;
    private string preferred = "";
    public string Title { get; private set; } = "Not Playing";
    public string Artist { get; private set; } = "Choose a player";
    public string Status { get; private set; } = "Connect Windows media to control a player.";
    public string Source => session?.SourceAppUserModelId ?? "";
    public bool Playing { get; private set; }
    public bool Connected => manager != null;
    public double Duration { get; private set; }
    private double position, sampledAt, rate = 1;
    public double Position => Math.Clamp(position + (Playing ? (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - sampledAt) * rate : 0), 0, Math.Max(0, Duration));
    public BitmapSource? Artwork { get; private set; }
    public bool CanSeek { get; private set; }
    public bool CanShuffle { get; private set; }
    public bool CanRepeat { get; private set; }
    public bool Shuffled { get; private set; }
    public MediaPlaybackAutoRepeatMode Repeat { get; private set; }
    public List<(string Title, string Artist, string Source)> History { get; } = [];
    public string[] Sources => manager?.GetSessions().Select(s => s.SourceAppUserModelId).ToArray() ?? [];
    private void Notify() => Changed?.Invoke();
    public async Task Connect(string source = "auto")
    {
        if (Storage.Diagnostic) return;
        Disconnect(); preferred = source;
        int token = revision;
        try
        {
            var result = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (token != revision) return;
            manager = result; manager.CurrentSessionChanged += CurrentChanged; manager.SessionsChanged += SessionsChanged;
            SelectSession();
        }
        catch (Exception e) { Status = "Windows media could not connect: " + e.Message; Notify(); }
    }
    private void CurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => dispatcher.BeginInvoke(SelectSession);
    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => dispatcher.BeginInvoke(SelectSession);
    private void Detach()
    {
        if (session == null) return;
        session.MediaPropertiesChanged -= PropertiesChanged; session.PlaybackInfoChanged -= PlaybackChanged; session.TimelinePropertiesChanged -= TimelineChanged;
    }
    private void SelectSession()
    {
        var next = preferred == "auto" ? manager?.GetCurrentSession() : manager?.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        Detach(); session = next; revision++;
        if (next != null)
        {
            next.MediaPropertiesChanged += PropertiesChanged; next.PlaybackInfoChanged += PlaybackChanged; next.TimelinePropertiesChanged += TimelineChanged;
            Refresh();
        }
        else { Title = "Not Playing"; Artist = "Start playback in your player"; Playing = false; Artwork = null; Duration = position = 0; Status = "Waiting for a Windows media session."; Notify(); }
    }
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => dispatcher.BeginInvoke(Refresh);
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => dispatcher.BeginInvoke(Refresh);
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => dispatcher.BeginInvoke(Refresh);
    private async void Refresh()
    {
        if (refreshing) { refreshAgain = true; return; }
        var current = session; int token = revision; if (current == null) return;
        refreshing = true; refreshAgain = false;
        try
        {
            if (token != revision) return;
            string knownTitle = Title, knownArtist = Artist; BitmapSource? knownArtwork = Artwork;
            var snapshot = await Task.Run(async () =>
            {
                var props = await current.TryGetMediaPropertiesAsync();
                var info = current.GetPlaybackInfo(); var timeline = current.GetTimelineProperties();
                BitmapSource? image = props.Title == knownTitle && props.Artist == knownArtist ? knownArtwork : null;
                if (props.Thumbnail != null && image == null)
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    if (stream.Size <= 8_000_000)
                    {
                        using var reader = new DataReader(stream.GetInputStreamAt(0));
                        await reader.LoadAsync((uint)stream.Size); byte[] bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
                        using var memory = new MemoryStream(bytes); var bitmap = new BitmapImage();
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 128; bitmap.StreamSource = memory; bitmap.EndInit(); bitmap.Freeze(); image = bitmap;
                    }
                }
                return (props.Title, props.Artist, info, timeline, image);
            });
            if (token != revision) return;
            if (Title != snapshot.Title && snapshot.Title.Length > 0)
            { History.Insert(0, (snapshot.Title, snapshot.Artist, Source)); if (History.Count > 30) History.RemoveAt(30); }
            Title = snapshot.Title.Length > 0 ? snapshot.Title : "Not Playing"; Artist = snapshot.Artist; Artwork = snapshot.image;
            Playing = snapshot.info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            rate = snapshot.info.PlaybackRate ?? 1;
            Duration = Math.Max(0, snapshot.timeline.EndTime.TotalSeconds);
            position = snapshot.timeline.Position.TotalSeconds;
            // Timeline position is anchored to LastUpdatedTime, not this async return.
            if (Playing) position += Math.Max(0, (DateTimeOffset.UtcNow - snapshot.timeline.LastUpdatedTime).TotalSeconds) * rate;
            sampledAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            CanSeek = snapshot.info.Controls.IsPlaybackPositionEnabled; CanShuffle = snapshot.info.Controls.IsShuffleEnabled; CanRepeat = snapshot.info.Controls.IsRepeatEnabled;
            Shuffled = snapshot.info.IsShuffleActive ?? false; Repeat = snapshot.info.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
            Status = "Connected to " + Source; Notify();
        }
        catch (Exception e) { if (token == revision) { Status = "Player unavailable: " + e.Message; Notify(); } }
        finally { refreshing = false; if (refreshAgain) _ = dispatcher.BeginInvoke(Refresh); }
    }
    public async Task Command(string command, double value = 0)
    {
        var current = session; if (current == null) { Status = "Start playback in a connected player first."; Notify(); return; }
        try
        {
            bool ok = command switch
            {
                "play" => await current.TryTogglePlayPauseAsync(),
                "next" => await current.TrySkipNextAsync(),
                "previous" => await current.TrySkipPreviousAsync(),
                "seek" => await current.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Clamp(value, 0, Duration)).Ticks),
                "shuffle" => await current.TryChangeShuffleActiveAsync(!Shuffled),
                "repeat" => await current.TryChangeAutoRepeatModeAsync(Repeat == MediaPlaybackAutoRepeatMode.None ? MediaPlaybackAutoRepeatMode.List : Repeat == MediaPlaybackAutoRepeatMode.List ? MediaPlaybackAutoRepeatMode.Track : MediaPlaybackAutoRepeatMode.None),
                _ => false
            };
            if (!ok) { Status = "This player does not support that control."; Notify(); }
            else Refresh();
        }
        catch (Exception e) { Status = "Playback command failed: " + e.Message; Notify(); }
    }
    public void Fixture(bool playing)
    { Title = "A moment of focus"; Artist = "Local motion fixture"; Playing = playing; Duration = 240; position = 67; sampledAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; Notify(); }
    public void Disconnect()
    {
        revision++; Detach(); session = null;
        if (manager != null) { manager.CurrentSessionChanged -= CurrentChanged; manager.SessionsChanged -= SessionsChanged; }
        manager = null; Playing = false; Artwork = null; Title = "Not Playing"; Artist = "Choose a player"; Status = "Media disconnected."; Notify();
    }
    public void Dispose() => Disconnect();
}
