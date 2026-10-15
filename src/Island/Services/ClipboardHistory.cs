using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Island.Services;

public sealed record SavedClipboard(string Summary, string? Text, byte[]? Png, string[]? Files, DateTimeOffset CopiedAt = default);

public static class ClipboardHistory
{
    public const int Limit = 50;
    private const long MemoryLimit = 32_000_000;
    private static readonly SemaphoreSlim Disk = new(1);
    public static bool Excluded(IEnumerable<string> formats) => formats.Any(f =>
        f.Contains("ExcludeClipboardContentFromMonitorProcessing", StringComparison.OrdinalIgnoreCase) ||
        f.Contains("Confidential", StringComparison.OrdinalIgnoreCase) || f.Contains("Password", StringComparison.OrdinalIgnoreCase));

    public static SavedClipboard Pack(ClipboardEntry item)
    {
        byte[]? png = null;
        if (item.Image != null)
        {
            using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(item.Image)); encoder.Save(stream); png = stream.ToArray();
        }
        return new(item.Summary, item.Text, png, item.Files, item.CopiedAt);
    }
    public static ClipboardEntry Unpack(SavedClipboard saved)
    {
        BitmapSource? image = null;
        if (saved.Png != null)
        {
            using var stream = new MemoryStream(saved.Png);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image = decoder.Frames[0];
            if ((long)image.PixelWidth * image.PixelHeight > 16_000_000) throw new InvalidDataException("Clipboard image is too large.");
            image.Freeze();
        }
        string key = Convert.ToHexString(SHA256.HashData(saved.Png ?? Encoding.UTF8.GetBytes(saved.Text != null ? "text:" + saved.Text : "files:" + string.Join('\0', saved.Files ?? []))));
        return new(saved.Summary, saved.Text, image, saved.Files) { Key = key, CopiedAt = saved.CopiedAt };
    }
    public static ClipboardEntry Identify(ClipboardEntry item) => item.Key.Length > 0 ? item : Unpack(Pack(item));
    private static long Size(ClipboardEntry entry) => entry.Image != null ? (long)entry.Image.PixelWidth * entry.Image.PixelHeight * 4 : (entry.Text?.Length ?? 0) * 2L + (entry.Files?.Sum(f => f.Length * 2L) ?? 0);
    public static void Add(List<ClipboardEntry> items, ClipboardEntry item, bool newest = true)
    {
        if (Size(item) > MemoryLimit) return;
        int existing = items.FindIndex(x => x.Key == item.Key);
        if (existing >= 0) { if (!newest && items[existing].CopiedAt >= item.CopiedAt) return; items.RemoveAt(existing); }
        if (newest) items.Insert(0, item); else items.Add(item);
        items.Sort((a, b) => b.CopiedAt.CompareTo(a.CopiedAt));
        long bytes = 0; int keep = 0;
        foreach (var entry in items.Take(Limit))
        {
            bytes += Size(entry);
            if (bytes > MemoryLimit) break; keep++;
        }
        if (items.Count > keep) items.RemoveRange(keep, items.Count - keep);
    }
    public static async Task<List<ClipboardEntry>> Load()
    {
        await Disk.WaitAsync();
        try { return await ClipboardWorker.Run(() =>
        {
            var items = new List<ClipboardEntry>();
            foreach (var saved in Storage.LoadCredential<SavedClipboard[]>("clipboard-history") ?? [])
            {
                try { Add(items, Unpack(saved), newest: false); } catch (Exception) { /* Skip an unreadable entry, retaining other copies. */ }
            }
            return items;
        }); }
        finally { Disk.Release(); }
    }
    public static async Task Save(ClipboardEntry[] snapshot)
    {
        await Disk.WaitAsync();
        try { await ClipboardWorker.Run(() =>
        {
            if (snapshot.Length == 0) Storage.RemoveCredential("clipboard-history");
            else Storage.SaveCredential("clipboard-history", snapshot.Select(Pack).ToArray());
            return true;
        }); }
        finally { Disk.Release(); }
    }
    public static async Task<(List<ClipboardEntry> Items, string Notice)> ImportWindows(DateTimeOffset after)
    {
        var items = new List<ClipboardEntry>();
        var result = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();
        if (result.Status != ClipboardHistoryItemsResultStatus.Success)
            return (items, "Earlier Windows copies unavailable. New copies will be saved.");
        foreach (var entry in result.Items.Where(i => i.Timestamp > after).OrderByDescending(i => i.Timestamp).Take(Limit))
        {
            try
            {
                var data = entry.Content; if (Excluded(data.AvailableFormats)) continue;
                ClipboardEntry? item = null;
                if (data.Contains(StandardDataFormats.StorageItems))
                {
                    var files = (await data.GetStorageItemsAsync()).Select(f => f.Path).Where(p => p.Length > 0).Take(12).ToArray();
                    if (files.Length > 0) item = new(string.Join(", ", files.Select(Path.GetFileName)), Files: files);
                }
                else if (data.Contains(StandardDataFormats.Bitmap))
                {
                    using var stream = await (await data.GetBitmapAsync()).OpenReadAsync();
                    if (stream.Size <= 8_000_000)
                    {
                        using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
                        byte[] bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
                        item = Unpack(new("Copied image", null, bytes, null));
                    }
                }
                else if (data.Contains(StandardDataFormats.Text))
                {
                    string text = await data.GetTextAsync();
                    if (text.Length is > 0 and <= 1_000_000) item = new(text.Length > 100 ? text[..100] + "…" : text, Text: text);
                }
                if (item != null) Add(items, Identify(item) with { CopiedAt = entry.Timestamp }, newest: false);
            }
            catch (Exception) { /* Windows can expire individual history entries during import. */ }
        }
        return (items, "");
    }
}
