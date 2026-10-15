using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Island.Services;

namespace Island.UI;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(ActivityModel model, SpotifyService spotify, Action<string> chooseScreen, Action reduceMotion, Action diagnostics)
    {
        Title = "Island · Settings"; Width = 480; Height = 620; MinWidth = 420; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Brush("#141416"); Foreground = Brushes.White; FontFamily = new("Segoe UI");
        var panel = new StackPanel { Margin = new(28) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Theme.Text("Island for Windows", 24, bold: true));
        var intro = Theme.Text("Hover at the top-center edge. Ctrl+Alt+Space opens or collapses Island. Escape dismisses it.", 13, Theme.Secondary); intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new(0, 8, 0, 22); panel.Children.Add(intro);
        var status = Theme.Text("", 12, Theme.Orange); status.TextWrapping = TextWrapping.Wrap;
        void Heading(string text) { var heading = Theme.Text(text, 15, bold: true); heading.Margin = new(0, 18, 0, 8); panel.Children.Add(heading); }
        void ActionButton(string title, Func<Task> action)
        {
            Button? button = null; button = Theme.Button(title, async () => { button!.IsEnabled = false; status.Text = ""; try { await action(); if (status.Text.Length == 0) status.Text = "Saved."; } catch (Exception e) { status.Text = e.Message; } finally { button.IsEnabled = true; } }, filled: true);
            button.Height = 34; button.Margin = new(0, 4, 0, 4); panel.Children.Add(button);
        }
        Heading("Media");
        var mediaInfo = Theme.Text("Windows media supports Spotify, Apple Music, browsers, and other players that publish a media session.", 12, Theme.Secondary); mediaInfo.TextWrapping = TextWrapping.Wrap; panel.Children.Add(mediaInfo);
        var connection = Theme.Text(model.Media.Status, 12, Theme.Orange); connection.TextWrapping = TextWrapping.Wrap; panel.Children.Add(connection);
        ActionButton("Connect active Windows player", async () => { await model.Media.Connect(); model.Settings.MediaSource = "auto"; model.Persist(); status.Text = model.Media.Status; });
        var sources = new ComboBox { ItemsSource = model.Media.Sources, Margin = new(0, 4, 0, 4), ToolTip = "Choose a specific running media session" }; panel.Children.Add(sources);
        void RefreshSources()
        {
            string? selected = sources.SelectedItem as string ?? model.Settings.MediaSource;
            string[] available = model.Media.Sources;
            if (!(sources.ItemsSource is string[] current && current.SequenceEqual(available))) sources.ItemsSource = available;
            sources.SelectedItem = available.Contains(selected) ? selected : available.FirstOrDefault();
            connection.Text = model.Media.Status;
        }
        RefreshSources();
        model.Media.Changed += RefreshSources; Closed += (_, _) => model.Media.Changed -= RefreshSources;
        ActionButton("Use selected player", async () => { if (sources.SelectedItem is string source) { await model.Media.Connect(source); model.Settings.MediaSource = source; model.Persist(); } });
        ActionButton("Disconnect media", () => { model.Media.Disconnect(); model.Settings.MediaSource = ""; model.Persist(); return Task.CompletedTask; });
        Heading("Spotify library");
        var help = Theme.Text("Windows playback and Spotify library access connect separately. Search, queue, Liked Songs, playlists and devices need your Spotify app's Client ID and browser sign-in.", 12, Theme.Secondary); help.TextWrapping = TextWrapping.Wrap; panel.Children.Add(help);
        var libraryState = Theme.Text("", 12, Theme.Orange); libraryState.TextWrapping = TextWrapping.Wrap; libraryState.Margin = new(0, 8, 0, 4); panel.Children.Add(libraryState);
        void RefreshLibrary() => libraryState.Text = spotify.Connected ? "Spotify library connected." : "Library setup required. Windows playback remains available.";
        RefreshLibrary(); spotify.Changed += RefreshLibrary; Closed += (_, _) => spotify.Changed -= RefreshLibrary;
        var instructions = Theme.Text($"1. Create a Web API app in Spotify's Developer Dashboard.\n2. Enter {SpotifyService.RedirectUri} and click Add, then Save.\n3. Add your Spotify account to the app's permitted users if required.\n4. Paste the 32-character Client ID below and connect. Finish sign-in in your browser.\n\nNo client secret is needed. Spotify's Premium and development-access requirements apply.", 12, Theme.Secondary);
        instructions.TextWrapping = TextWrapping.Wrap; instructions.Margin = new(0, 8, 0, 8);
        panel.Children.Add(new Expander { Header = "Spotify setup steps", Foreground = Brushes.White, Content = instructions, Margin = new(0, 4, 0, 4) });
        ActionButton("Open Spotify Developer Dashboard", () => { Native.Open("https://developer.spotify.com/dashboard"); return Task.CompletedTask; });
        panel.Children.Add(Theme.Text("Spotify Client ID", 12));
        var client = Theme.Input(model.Settings.SpotifyClientId, "Spotify Client ID"); client.Margin = new(0, 8, 0, 4); panel.Children.Add(client);
        ActionButton("Connect Spotify library", async () => await spotify.Authorize(client.Text.Trim()));
        ActionButton("Disconnect Spotify library", () => { spotify.Disconnect(); return Task.CompletedTask; });
        Heading("Display and motion");
        var screens = new ComboBox { ItemsSource = System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray(), SelectedItem = model.Settings.Screen, Margin = new(0, 4, 0, 8) }; panel.Children.Add(screens);
        screens.SelectionChanged += (_, _) => { if (screens.SelectedItem is string screen) chooseScreen(screen); };
        var motion = new CheckBox { Content = "Reduce motion (unchecked uses Windows setting)", Foreground = Brushes.White, IsChecked = model.Settings.ReduceMotion == true, Margin = new(0, 5, 0, 5) }; panel.Children.Add(motion);
        motion.Click += (_, _) => { model.Settings.ReduceMotion = motion.IsChecked == true ? true : null; model.Persist(); reduceMotion(); };
        var login = new CheckBox { Content = "Start Island when I sign in to Windows", Foreground = Brushes.White, IsChecked = Native.StartsWithWindows, Margin = new(0, 5, 0, 5) }; panel.Children.Add(login);
        login.Click += (_, _) => { try { Native.StartsWithWindows = login.IsChecked == true; } catch (Exception e) { status.Text = e.Message; } };
        ActionButton("Motion diagnostics", () => { diagnostics(); return Task.CompletedTask; });
        Heading("Privacy");
        var privacy = Theme.Text("Clipboard history keeps up to 50 copies encrypted for your Windows user on this PC. Enable imports available Windows history; Pause keeps saved copies and Clear removes Island history. Scratchpad notes save locally, encrypted for your Windows user. Windows protects account tokens for your user. Chat messages go directly to OpenAI when you send them. Timers only alert while Island is running.", 12, Theme.Secondary); privacy.TextWrapping = TextWrapping.Wrap; panel.Children.Add(privacy);
        status.Margin = new(0, 18, 0, 0); panel.Children.Add(status);
    }
}
