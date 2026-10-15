using System.IO;
using Island.Services;

internal static class ScratchpadChecks
{
    public static void Run(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Scratchpad assertion failed."); }
        test("Scratchpad survives reopening and clearing through encrypted storage", () =>
        {
            string key = "scratchpad-test-" + Guid.NewGuid().ToString("N");
            ScratchpadStore Open() => new(() => Storage.LoadCredential<string>(key) ?? "", text => Storage.SaveCredential(key, text));
            try
            {
                const string note = "Quick idea\nAnother line — café 📝";
                Open().Save(note).GetAwaiter().GetResult();
                Check(Open().Load() == note);
                Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Storage.Root, key + ".bin"))).Contains(note));
                Open().Save("").GetAwaiter().GetResult(); Check(Open().Load() == "");
            }
            finally { Storage.RemoveCredential(key); }
        });
        test("Scratchpad serializes fast edits and recovers after a failed write", () =>
        {
            string saved = ""; var writes = new List<string>();
            var store = new ScratchpadStore(() => saved, text =>
            {
                if (text == "first") { Thread.Sleep(40); throw new IOException("Fixture disk error"); }
                writes.Add(text); saved = text;
            });
            var first = store.Save("first"); var second = store.Save("second"); var latest = store.Save("latest");
            try { first.GetAwaiter().GetResult(); } catch (IOException) { }
            Task.WhenAll(second, latest).GetAwaiter().GetResult();
            Check(writes.SequenceEqual(new[] { "second", "latest" }) && saved == "latest");
        });
    }
}
