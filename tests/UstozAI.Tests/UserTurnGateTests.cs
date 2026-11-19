using UstozAI.Api.Services.Pipeline;
using Xunit;

namespace UstozAI.Tests;

/// <summary>#7 — one turn at a time per student, but different students never wait for each other.</summary>
public class UserTurnGateTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Fifty_students_enter_at_the_same_time()
    {
        var gate = new UserTurnGate();
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        var started = DateTime.UtcNow;
        var handles = await Task.WhenAll(ids.Select(id => gate.EnterAsync(id, Long, default)));
        Assert.All(handles, Assert.NotNull);                                   // nobody was blocked …
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));     // … and nobody waited
        Assert.All(ids, id => Assert.True(gate.IsBusy(id)));
        foreach (var h in handles) h!.Dispose();
    }

    [Fact]
    public async Task Same_student_second_turn_waits_for_the_first()
    {
        var gate = new UserTurnGate();
        var me = Guid.NewGuid();
        var first = await gate.EnterAsync(me, Long, default);
        var second = gate.EnterAsync(me, Long, default);
        await Task.Delay(150);
        Assert.False(second.IsCompleted);                                      // still queued
        first!.Dispose();
        Assert.NotNull(await second);                                          // gets in right after
    }

    [Fact]
    public async Task Waiting_turn_times_out_instead_of_hanging()
    {
        var gate = new UserTurnGate();
        var me = Guid.NewGuid();
        using var first = await gate.EnterAsync(me, Long, default);
        Assert.Null(await gate.EnterAsync(me, TimeSpan.FromMilliseconds(100), default));
    }

    [Fact]
    public async Task Cancelled_wait_does_not_block_later_turns()
    {
        var gate = new UserTurnGate();
        var me = Guid.NewGuid();
        var first = await gate.EnterAsync(me, Long, default);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(me, Long, cts.Token));
        first!.Dispose();
        first.Dispose();                                                       // double dispose is harmless
        using var next = await gate.EnterAsync(me, TimeSpan.FromMilliseconds(200), default);
        Assert.NotNull(next);
    }
}
