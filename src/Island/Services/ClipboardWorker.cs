using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Island.Services;

// OLE clipboard APIs require an STA. A dedicated STA keeps a slow clipboard
// owner or image conversion from blocking WPF's render dispatcher.
public static class ClipboardWorker
{
    private static readonly Lazy<Task<Dispatcher>> Worker = new(Start);
    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "Island clipboard" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task;
    }
    public static async Task<T> Run<T>(Func<T> action)
    { var dispatcher = await Worker.Value.ConfigureAwait(false); return await dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.ConfigureAwait(false); }
}
