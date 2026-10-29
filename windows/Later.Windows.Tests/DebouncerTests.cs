using Later.Windows.Platform;
using Xunit;

namespace Later.Windows.Tests;

public class DebouncerTests
{
    [Fact]
    public void FiveQuickPokesFireOnceAfterTheLastOne()
    {
        int fires = 0; long firedAt = 0;
        using var d = new Debouncer(TimeSpan.FromMilliseconds(200), () => { firedAt = Environment.TickCount64; Interlocked.Increment(ref fires); });
        long lastPoke = 0;
        for (int i = 0; i < 5; i++) { d.Poke(); lastPoke = Environment.TickCount64; Thread.Sleep(50); }
        for (int i = 0; i < 40 && Volatile.Read(ref fires) == 0; i++) Thread.Sleep(50);   // generous: the pool may be busy
        Assert.Equal(1, Volatile.Read(ref fires));
        Assert.True(Volatile.Read(ref firedAt) - lastPoke >= 150, $"fired {firedAt - lastPoke} ms after the last poke");
        Thread.Sleep(600);
        Assert.Equal(1, Volatile.Read(ref fires));   // no further fire
    }

    [Fact]
    public void NothingFiresWithoutAPoke()
    {
        int fires = 0;
        using var d = new Debouncer(TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref fires));
        Thread.Sleep(300);
        Assert.Equal(0, fires);
    }
}
