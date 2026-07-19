using System;
using System.Collections.Generic;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class CurveLibraryStoreTests : IDisposable
{
    private readonly string _dir;

    public CurveLibraryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        CurveLibraryStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        CurveLibraryStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty()
    {
        Assert.Empty(CurveLibraryStore.Load());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNamedCurves()
    {
        var curva = new SavedCurve
        {
            Name = "Mi curva de franco",
            Points = new() { new(0, 0), new(0.2, 0.1), new(0.5, 0.3), new(0.8, 0.7), new(1, 1) },
        };
        Assert.True(CurveLibraryStore.Save(new[] { curva }).Success);

        var back = CurveLibraryStore.Load();
        Assert.Single(back);
        Assert.Equal("Mi curva de franco", back[0].Name);
        Assert.Equal(0.3, back[0].Points[2].Y, 3);
    }

    [Fact]
    public void Load_SanitizesHandEditedPoints()
    {
        // Un JSON editado a mano con 3 puntos no puede romper la UI (que asume 5).
        var rota = new SavedCurve { Name = "rota", Points = new() { new(0, 0), new(0.5, 0.9), new(1, 1) } };
        Assert.True(CurveLibraryStore.Save(new[] { rota }).Success);

        var back = CurveLibraryStore.Load();
        Assert.Equal(5, back[0].Points.Count);   // reseteada a la valida por defecto
    }
}
