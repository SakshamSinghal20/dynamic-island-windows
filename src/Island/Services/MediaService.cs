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
    private bool refreshing, refreshAgain, loadingArtwork, nativeArtwork;
    private int revision;
    private string preferred = "";
    public string Title { get; private set; } = "Not Playing";
    public string Artist { get; private set; } = "Choose a player";
    public string Status { get; private set; } = "Connect Windows media to control a player.";
    public string Error { get; private set; } = "";
    public string Source => session?.SourceAppUserModelId ?? "";
    public bool Playing { get; private set; }
    public bool Connected => manager != null;
    public double Duration { get; private set; }
    private double position, sampledAt, rate = 1;
    public double Position => Math.Clamp(position + (Playing ? (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - sampledAt) * rate : 0), 0, Math.Max(0, Duration));
    public BitmapSource? Artwork { get; private set; }
    public Func<string, string, Task<byte[]?>>? ArtworkFallback { private get; set; }
    public static async Task<(BitmapSource? Image, bool Native)> ReadArtwork(IRandomAccessStreamReference? thumbnail, BitmapSource? cached, bool cachedIsNative)
    {
        if (thumbnail == null || (cached != null && cachedIsNative)) return (cached, cachedIsNative);
        using var stream = await thumbnail.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > 8_000_000) return (cached, cachedIsNative);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size); byte[] bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
        return (DecodeArtwork(bytes), true);
    }
    private static BitmapSource DecodeArtwork(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes); var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 128; bitmap.StreamSource = memory; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    public bool CanSeek { get; private set; }
    public bool CanPlayPause { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanPrevious { get; private set; }
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
        if (ReferenceEquals(next, session) && next != null) { Refresh(); return; }
        Detach(); session = next; revision++;
        if (next != null)
        {
            next.MediaPropertiesChanged += PropertiesChanged; next.PlaybackInfoChanged += PlaybackChanged; next.TimelinePropertiesChanged += TimelineChanged;
            Refresh();
        }
        else { ResetPlayback(); Artist = "Start playback in your player"; Status = "Waiting for a Windows media session."; Notify(); }
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
            string knownTitle = Title, knownArtist = Artist; BitmapSource? knownArtwork = Artwork; bool knownNativeArtwork = nativeArtwork;
            var snapshot = await Task.Run(async () =>
            {
                var props = await current.TryGetMediaPropertiesAsync();
                var info = current.GetPlaybackInfo(); var timeline = current.GetTimelineProperties();
                BitmapSource? cached = props.Title == knownTitle && props.Artist == knownArtist ? knownArtwork : null;
                var cover = await ReadArtwork(props.Thumbnail, cached, cached != null && knownNativeArtwork);
                return (props.Title, props.Artist, info, timeline, image: cover.Image, native: cover.Native);
            });
            if (token != revision) return;
            if (Title != snapshot.Title && snapshot.Title.Length > 0)
            { History.Insert(0, (snapshot.Title, snapshot.Artist, Source)); if (History.Count > 30) History.RemoveAt(30); }
            // A fallback may finish while this metadata snapshot is in flight.
            if (snapshot.image == null && Title == snapshot.Title && Artist == snapshot.Artist) { snapshot.image = Artwork; snapshot.native = nativeArtwork; }
            Title = snapshot.Title.Length > 0 ? snapshot.Title : "Not Playing"; Artist = snapshot.Artist; Artwork = snapshot.image;
            nativeArtwork = snapshot.native;
            Playing = snapshot.info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            rate = snapshot.info.PlaybackRate ?? 1;
            Duration = Math.Max(0, snapshot.timeline.EndTime.TotalSeconds);
            position = snapshot.timeline.Position.TotalSeconds;
            // Timeline position is anchored to LastUpdatedTime, not this async return.
            if (Playing) position += Math.Max(0, (DateTimeOffset.UtcNow - snapshot.timeline.LastUpdatedTime).TotalSeconds) * rate;
            sampledAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var controls = snapshot.info.Controls;
            CanPlayPause = controls.IsPlayPauseToggleEnabled || (Playing ? controls.IsPauseEnabled : controls.IsPlayEnabled);
            CanNext = controls.IsNextEnabled; CanPrevious = controls.IsPreviousEnabled;
            CanSeek = controls.IsPlaybackPositionEnabled && Duration > 0; CanShuffle = controls.IsShuffleEnabled; CanRepeat = controls.IsRepeatEnabled;
            Shuffled = snapshot.info.IsShuffleActive ?? false; Repeat = snapshot.info.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
            Status = "Connected to " + Source; Notify();
            RefreshArtwork();
        }
        catch (Exception e) { if (token == revision) { Status = "Player unavailable: " + e.Message; Notify(); } }
        finally { refreshing = false; if (refreshAgain) _ = dispatcher.BeginInvoke(Refresh); }
    }
    private async void RefreshArtwork()
    {
        var fallback = ArtworkFallback;
        if (Artwork != null || loadingArtwork || fallback == null || !Source.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return;
        int token = revision; string title = Title, artist = Artist;
        loadingArtwork = true;
        try
        {
            // Cover retrieval must not delay playback state or control updates.
            var image = await Task.Run(async () =>
            {
                var bytes = await fallback(title, artist);
                if (bytes == null || bytes.Length == 0 || bytes.Length > 8_000_000) return null;
                return DecodeArtwork(bytes);
            });
            if (token == revision && Title == title && Artist == artist && Artwork == null && image != null) { Artwork = image; nativeArtwork = false; Notify(); }
        }
        catch (Exception) { /* Artwork is optional; keep playback and its error state independent. */ }
        finally
        {
            loadingArtwork = false;
            if (session != null && (token != revision || Title != title || Artist != artist)) RefreshArtwork();
        }
    }
    public async Task Command(string command, double value = 0)
    {
        // A player can replace its SMTC session after an update/restart/wake.
        // Rebind before dispatch, instead of retaining a disconnected wrapper.
        if (manager != null) SelectSession();
        var current = session;
        Error = "";
        try
        {
            if (current == null) throw new InvalidOperationException("Start playback in a connected player first.");
            var info = current.GetPlaybackInfo(); var controls = info.Controls;
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool supported = command switch
            {
                "play" => controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                "next" => controls.IsNextEnabled, "previous" => controls.IsPreviousEnabled,
                "seek" => controls.IsPlaybackPositionEnabled && Duration > 0 && double.IsFinite(value),
                "shuffle" => controls.IsShuffleEnabled, "repeat" => controls.IsRepeatEnabled, _ => false
            };
            if (!supported) throw new InvalidOperationException("This player does not currently allow that control. Open the player and start a track.");
            bool ok = command switch
            {
                "play" when playing && controls.IsPauseEnabled => await current.TryPauseAsync(),
                "play" when !playing && controls.IsPlayEnabled => await current.TryPlayAsync(),
                "play" => await current.TryTogglePlayPauseAsync(),
                "next" => await current.TrySkipNextAsync(),
                "previous" => await current.TrySkipPreviousAsync(),
                "seek" => await current.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Clamp(value, 0, Duration)).Ticks),
                "shuffle" => await current.TryChangeShuffleActiveAsync(!(info.IsShuffleActive ?? false)),
                "repeat" => await current.TryChangeAutoRepeatModeAsync(info.AutoRepeatMode == MediaPlaybackAutoRepeatMode.None ? MediaPlaybackAutoRepeatMode.List : info.AutoRepeatMode == MediaPlaybackAutoRepeatMode.List ? MediaPlaybackAutoRepeatMode.Track : MediaPlaybackAutoRepeatMode.None),
                _ => false
            };
            if (!ok) throw new InvalidOperationException("The player rejected that control. Open the player and retry.");
            Refresh();
        }
        catch (Exception e)
        {
            Error = Status = "Playback command failed: " + e.Message; Notify();
            throw new InvalidOperationException(Error, e);
        }
    }
    public void Fixture(bool playing)
    { Title = "A moment of focus"; Artist = "Local motion fixture"; Playing = playing; Duration = 240; position = 67; sampledAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; Notify(); }
    public void Disconnect()
    {
        revision++; Detach(); session = null;
        if (manager != null) { manager.CurrentSessionChanged -= CurrentChanged; manager.SessionsChanged -= SessionsChanged; }
        manager = null; ResetPlayback(); Artist = "Choose a player"; Status = "Media disconnected."; Notify();
    }
    private void ResetPlayback()
    {
        Playing = false; Artwork = null; nativeArtwork = false; Title = "Not Playing"; Error = "";
        Duration = position = 0; rate = 1;
        CanPlayPause = CanNext = CanPrevious = CanSeek = CanShuffle = CanRepeat = false;
        Shuffled = false; Repeat = MediaPlaybackAutoRepeatMode.None;
    }
    public void Dispose() => Disconnect();
}
