using HidusbfModernGui;
using Xunit;

public class ConfigNavTests
{
    [Fact]
    public void StartsAtHub()
    {
        var nav = new ConfigNav();
        Assert.Equal(ConfigPage.Hub, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Go_MovesAndAllowsBack()
    {
        var nav = new ConfigNav();
        nav.Go(ConfigPage.Sticks);
        Assert.Equal(ConfigPage.Sticks, nav.Current);
        Assert.True(nav.CanGoBack);
    }

    [Fact]
    public void Back_FromAnyPage_ReturnsToHub()
    {
        var nav = new ConfigNav();
        nav.Go(ConfigPage.Gatillos);
        nav.Back();
        Assert.Equal(ConfigPage.Hub, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Back_AtHub_IsANoOp()
    {
        var nav = new ConfigNav();
        nav.Back();
        Assert.Equal(ConfigPage.Hub, nav.Current);
    }

    [Fact]
    public void TitleOf_EveryPage_HasText()
    {
        foreach (ConfigPage p in System.Enum.GetValues<ConfigPage>())
            Assert.False(string.IsNullOrWhiteSpace(ConfigNav.TitleOf(p)));
    }
}
