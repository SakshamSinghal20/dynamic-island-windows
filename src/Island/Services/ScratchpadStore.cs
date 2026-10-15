using System;
using System.Threading.Tasks;

namespace Island.Services;

public sealed class ScratchpadStore
{
    public const int MaxLength = 100_000;
    private readonly Func<string> read;
    private readonly Action<string> write;
    private readonly object gate = new();
    private Task pending = Task.CompletedTask;
    public ScratchpadStore(Func<string>? read = null, Action<string>? write = null)
    {
        this.read = read ?? (() => Storage.LoadCredential<string>("scratchpad") ?? "");
        this.write = write ?? (text => Storage.SaveCredential("scratchpad", text));
    }
    public string Load() => read();
    public Task Save(string text)
    {
        if (text.Length > MaxLength) throw new ArgumentException("Scratchpad is full.");
        lock (gate) return pending = WriteAfter(pending, text);
    }
    private async Task WriteAfter(Task previous, string snapshot)
    {
        try { await previous.ConfigureAwait(false); } catch { /* A later save can retry after a disk error. */ }
        await Task.Run(() => write(snapshot)).ConfigureAwait(false);
    }
}
