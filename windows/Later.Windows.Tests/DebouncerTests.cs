using Later.Windows.Platform;
using Xunit;

namespace Later.Windows.Tests;

public class DebouncerTests
{
    [Fact]
    public void FiveQuickPokesFireOnceAfterTheLastOne()
    {
        int fires = 0; var last = DateTime.MinValue;
        using var d = new Debouncer(TimeSpan.FromMilliseconds(200), () => { Interlocked.Increment(ref fires); last = DateTime.UtcNow; });
        DateTime lastPoke = default;
        for (int i = 0; i < 5; i++) { d.Poke(); lastPoke = DateTime.UtcNow; Thread.Sleep(50); }
        Thread.Sleep(450);
        Assert.Equal(1, Volatile.Read(ref fires));
        Assert.True((last - lastPoke).TotalMilliseconds >= 150, $"fired {(last - lastPoke).TotalMilliseconds} ms after the last poke");
        Thread.Sleep(600);
        Assert.Equal(1, Volatile.Read(ref fires));
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
