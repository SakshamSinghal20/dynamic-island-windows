using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

public sealed partial class IslandSurface
{
    private readonly ScratchpadStore scratchStore = new();
    private readonly TextBox scratchpad = Theme.Input("", "Scratchpad note");
    private readonly TextBlock scratchStatus = Theme.Text("Saved on this PC", 11, Theme.Secondary);
    private readonly DispatcherTimer scratchSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool scratchDirty;
    private Task<bool> scratchPending = Task.FromResult(true);
    public event Action? ScratchpadFocusRequested;
    public void FocusScratchpad() { scratchpad.Focus(); scratchpad.CaretIndex = scratchpad.Text.Length; }
    private void ConfigureScratchpad()
    {
        var page = pages[3];
        Theme.Place(page, Theme.Text("Scratchpad", 15, bold: true), 0, 6, 152, 24);
        Theme.Place(page, Theme.Button("Copy note", () =>
        {
            if (scratchpad.Text.Length == 0) return;
            try { Clipboard.SetText(scratchpad.Text); scratchStatus.Text = "Copied"; }
            catch (Exception) { scratchStatus.Text = "Clipboard busy. Try Copy again."; }
        }, "copy"), 191, 2, 32, 30);
        Theme.Place(page, Theme.Button("Clear", () => { scratchpad.Clear(); scratchpad.Focus(); }), 222, 2, 42, 30);
        scratchpad.AcceptsReturn = true; scratchpad.TextWrapping = TextWrapping.Wrap;
        scratchpad.VerticalContentAlignment = VerticalAlignment.Top;
        scratchpad.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scratchpad.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scratchpad.MaxLength = ScratchpadStore.MaxLength; scratchpad.UndoLimit = 100;
        scratchpad.Text = scratchStore.Load();
        var input = new Grid(); input.Children.Add(scratchpad);
        var hint = Theme.Text("Write a quick note…", 13, Theme.Secondary);
        hint.Margin = new(8, 5, 8, 5); hint.IsHitTestVisible = false;
        hint.Visibility = scratchpad.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; input.Children.Add(hint);
        Theme.Place(page, input, 0, 36, 264, 109);
        Theme.Place(page, scratchStatus, 0, 151, 264, 18);
        scratchpad.TextChanged += (_, _) =>
        {
            hint.Visibility = scratchpad.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            scratchDirty = true; scratchStatus.Text = "Saving…";
            scratchSaveTimer.Stop(); scratchSaveTimer.Start();
        };
        scratchSaveTimer.Tick += async (_, _) => await SaveScratchpad();
    }
    public Task<bool> SaveScratchpad()
    {
        scratchSaveTimer.Stop();
        if (!scratchDirty) return scratchPending;
        scratchDirty = false;
        return scratchPending = SaveNote(scratchpad.Text);
    }
    private async Task<bool> SaveNote(string snapshot)
    {
        try
        {
            await scratchStore.Save(snapshot);
            if (scratchpad.Text == snapshot) scratchStatus.Text = "Saved on this PC";
            return true;
        }
        catch (Exception)
        {
            scratchDirty = true; scratchStatus.Text = "Couldn’t save. Your note is still here."; return false;
        }
    }
}
