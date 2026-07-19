using System.Collections.Generic;
using System.IO;
using System;
using System.Text.Json;
using HidusbfModernGui;
using Xunit;

public class RemapProfileStoreTests : IDisposable
{
    private readonly string _dir;
    public RemapProfileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UPRemapTests_" + Guid.NewGuid().ToString("N"));
        RemapProfileStore.OverrideDirectoryForTests(_dir);
    }
    public void Dispose()
    {
        RemapProfileStore.OverrideDirectoryForTests(null);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RoundTrips()
    {
        var p = new RemapProfile { Name = "FPS", Settings = new RemapSettings {
            LeftDeadzonePct = 12, LeftCurve = ResponseCurve.Precisa, L2PointPct = 25,
            ButtonRemap = new() { [PadButton.Cross] = PadButton.R1 } } };
        Assert.True(RemapProfileStore.Save(new[] { p }).Success);
        var loaded = RemapProfileStore.Load();
        Assert.Single(loaded);
        Assert.Equal("FPS", loaded[0].Name);
        Assert.Equal(12, loaded[0].Settings.LeftDeadzonePct);
        Assert.Equal(ResponseCurve.Precisa, loaded[0].Settings.LeftCurve);
        Assert.Equal(PadButton.R1, loaded[0].Settings.ButtonRemap[PadButton.Cross]);
    }

    [Fact]
    public void CorruptFile_ReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RemapProfileStore.Path, "{ not json");
        Assert.Empty(RemapProfileStore.Load());
    }

    [Fact]
    public void Save_CreatesBackupOnOverwrite()
    {
        RemapProfileStore.Save(new[] { new RemapProfile { Name = "a", Settings = new() } });
        RemapProfileStore.Save(new[] { new RemapProfile { Name = "b", Settings = new() } });
        Assert.True(File.Exists(RemapProfileStore.Path + ".backup"));
    }

    [Fact]
    public void CurvePoints_RoundTripThroughJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var s = new RemapSettings
        {
            LeftCurve = ResponseCurve.Propia,
            LeftCurvePoints = new() { new(0, 0), new(0.3, 0.6), new(0.7, 0.65), new(1, 1) },
        };
        string json = JsonSerializer.Serialize(s, options);
        var back = JsonSerializer.Deserialize<RemapSettings>(json, options)!;
        Assert.Equal(ResponseCurve.Propia, back.LeftCurve);
        Assert.Equal(4, back.LeftCurvePoints.Count);
        Assert.Equal(0.6, back.LeftCurvePoints[1].Y, 3);
    }
}
