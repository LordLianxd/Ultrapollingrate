using System;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class LightIntentTests : IDisposable
{
    private readonly string _dir;

    public LightIntentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingTests_" + Guid.NewGuid().ToString("N"));
        IntentStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        IntentStore.OverrideDirectoryForTests(null);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Static_RoundTrips()
    {
        var intent = LightIntent.FromStatic(new LightState(10, 20, 30, PlayerLeds.Player4, LedBrightness.Low));
        Assert.True(IntentStore.Save(intent).Success);

        var loaded = IntentStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(LightIntentKind.Static, loaded!.Kind);
        Assert.Equal(10, loaded.R);
        Assert.Equal(20, loaded.G);
        Assert.Equal(30, loaded.B);
        Assert.Equal(PlayerLeds.Player4, loaded.Player);
        Assert.Equal(LedBrightness.Low, loaded.Brightness);
    }

    [Fact]
    public void Rainbow_RoundTrips_WithPlayerAndBrightness()
    {
        var intent = LightIntent.FromRainbow(RainbowStyle.Vivid, 120, PlayerLeds.Player2, LedBrightness.Medium);
        Assert.True(IntentStore.Save(intent).Success);

        var loaded = IntentStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(LightIntentKind.Rainbow, loaded!.Kind);
        Assert.Equal(RainbowStyle.Vivid, loaded.Style);
        Assert.Equal(120, loaded.RainbowColoursPerSecond);
        Assert.Equal(PlayerLeds.Player2, loaded.Player);
        Assert.Equal(LedBrightness.Medium, loaded.Brightness);
    }

    [Fact]
    public void Enums_PersistAsNames()
    {
        IntentStore.Save(LightIntent.FromRainbow(RainbowStyle.Vivid, 30, PlayerLeds.Player4, LedBrightness.Low));
        string json = File.ReadAllText(IntentStore.Path);
        Assert.Contains("Vivid", json);
        Assert.Contains("Player4", json);
        Assert.Contains("Low", json);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(IntentStore.Load());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(IntentStore.Path, "{ this is not valid json");
        Assert.Null(IntentStore.Load());
    }

    [Fact]
    public void Save_CreatesBackupOnOverwrite()
    {
        IntentStore.Save(LightIntent.FromStatic(new LightState(1, 1, 1, PlayerLeds.Player1, LedBrightness.High)));
        IntentStore.Save(LightIntent.FromStatic(new LightState(2, 2, 2, PlayerLeds.Player1, LedBrightness.High)));
        Assert.True(File.Exists(IntentStore.Path + ".backup"));
    }

    [Fact]
    public void ToLightState_MapsColourFields()
    {
        var s = LightIntent.FromStatic(new LightState(9, 8, 7, PlayerLeds.Player3, LedBrightness.Medium)).ToLightState();
        Assert.Equal((byte)9, s.R);
        Assert.Equal((byte)8, s.G);
        Assert.Equal((byte)7, s.B);
        Assert.Equal(PlayerLeds.Player3, s.Player);
        Assert.Equal(LedBrightness.Medium, s.Brightness);
    }

    [Fact]
    public void PlayerEffect_RoundTrips()
    {
        var intent = LightIntent.FromStatic(new LightState(1, 2, 3, PlayerLeds.Player1, LedBrightness.High));
        intent.PlayerEffect = PlayerLedEffect.Twinkle;
        Assert.True(IntentStore.Save(intent).Success);

        var loaded = IntentStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(PlayerLedEffect.Twinkle, loaded!.PlayerEffect);
    }

    [Fact]
    public void PlayerEffect_DefaultsToNone()
    {
        var intent = LightIntent.FromStatic(new LightState(0, 0, 0, PlayerLeds.Player1, LedBrightness.High));
        Assert.Equal(PlayerLedEffect.None, intent.PlayerEffect);
    }

    [Fact]
    public void PlayerEffectFps_RoundTrips_DefaultsTo6()
    {
        var fresh = LightIntent.FromStatic(new LightState(0,0,0, PlayerLeds.Player1, LedBrightness.High));
        Assert.Equal(6, fresh.PlayerEffectFps);

        fresh.PlayerEffectFps = 12;
        Assert.True(IntentStore.Save(fresh).Success);
        Assert.Equal(12, IntentStore.Load()!.PlayerEffectFps);
    }
}
