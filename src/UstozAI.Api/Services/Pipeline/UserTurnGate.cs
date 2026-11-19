using System.Collections.Concurrent;

namespace UstozAI.Api.Services.Pipeline;

/// <summary>
/// One turn at a time PER STUDENT. Different students never wait for each other (50 people talk in parallel);
/// only the same student's second turn — e.g. from a second browser tab — waits until the first one is finished,
/// so anger, homework and message order can't overwrite each other. A barge-in cancels the running turn on the
/// client, which releases the gate immediately.
/// </summary>
public sealed class UserTurnGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    /// <summary>Waits for the student's turn. Returns a handle to dispose when done, or null on timeout.</summary>
    public async Task<IDisposable?> EnterAsync(Guid userId, TimeSpan timeout, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(timeout, ct) ? new Releaser(gate) : null;
    }

    /// <summary>Is a turn of this student running right now? (diagnostics / tests)</summary>
    public bool IsBusy(Guid userId) => _gates.TryGetValue(userId, out var g) && g.CurrentCount == 0;

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}
