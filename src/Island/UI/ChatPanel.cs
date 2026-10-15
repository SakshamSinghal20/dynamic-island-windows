using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Island.Services;

namespace Island.UI;

public sealed class ChatPanel : Grid
{
    private readonly ChatService service;
    public event Action? CloseRequested, SizeRequested;
    private readonly StackPanel transcript = new() { Margin = new(0, 10, 8, 10) };
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBox composer = Theme.Input("", "Message ChatGPT");
    private readonly TextBlock status = Theme.Text("", 11, Theme.Secondary);
    private readonly ComboBox accounts = new() { MinWidth = 120, MaxWidth = 210, DisplayMemberPath = "Email" }, models = new() { MinWidth = 100, MaxWidth = 160, DisplayMemberPath = "Name" };
    private readonly Button signIn, stop, send, retry;
    private readonly List<(Border Border, TextBlock Text)> rendered = [];
    private readonly List<string> renderedStates = [];
    private object? conversation;
    private bool updating;
    public ChatPanel(ChatService service)
    {
        this.service = service;
        RowDefinitions.Add(new() { Height = new(32) }); RowDefinitions.Add(new() { Height = new(36) }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = new(24) }); RowDefinitions.Add(new() { Height = new(64) });
        var header = new DockPanel(); Children.Add(header);
        var close = Theme.Button("Close chat", () => CloseRequested?.Invoke(), "close"); close.Width = 28; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var expand = Theme.Button("Change chat size", () => SizeRequested?.Invoke(), "expand"); expand.Width = 28; DockPanel.SetDock(expand, Dock.Right); header.Children.Add(expand);
        var fresh = Theme.Button("New conversation", service.NewConversation, "plus"); fresh.Width = 28; DockPanel.SetDock(fresh, Dock.Right); header.Children.Add(fresh);
        header.Children.Add(Theme.Text("Ask AI", 16, bold: true));
        var connections = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetRow(connections, 1); Children.Add(connections);
        signIn = Theme.Button("Continue with ChatGPT", () => _ = service.SignIn(), filled: true); signIn.MinWidth = 160; connections.Children.Add(signIn);
        accounts.Margin = new(4, 0, 4, 0); connections.Children.Add(accounts); connections.Children.Add(models);
        var options = Theme.Button("Account options", OpenOptions, "settings"); options.Width = 30; connections.Children.Add(options);
        accounts.SelectionChanged += (_, _) => { if (!updating && accounts.SelectedIndex >= 0 && accounts.SelectedIndex != service.Selected) _ = service.Select(accounts.SelectedIndex); };
        models.SelectionChanged += (_, _) => { if (!updating && models.SelectedItem is AiModel model) service.ModelId = model.Id; };
        scroll.Content = transcript; Grid.SetRow(scroll, 2); Children.Add(scroll);
        Grid.SetRow(status, 3); Children.Add(status);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = new(40) }); Grid.SetRow(footer, 4); Children.Add(footer);
        composer.AcceptsReturn = true; composer.TextWrapping = TextWrapping.Wrap; composer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; composer.Margin = new(0, 4, 6, 4);
        footer.Children.Add(composer);
        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        send = Theme.Button("Send message", Send, "send", filled: true); send.Height = 30; actions.Children.Add(send);
        stop = Theme.Button("Stop response", service.Stop, "close"); stop.Height = 28; actions.Children.Add(stop);
        retry = Theme.Button("Retry response", () => _ = service.Retry(), "repeat"); retry.Height = 28; actions.Children.Add(retry);
        composer.KeyDown += (_, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { Send(); e.Handled = true; } };
        service.Changed += Refresh; Refresh();
    }
    private void Send()
    {
        if (!service.Connected || service.Working || service.ModelId.Length == 0) return;
        string prompt = composer.Text.Trim(); if (prompt.Length == 0) return;
        composer.Clear(); _ = service.Ask(prompt);
    }
    private void OpenOptions()
    {
        var menu = new ContextMenu();
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Add account", () => _ = service.SignIn(true)); Add("Reload models", () => _ = service.LoadModels());
        Add("Cancel sign-in", service.CancelSignIn); Add("Sign out", () => _ = service.SignOut());
        Add("Usage", () => Native.Open("https://chatgpt.com/codex/settings/usage")); menu.IsOpen = true;
    }
    private void Refresh()
    {
        updating = true;
        try
        {
            status.Text = service.Status; status.ToolTip = service.Status;
            signIn.Visibility = service.Connected ? Visibility.Collapsed : Visibility.Visible; signIn.IsEnabled = !service.SigningIn;
            accounts.Visibility = service.Accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            models.Visibility = service.Connected ? Visibility.Visible : Visibility.Collapsed;
            if (accounts.Items.Count != service.Accounts.Count || !accounts.Items.Cast<Credential>().SequenceEqual(service.Accounts)) { accounts.ItemsSource = service.Accounts.ToArray(); accounts.SelectedIndex = service.Selected; }
            if (models.Items.Count != service.Models.Count || !models.Items.Cast<AiModel>().SequenceEqual(service.Models)) { models.ItemsSource = service.Models.ToArray(); models.SelectedIndex = service.Models.FindIndex(m => m.Id == service.ModelId); }
            send.IsEnabled = service.Connected && !service.Working && service.ModelId.Length > 0;
            stop.Visibility = service.Working ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = !service.Working && service.Conversation.Messages.LastOrDefault()?.State is "failed" or "stopped" ? Visibility.Visible : Visibility.Collapsed;
            bool followBottom = scroll.ScrollableHeight - scroll.VerticalOffset < 40;
            if (!ReferenceEquals(conversation, service.Conversation) || rendered.Count > service.Conversation.Messages.Count)
            { conversation = service.Conversation; transcript.Children.Clear(); rendered.Clear(); renderedStates.Clear(); }
            for (int i = 0; i < service.Conversation.Messages.Count; i++)
            {
                var message = service.Conversation.Messages[i]; bool user = message.Role == "user";
                if (i >= rendered.Count)
                {
                    var text = Theme.Text("", 14); text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None;
                    var border = new Border { Background = user ? Theme.Blue : Brushes.Transparent, CornerRadius = new(16), Padding = new Thickness(user ? 12 : 0), Margin = new Thickness(user ? 35 : 0, 5, user ? 0 : 10, 12), Child = text };
                    transcript.Children.Add(border); rendered.Add((border, text)); renderedStates.Add("");
                }
                if (rendered[i].Text.Text == message.Text && renderedStates[i] == message.State) continue;
                rendered[i].Text.Text = message.Text.Length == 0 && message.State == "streaming" ? "Thinking…" : message.Text;
                if (!user && message.State == "complete" && renderedStates[i] != "complete") rendered[i].Border.Child = Markdown(message.Text);
                renderedStates[i] = message.State;
            }
            if (followBottom) scroll.ScrollToEnd();
        }
        finally { updating = false; }
    }
    // Native selectable text; Markdown never executes HTML, scripts, or images.
    private static FlowDocumentScrollViewer Markdown(string value)
    {
        var document = new FlowDocument { FontFamily = new("Segoe UI Variable, Segoe UI"), FontSize = 14, Foreground = Brushes.White, Background = Brushes.Transparent, PagePadding = new(0), ColumnWidth = double.PositiveInfinity };
        bool code = false;
        foreach (string line in value.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            string content = line; var paragraph = new Paragraph { Margin = new(0, 0, 0, 6), LineHeight = 21 };
            if (code) { paragraph.FontFamily = new("Cascadia Mono, Consolas"); paragraph.FontSize = 12; paragraph.Background = Theme.Control; paragraph.Inlines.Add(new Run(content)); }
            else
            {
                int heading = content.TakeWhile(c => c == '#').Count();
                if (heading is > 0 and <= 6 && content.Length > heading && content[heading] == ' ') { paragraph.FontSize = 22 - heading; paragraph.FontWeight = FontWeights.SemiBold; content = content[(heading + 1)..]; }
                if (content.StartsWith("- ", StringComparison.Ordinal) || content.StartsWith("* ", StringComparison.Ordinal)) content = "• " + content[2..];
                foreach (string piece in Regex.Split(content, @"(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\(https?://[^\s)]+\))"))
                {
                    if (piece.StartsWith("**", StringComparison.Ordinal) && piece.EndsWith("**", StringComparison.Ordinal)) paragraph.Inlines.Add(new Bold(new Run(piece[2..^2])));
                    else if (piece.StartsWith('`') && piece.EndsWith('`') && piece.Length > 1) paragraph.Inlines.Add(new Run(piece[1..^1]) { FontFamily = new("Consolas"), Background = Theme.Control });
                    else
                    {
                        var match = Regex.Match(piece, @"^\[([^\]]+)\]\((https?://[^\s)]+)\)$");
                        if (match.Success) { string address = match.Groups[2].Value; var link = new Hyperlink(new Run(match.Groups[1].Value)) { Foreground = Theme.Blue }; link.Click += (_, _) => Native.Open(address); paragraph.Inlines.Add(link); }
                        else paragraph.Inlines.Add(new Run(piece));
                    }
                }
            }
            document.Blocks.Add(paragraph);
        }
        return new FlowDocumentScrollViewer { Document = document, Background = Brushes.Transparent, IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new(0), BorderThickness = new(0) };
    }
}
