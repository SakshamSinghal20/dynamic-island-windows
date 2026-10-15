using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.Services;

internal static class ClipboardChecks
{
    public static void Run(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Clipboard history assertion failed."); }
        test("Clipboard history retains the newest 50 distinct copies in time order", () =>
        {
            var items = new List<ClipboardEntry>(); var start = DateTimeOffset.UtcNow;
            for (int i = 0; i < 60; i++) ClipboardHistory.Add(items, ClipboardHistory.Identify(new("copy", Text: "copy " + i) { CopiedAt = start.AddSeconds(i) }));
            Check(items.Count == 50 && items[0].Text == "copy 59" && items[^1].Text == "copy 10");
            ClipboardHistory.Add(items, ClipboardHistory.Identify(new("copy", Text: "copy 20") { CopiedAt = start.AddMinutes(2) }), newest: false);
            Check(items.Count == 50 && items[0].Text == "copy 20");
            ClipboardHistory.Add(items, ClipboardHistory.Identify(new("copy", Text: "copy 20") { CopiedAt = start }), newest: false);
            Check(items.Count == 50 && items[0].CopiedAt == start.AddMinutes(2));
        });
        test("Clipboard encrypted roundtrip restores text, image and file copies", () =>
        {
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, 8); bitmap.Freeze();
            ClipboardEntry[] entries = [new("text", Text: "Earlier copy 📝"), new("files", Files: ["C:\\example.txt"]), new("image", Image: bitmap)];
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entries.Select(ClipboardHistory.Pack).ToArray());
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Check(!plain.SequenceEqual(encrypted));
            byte[] restored = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var copies = JsonSerializer.Deserialize<SavedClipboard[]>(restored)!.Select(ClipboardHistory.Unpack).ToArray();
            Check(copies[0].Text == entries[0].Text && copies[1].Files![0] == entries[1].Files![0]);
            Check(copies[2].Image!.IsFrozen && copies[2].Image!.PixelWidth == 2 && copies[2].Image!.PixelHeight == 1);
            Check(copies[0].Key != copies[1].Key && copies[0].CopiedAt == entries[0].CopiedAt);
        });
        test("Clipboard history honors confidential and excluded formats", () =>
        {
            Check(ClipboardHistory.Excluded(["ExcludeClipboardContentFromMonitorProcessing"]));
            Check(ClipboardHistory.Excluded(["CustomPassword", "Text"]));
            Check(ClipboardHistory.Excluded(["Confidential"]));
            Check(!ClipboardHistory.Excluded(["UnicodeText", "Bitmap"]));
        });
    }
}
