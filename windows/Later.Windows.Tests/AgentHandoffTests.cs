using Later.Windows.Engine;
using Xunit;

namespace Later.Windows.Tests;

public class AgentHandoffTests
{
    [Fact]
    public void TheLockIsLetGoBeforeTheEngineIsTakenApartSoASlowReadNeverDelaysTheApp()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var agent = EngineLock.Open(suffix);
        using var app = EngineLock.Open(suffix);
        using var held = new ManualResetEventSlim(); using var readEnds = new ManualResetEventSlim(); using var apartEnds = new ManualResetEventSlim();
        var order = new List<string>();
        void Add(string step) { lock (order) order.Add(step); }
        bool acquired = false;
        var agentThread = new Thread(() =>
        {
            acquired = agent.TryAcquire(TimeSpan.Zero);   // the mutex belongs to this thread, as the engine thread's does
            held.Set();
            Agent.LetGo(
                stopAndSave: () => Add("save"),
                release: () => { agent.Release(); Add("release"); },
                takeApart: () => { readEnds.Wait(20_000); Add("apart"); apartEnds.Set(); },   // disposing waits for the read that is still running
                onPool: work => ThreadPool.QueueUserWorkItem(_ => work()));
        });
        agentThread.Start();
        try
        {
            Assert.True(held.Wait(10_000));
            Assert.True(acquired);
            Assert.True(app.TryAcquire(TimeSpan.FromSeconds(5)));   // the app has the engine while the read is still blocked
            Assert.False(apartEnds.IsSet);
        }
        finally { readEnds.Set(); agentThread.Join(10_000); }
        Assert.True(apartEnds.Wait(10_000));
        app.Release();
        lock (order) Assert.Equal(["save", "release", "apart"], order.ToArray());
    }
}
