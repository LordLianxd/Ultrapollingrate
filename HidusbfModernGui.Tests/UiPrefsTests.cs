using System;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class UiPrefsTests : IDisposable
{
    private readonly string _dir;

    public UiPrefsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        UiPrefsStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        UiPrefsStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Primer arranque: la app se abre como siempre se ha abierto.
    [Fact]
    public void Load_WithoutFile_DefaultsToNight()
    {
        var p = UiPrefsStore.Load();
        Assert.Equal(AppTheme.Noche, p.Theme);
        Assert.Equal(UiPrefsStore.DefaultObsPort, p.ObsPort);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        Assert.True(UiPrefsStore.Save(new UiPrefs { Theme = AppTheme.Dia, ObsPort = 9001 }).Success);
        var back = UiPrefsStore.Load();
        Assert.Equal(AppTheme.Dia, back.Theme);
        Assert.Equal(9001, back.ObsPort);
    }

    // Un archivo a medio escribir o editado a mano no puede impedir que la app abra.
    [Fact]
    public void Load_WithCorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(UiPrefsStore.Path, "{ esto no es json");
        var p = UiPrefsStore.Load();
        Assert.Equal(AppTheme.Noche, p.Theme);
        Assert.Equal(UiPrefsStore.DefaultObsPort, p.ObsPort);
    }

    // Un puerto imposible (0, negativo, fuera de rango) se corrige al leer, no al usar:
    // asi ningun consumidor tiene que volver a validarlo.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public void Load_WithImpossiblePort_FallsBackToDefault(int bad)
    {
        File.WriteAllText(UiPrefsStore.Path, $"{{\"Theme\":\"Noche\",\"ObsPort\":{bad}}}");
        Assert.Equal(UiPrefsStore.DefaultObsPort, UiPrefsStore.Load().ObsPort);
    }

    [Fact]
    public void Theme_IsStoredByName_NotByNumber()
    {
        UiPrefsStore.Save(new UiPrefs { Theme = AppTheme.Dia, ObsPort = 8787 });
        Assert.Contains("\"Dia\"", File.ReadAllText(UiPrefsStore.Path));
    }
}
