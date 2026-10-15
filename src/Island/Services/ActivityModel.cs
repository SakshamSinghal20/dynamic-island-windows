using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Island.Core;

namespace Island.Services;

public sealed record ClipboardEntry(string Summary, string? Text = null, BitmapSource? Image = null, string[]? Files = null)
{ public string Key { get; init; } = ""; public DateTimeOffset CopiedAt { get; init; } = DateTimeOffset.UtcNow; }
public sealed class ActivityModel : IDisposable
{
    public event Action? Changed;
    public event Action? Completed;
    public readonly Settings Settings;
    public readonly MediaService Media;
    public TimerSession? Timer => Settings.Timer;
    public bool Finished { get; private set; }
    private DateTimeOffset completedUntil;
    public bool ClipboardEnabled { get; private set; }
    public List<ClipboardEntry> ClipboardItems { get; } = [];
    public string ClipboardStatus { get; private set; } = "Enable to save up to 50 recent copies on this PC.";
    public string Error { get; private set; } = "";
    private uint clipboardSequence;
    private bool capturingClipboard;
    private int clipboardGeneration;
    private readonly DispatcherTimer clock;
    public bool HasActivity => Media.Playing || (Timer != null && !Finished) || completedUntil > DateTimeOffset.UtcNow;
    public string TimerText => Finished ? "Done" : TimerSession.Label(Timer?.Remaining(DateTimeOffset.UtcNow) ?? 1500);
    public ActivityModel(Settings settings, Dispatcher dispatcher)
    {
        Settings = settings; Media = new MediaService(dispatcher);
        Media.Changed += Notify;
        clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        Tick();
    }
    public void Notify() => Changed?.Invoke();
    public void SetError(string message) { Error = message; Notify(); }
    public async void Persist()
    {
        try { await Storage.SaveSettings(Settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetError("Could not save settings: " + e.Message); }
    }
    public void StartTimer(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes < 1 || minutes > 1440) { SetError("Enter 1–1,440 minutes."); return; }
        Settings.Timer = new(minutes * 60, DateTimeOffset.UtcNow); Finished = false; Error = ""; completedUntil = default; Persist(); Notify();
    }
    public void PauseTimer() { Timer?.TogglePause(DateTimeOffset.UtcNow); Persist(); Notify(); }
    public void ExtendTimer() { Timer?.Extend(60, DateTimeOffset.UtcNow); Finished = false; Persist(); Notify(); }
    public void StopTimer() { Settings.Timer = null; Finished = false; completedUntil = default; Persist(); Notify(); }
    private void Tick()
    {
        if (!Finished && Timer?.IsComplete(DateTimeOffset.UtcNow) == true)
        { Finished = true; completedUntil = DateTimeOffset.UtcNow.AddSeconds(6); if (!Storage.Diagnostic) System.Media.SystemSounds.Asterisk.Play(); Completed?.Invoke(); }
        Notify();
    }
    public async Task InitializeClipboard()
    {
        if (Storage.Diagnostic) return;
        int generation = clipboardGeneration;
        try
        {
            var saved = await ClipboardHistory.Load();
            if (generation != clipboardGeneration) return;
            foreach (var entry in saved) ClipboardHistory.Add(ClipboardItems, entry, newest: false);
            if (Settings.ClipboardHistoryEnabled) EnableClipboard();
            else { ClipboardStatus = "Capture paused · saved history available"; Notify(); }
        }
        catch (Exception) { ClipboardStatus = "Saved history could not be loaded."; Notify(); }
    }
    public async void EnableClipboard()
    {
        if (ClipboardEnabled || Storage.Diagnostic) return;
        ClipboardEnabled = Settings.ClipboardHistoryEnabled = true; Persist();
        int generation = ++clipboardGeneration;
        ClipboardStatus = "Loading earlier copies…"; CaptureClipboard(true); Notify();
        try
        {
            var imported = await await ClipboardWorker.Run(() => ClipboardHistory.ImportWindows(Settings.ClipboardHistoryClearedAt));
            if (!ClipboardEnabled || generation != clipboardGeneration) return;
            foreach (var entry in imported.Items) ClipboardHistory.Add(ClipboardItems, entry, newest: false);
            ClipboardStatus = imported.Notice.Length > 0 ? imported.Notice : "History saved on this PC · up to 50 items";
            SaveClipboard(); Notify();
        }
        catch (Exception) { if (generation == clipboardGeneration) { ClipboardStatus = "Earlier Windows copies unavailable. New copies will be saved."; Notify(); } }
    }
    public void PauseClipboard()
    {
        clipboardGeneration++; ClipboardEnabled = Settings.ClipboardHistoryEnabled = false; Persist();
        ClipboardStatus = "Paused · your history is kept"; Notify();
    }
    public void ClearClipboard()
    {
        clipboardGeneration++; ClipboardItems.Clear(); Settings.ClipboardHistoryClearedAt = DateTimeOffset.UtcNow; Persist();
        ClipboardStatus = "Island history cleared"; SaveClipboard(); Notify();
    }
    private async void SaveClipboard()
    {
        try { await ClipboardHistory.Save(ClipboardItems.ToArray()); }
        catch (Exception) { ClipboardStatus = "History is available, but could not be saved to disk."; Notify(); }
    }
    public async void CaptureClipboard(bool explicitCapture = false)
    {
        if (!ClipboardEnabled || Storage.Diagnostic || capturingClipboard) return;
        uint sequence = Native.GetClipboardSequenceNumber();
        int generation = clipboardGeneration;
        if (!explicitCapture && sequence == clipboardSequence) return;
        capturingClipboard = true;
        try
        {
            ClipboardEntry? entry = await ClipboardWorker.Run(() => ReadClipboard() is { } item ? ClipboardHistory.Identify(item) : null);
            clipboardSequence = sequence;
            if (ClipboardEnabled && generation == clipboardGeneration && entry != null) { ClipboardHistory.Add(ClipboardItems, entry); SaveClipboard(); Notify(); }
        }
        catch (ExternalException) { if (explicitCapture) { ClipboardStatus = "Clipboard is busy. Try Capture again."; Notify(); } }
        catch (Exception e) { ClipboardStatus = "Could not capture clipboard: " + e.Message; Notify(); }
        finally { capturingClipboard = false; }
        if (ClipboardEnabled && Native.GetClipboardSequenceNumber() != sequence) CaptureClipboard();
    }
    private static ClipboardEntry? ReadClipboard()
    {
        var data = Clipboard.GetDataObject(); if (data == null) return null;
        string[] formats = data.GetFormats(false);
        if (ClipboardHistory.Excluded(formats)) return null;
        if (Clipboard.ContainsFileDropList())
        {
            string[] paths = Clipboard.GetFileDropList().Cast<string>().Take(12).ToArray();
            return new(string.Join(", ", paths.Select(Path.GetFileName)), Files: paths);
        }
        if (Clipboard.ContainsImage())
        {
            BitmapSource? source = Clipboard.GetImage();
            if (source != null && (long)source.PixelWidth * source.PixelHeight <= 16_000_000) { source.Freeze(); return new("Copied image", Image: source); }
        }
        else if (Clipboard.ContainsText())
        {
            string value = Clipboard.GetText();
            if (value.Length is > 0 and <= 1_000_000) return new(value.Length > 100 ? value[..100] + "…" : value, Text: value);
        }
        return null;
    }
    public void CopyClipboard(int index)
    {
        if (index < 0 || index >= ClipboardItems.Count) return;
        try
        {
            var item = ClipboardItems[index];
            if (item.Text != null) Clipboard.SetText(item.Text);
            else if (item.Image != null) Clipboard.SetImage(item.Image);
            else if (item.Files != null) { var paths = new StringCollection(); paths.AddRange(item.Files); Clipboard.SetFileDropList(paths); }
            clipboardSequence = Native.GetClipboardSequenceNumber();
        }
        catch (ExternalException) { ClipboardStatus = "Clipboard is busy. Try Copy again."; Notify(); }
    }
    public void Dispose() { clock.Stop(); Media.Dispose(); }
}
