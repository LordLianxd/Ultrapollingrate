using System.Collections.Generic;
using System.IO;
using System;
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
}
