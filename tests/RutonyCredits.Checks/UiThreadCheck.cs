using System.Collections.Concurrent;

internal static class UiThreadCheck
{
    private sealed class FrozenContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        { Interlocked.Increment(ref Posts); queue.Enqueue((callback, state)); }
        public void Rescue()
        { while (queue.TryDequeue(out var item)) item.Callback(item.State); }
    }
    public static async Task Run(Action action, string operation)
    {
        var context = new FrozenContext();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { action(); done.SetResult(); }
            catch (Exception e) { done.SetException(e); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(2))) != done.Task)
        {
            // Rescue only after the regression has failed, to release test resources.
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!done.Task.IsCompleted && DateTime.UtcNow < until)
            { context.Rescue(); await Task.Delay(10); }
            if (!done.Task.IsCompleted) throw new Exception("Lifecycle did not finish even after test rescue: " + operation);
            await done.Task;
            throw new Exception("UI thread deadlock: " + operation + "; captured continuations=" + context.Posts);
        }
        await done.Task;
    }
}
