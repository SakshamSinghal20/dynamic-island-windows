using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Island.Services;

namespace Island.UI;

public sealed class SpotifyPanel : Grid
{
    private readonly SpotifyService spotify;
    private readonly MediaService media;
    private readonly TextBox query = Theme.Input("", "Search Spotify tracks");
    private readonly ListBox list = new() { Background = Brushes.Black, Foreground = Brushes.White, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = Theme.Text("", 12, Theme.Secondary), heading = Theme.Text("Queue", 13, bold: true);
    private readonly Button connect, previous, more, play, queue, save, searchButton;
    private readonly Func<bool> reducedMotion;
    private ScrollViewer? scroller;
    private double scrollTarget;
    private TimeSpan? scrollTime;
    private bool scrolling;
    private ListBoxItem? pressedPlaylist;
    private Point playlistPress;
    private long playlistOpenedAt;
    private readonly DockPanel search = new();
    private readonly Dictionary<string, Button> modes = [];
    private CancellationTokenSource? loading;
    private string mode = "Queue", context = "", contextTitle = "";
    private string searchTerm = "";
    private int offset, nextOffset;
    private bool hasMore, busy, acting;
    private SpotifyItem? Selected => (list.SelectedItem as ListBoxItem)?.Tag as SpotifyItem;
    public SpotifyPanel(SpotifyService spotify, MediaService media, Action settings, Func<bool> reducedMotion)
    {
        this.spotify = spotify; this.media = media; this.reducedMotion = reducedMotion; Width = 380; Height = 390;
        ScrollViewer.SetCanContentScroll(list, false);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.PreviewMouseWheel += ScrollList;
        list.PreviewMouseDown += (_, _) => StopScroll();
        foreach (double height in new[] { 38d, 0, 26, -1, 34, 60 }) RowDefinitions.Add(new() { Height = height < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(height) });
        void Row(UIElement item, int row) { SetRow(item, row); Children.Add(item); }
        searchButton = Theme.Button("Search", SearchSongs, filled: true); searchButton.Width = 66;
        System.Windows.Automation.AutomationProperties.SetName(searchButton, "Search Spotify");
        DockPanel.SetDock(searchButton, Dock.Right); search.Children.Add(searchButton);
        var searchInput = new Grid { Margin = new(0, 0, 8, 0) }; searchInput.Children.Add(query);
        var hint = Theme.Text("Search songs or artists", 12, Theme.Secondary); hint.Margin = new(8, 0, 8, 0); hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false;
        searchInput.Children.Add(hint); search.Children.Add(searchInput); search.Margin = new(0, 0, 0, 6); search.Visibility = Visibility.Collapsed; Row(search, 1);
        query.TextChanged += (_, _) => hint.Visibility = query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SearchSongs(); e.Handled = true; } };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 5, 0, 4) };
        foreach (string name in new[] { "Queue", "Recent", "Liked", "Playlists", "Search" })
        {
            void SelectMode() { mode = name; context = contextTitle = ""; offset = 0; _ = Load(); }
            var button = Theme.Button(name, () => { SelectMode(); if (name == "Search") query.Focus(); });
            Theme.HoverNavigation(button, () => { if (mode != name) SelectMode(); });
            button.Width = 72; button.Margin = new(0, 0, 4, 0); modes[name] = button; tabs.Children.Add(button);
        }
        Row(tabs, 0);
        var navigation = new DockPanel();
        more = Theme.Button("Next page", () => { offset = nextOffset; _ = Load(); }, "next"); more.Width = 28; DockPanel.SetDock(more, Dock.Right); navigation.Children.Add(more);
        previous = Theme.Button("Previous page", () => { offset = Math.Max(0, offset - (mode == "Search" ? 10 : 20)); _ = Load(); }, "previous"); previous.Width = 28; DockPanel.SetDock(previous, Dock.Right); navigation.Children.Add(previous);
        navigation.Children.Add(heading); Row(navigation, 2); Row(list, 3);
        list.SelectionChanged += (_, _) => Selection();
        list.MouseDoubleClick += (_, _) =>
        {
            // A double-click on a playlist can land its second click on the newly
            // loaded tracks. Opening the playlist must never start playback.
            if (Environment.TickCount64 - playlistOpenedAt <= System.Windows.Forms.SystemInformation.DoubleClickTime) return;
            if (Selected?.Kind != "playlist") _ = Act("play");
        };
        list.PreviewKeyDown += (_, _) => StopScroll();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { if (Selected?.Kind == "playlist") BrowsePlaylist(); else _ = Act("play"); e.Handled = true; } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 3, 0, 3) };
        play = Theme.Button("Play", () => _ = Act("play"), filled: true); queue = Theme.Button("Add to queue", () => _ = Act("queue"));
        save = Theme.Button("Save / unsave", () => _ = Act("save"));
        foreach (var button in new[] { play, queue, save }) { button.Margin = new(0, 0, 8, 0); button.MinWidth = 44; actions.Children.Add(button); }
        Row(actions, 4);
        var footer = new DockPanel(); connect = Theme.Button("Set up library", settings, filled: true); connect.Width = 122; connect.Height = 30; DockPanel.SetDock(connect, Dock.Right); footer.Children.Add(connect);
        status.TextWrapping = TextWrapping.Wrap; status.Margin = new(0, 4, 8, 0); footer.Children.Add(status); Row(footer, 5);
        IsVisibleChanged += (_, _) => { if (!IsVisible) { loading?.Cancel(); StopScroll(); pressedPlaylist = null; } };
        Unloaded += (_, _) => StopScroll();
        spotify.Changed += () => { if (IsVisible) _ = Load(); };
        Selection();
    }
    public void Open() { if (acting) return; offset = 0; _ = Load(); }
    private void SearchSongs()
    {
        if (acting) return;
        searchTerm = query.Text.Trim(); mode = "Search"; context = contextTitle = ""; offset = 0; _ = Load();
    }
    private async Task Load()
    {
        if (acting) return;
        StopScroll(); pressedPlaylist = null; scroller?.ScrollToTop();
        loading?.Cancel(); loading?.Dispose(); loading = new(); var cancellation = loading.Token;
        busy = true; list.Items.Clear(); hasMore = false; Selection(); status.Text = "Loading…";
        connect.Visibility = spotify.Connected || Storage.Diagnostic ? Visibility.Collapsed : Visibility.Visible;
        search.Visibility = mode == "Search" ? Visibility.Visible : Visibility.Collapsed;
        RowDefinitions[1].Height = new GridLength(mode == "Search" ? 40 : 0);
        heading.Text = mode == "Search" && searchTerm.Length > 0 ? $"Results for {searchTerm}" : mode == "Playlist" ? contextTitle : mode;
        foreach (var pair in modes) pair.Value.Background = pair.Key == mode || (pair.Key == "Playlists" && mode == "Playlist") ? Theme.Control : Brushes.Transparent;
        try
        {
            if (mode == "Search" && searchTerm.Length == 0) { status.Text = "Type a song or artist, then press Enter or Search."; return; }
            SpotifyPage page;
            if (Storage.Diagnostic) page = new(Enumerable.Range(1, 12).Select(i => new SpotifyItem("spotify:track:fixture" + i, "Preview track " + i, "Local layout fixture", "track")).ToArray(), false, 12);
            else if (!spotify.Connected)
            {
                if (mode == "Recent") page = new(media.History.Select(t => new SpotifyItem("", t.Title, t.Artist, "history")).ToArray(), false, 0);
                else { status.Text = "Library needs a Spotify Client ID and sign-in. Playback uses Windows."; return; }
            }
            else page = await spotify.Api.Browse(mode, mode == "Search" ? searchTerm : "", offset, context, cancellation);
            if (cancellation.IsCancellationRequested) return;
            foreach (var item in page.Items)
            {
                var row = new StackPanel { Margin = new(4, 5, 4, 5) }; row.Children.Add(Theme.Text(item.Title, 13, bold: true)); row.Children.Add(Theme.Text(item.Subtitle, 11, Theme.Secondary));
                var entry = new ListBoxItem { Content = row, Tag = item, ToolTip = item.Title + " · " + item.Subtitle, IsEnabled = !item.Restricted };
                if (item.Kind == "playlist")
                {
                    entry.Cursor = Cursors.Hand;
                    entry.PreviewMouseLeftButtonDown += (_, e) => { pressedPlaylist = entry; playlistPress = e.GetPosition(list); };
                    entry.PreviewMouseLeftButtonUp += (_, e) =>
                    {
                        Point released = e.GetPosition(list);
                        bool click = pressedPlaylist == entry && Math.Abs(released.X - playlistPress.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(released.Y - playlistPress.Y) < SystemParameters.MinimumVerticalDragDistance;
                        pressedPlaylist = null;
                        if (!click || busy) return;
                        e.Handled = true; BrowsePlaylist(item);
                    };
                }
                list.Items.Add(entry);
            }
            hasMore = page.HasMore; nextOffset = page.NextOffset;
            status.Text = page.Items.Length == 0 ? (mode == "Search" ? "No songs found. Try another song or artist." : "Nothing here yet. Start Spotify or try another view.") : mode == "Playlists" ? "Click a playlist to browse its tracks." : $"{page.Items.Length} items · select one for actions";
        }
        catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) status.Text = "Spotify took too long. Try again."; }
        catch (Exception e) { if (!cancellation.IsCancellationRequested) { status.Text = e.Message; status.ToolTip = e.Message; } }
        finally { if (!cancellation.IsCancellationRequested) { busy = false; Selection(); } }
    }
    private void Selection()
    {
        list.IsEnabled = query.IsEnabled = searchButton.IsEnabled = !acting;
        foreach (var button in modes.Values) button.IsEnabled = !acting;
        var item = Selected; bool ready = !busy && item != null && item.Kind != "history" && !item.Restricted && spotify.Connected;
        play.IsEnabled = ready; queue.IsEnabled = ready && item!.Kind is "track" or "episode"; save.IsEnabled = ready && item!.Kind == "track";
        previous.IsEnabled = !busy && offset > 0; more.IsEnabled = !busy && hasMore;
    }
    private void BrowsePlaylist() { if (Selected is { Kind: "playlist" } item) BrowsePlaylist(item); }
    private void BrowsePlaylist(SpotifyItem item)
    {
        if (busy) return;
        playlistOpenedAt = Environment.TickCount64;
        context = item.Uri; contextTitle = item.Title; mode = "Playlist"; offset = 0; _ = Load();
    }
    private void ScrollList(object sender, MouseWheelEventArgs e)
    {
        static ScrollViewer? FindScroller(DependencyObject parent)
        {
            if (parent is ScrollViewer viewer) return viewer;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindScroller(VisualTreeHelper.GetChild(parent, i)) is ScrollViewer found) return found;
            return null;
        }
        scroller ??= FindScroller(list);
        if (scroller == null) return;
        e.Handled = true;
        if (!scrolling) scrollTarget = scroller.VerticalOffset;
        // Half a row per notch; bound queued movement so rapid wheel input cannot
        // build up a long fling. High-resolution wheel deltas remain proportional.
        double movement = Math.Clamp(e.Delta / 120d * 24, -72, 72);
        scrollTarget = Math.Clamp(scrollTarget - movement, Math.Max(0, scroller.VerticalOffset - 96), Math.Min(scroller.ScrollableHeight, scroller.VerticalOffset + 96));
        if (reducedMotion()) { StopScroll(); scroller.ScrollToVerticalOffset(scrollTarget); return; }
        if (!scrolling) { scrolling = true; scrollTime = null; CompositionTarget.Rendering += ScrollFrame; }
    }
    private void ScrollFrame(object? sender, EventArgs e)
    {
        if (scroller == null || !IsVisible) { StopScroll(); return; }
        var time = ((RenderingEventArgs)e).RenderingTime;
        double dt = scrollTime.HasValue ? Math.Clamp((time - scrollTime.Value).TotalSeconds, 0, .05) : 1d / 60;
        scrollTime = time;
        double current = scroller.VerticalOffset;
        scrollTarget = Math.Clamp(scrollTarget, 0, scroller.ScrollableHeight);
        double next = current + (scrollTarget - current) * (1 - Math.Exp(-dt / .045));
        if (Math.Abs(scrollTarget - next) < .5) { scroller.ScrollToVerticalOffset(scrollTarget); StopScroll(); }
        else scroller.ScrollToVerticalOffset(next);
    }
    private void StopScroll()
    {
        if (scrolling) CompositionTarget.Rendering -= ScrollFrame;
        scrolling = false; scrollTime = null;
    }
    private async Task Act(string action)
    {
        if (busy || Selected is not SpotifyItem item || !spotify.Connected) return;
        busy = acting = true; Selection();
        try
        {
            switch (action)
            {
                case "play": await spotify.Api.Play(item); status.Text = "Playback requested."; break;
                case "queue": await spotify.Api.Queue(item); status.Text = "Added to your Spotify queue."; break;
                case "save": status.Text = await spotify.Api.ToggleSaved(item.Uri) ? "Saved to Liked Songs." : "Removed from Liked Songs."; break;
            }
        }
        catch (Exception e) { status.Text = e.Message; status.ToolTip = e.Message; }
        finally { busy = acting = false; Selection(); }
    }
}
