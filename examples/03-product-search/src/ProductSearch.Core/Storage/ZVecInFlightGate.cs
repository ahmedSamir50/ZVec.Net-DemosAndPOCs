namespace ProductSearch.Core.Storage;

/// <summary>
/// Waits until no in-flight native ZVec calls hold a collection reference,
/// then runs a lifecycle mutation (Dispose / Reopen / Destroy).
/// SDK ops on an open handle are thread-safe; only close/reopen must drain first.
/// </summary>
internal sealed class ZVecInFlightGate
{
    private int _inFlight;

    public void Enter()
        => Interlocked.Increment(ref _inFlight);

    public void Leave()
        => Interlocked.Decrement(ref _inFlight);

    public void Drain(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        var spinner = new SpinWait();

        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {_inFlight} in-flight ZVec operation(s) to finish before closing the collection.");
            }

            spinner.SpinOnce();
        }
    }
}
