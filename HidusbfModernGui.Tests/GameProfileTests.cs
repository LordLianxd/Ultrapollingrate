using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class GameProfileTests : IDisposable
{
    private readonly string _dir;

    public GameProfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingGP_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        GameProfileStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        GameProfileStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty() => Assert.Empty(GameProfileStore.Load());

    [Fact]
    public void SaveAndLoad_RoundTripsLightAndRemap()
    {
        var p = new GameProfile
        {
            Name = "Warzone",
            Rate = 8000,
            Light = new LightIntent { R = 10, G = 20, B = 30, PlayerEffect = PlayerLedEffect.Breathe },
            Remap = new RemapSettings { LeftDeadzonePct = 12, LeftCurve = ResponseCurve.Propia },
        };
        Assert.True(GameProfileStore.Save(new[] { p }).Success);

        var back = GameProfileStore.Load();
        Assert.Single(back);
        Assert.Equal("Warzone", back[0].Name);
        Assert.Equal(8000, back[0].Rate);
        Assert.Equal(20, back[0].Light!.G);
        Assert.Equal(PlayerLedEffect.Breathe, back[0].Light!.PlayerEffect);
        Assert.Equal(12, back[0].Remap!.LeftDeadzonePct);
        Assert.Equal(ResponseCurve.Propia, back[0].Remap!.LeftCurve);
    }

    [Fact]
    public void Migrate_MergesByName()
    {
        var light = new[] { new LightProfile { Name = "Apex", Rate = 1000, R = 255 } };
        var remap = new[] { new RemapProfile { Name = "Apex", Settings = new RemapSettings { RightDeadzonePct = 7 } } };

        var merged = GameProfileStore.Migrate(light, remap);

        var apex = Assert.Single(merged);
        Assert.Equal("Apex", apex.Name);
        Assert.Equal(1000, apex.Rate);
        Assert.Equal(255, apex.Light!.R);
        Assert.Equal(7, apex.Remap!.RightDeadzonePct);
    }

    [Fact]
    public void Migrate_KeepsUnmatchedHalves()
    {
        var light = new[] { new LightProfile { Name = "SoloLuz", B = 99 } };
        var remap = new[] { new RemapProfile { Name = "SoloMando", Settings = new RemapSettings { L2PointPct = 40 } } };

        var merged = GameProfileStore.Migrate(light, remap);

        Assert.Equal(2, merged.Count);
        var luz = merged.Single(p => p.Name == "SoloLuz");
        Assert.NotNull(luz.Light);
        Assert.Null(luz.Remap);
        var mando = merged.Single(p => p.Name == "SoloMando");
        Assert.Null(mando.Light);
        Assert.Equal(40, mando.Remap!.L2PointPct);
    }

    [Fact]
    public void Migrate_SkipsInternalLastUsedPseudoProfile()
    {
        var remap = new[]
        {
            new RemapProfile { Name = "__ultimo_usado__", Settings = new RemapSettings() },
            new RemapProfile { Name = "Real", Settings = new RemapSettings() },
        };
        var merged = GameProfileStore.Migrate(Array.Empty<LightProfile>(), remap);
        Assert.Equal("Real", Assert.Single(merged).Name);
    }

    [Fact]
    public void Migrate_IsCaseInsensitiveOnName()
    {
        var light = new[] { new LightProfile { Name = "duo", R = 5 } };
        var remap = new[] { new RemapProfile { Name = "DUO", Settings = new RemapSettings() } };
        var merged = GameProfileStore.Migrate(light, remap);
        Assert.Single(merged);
        Assert.NotNull(merged[0].Light);
        Assert.NotNull(merged[0].Remap);
    }

    [Fact]
    public void Migrate_EmptyInputs_ReturnsEmpty()
        => Assert.Empty(GameProfileStore.Migrate(Array.Empty<LightProfile>(), Array.Empty<RemapProfile>()));
}
