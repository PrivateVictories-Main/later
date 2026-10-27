using Xunit;

namespace Later.Core.Tests;

public class SmokeTests
{
    [Fact] public void Fails_until_the_port_exists() => Assert.Equal(1, Later.Core.Browser.Chrome.Wire().Length);
}
