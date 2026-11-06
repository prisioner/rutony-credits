// No external fake-time package: timestamp and UTC can be moved independently.
internal sealed class MonoTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly HashSet<Timer> timers = [];
    private long timestamp;
    private DateTimeOffset utc = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return timestamp; }
    public override DateTimeOffset GetUtcNow() { lock (gate) return utc; }
    public void MoveUtc(TimeSpan amount) { lock (gate) utc += amount; }
    public void Advance(TimeSpan amount)
    {
        Timer[] due;
        lock (gate)
        {
            timestamp += amount.Ticks; utc += amount;
            due = timers.Where(timer => timer.Due <= timestamp).ToArray();
            foreach (var timer in due) timer.Due = timer.Period > 0 ? timestamp + timer.Period : long.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (gate) { timers.Add(timer); timer.Change(dueTime, period); }
        return timer;
    }
    private sealed class Timer(MonoTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public long Period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.timestamp + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }
        public void Fire() { lock (owner.gate) { if (disposed) return; } callback(state); }
        public void Dispose() { lock (owner.gate) { disposed = true; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
