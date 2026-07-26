using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class PalettePresetTests : IDisposable
{
    private readonly string _dir;

    public PalettePresetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingPal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        PaletteStore.OverrideDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        PaletteStore.OverrideDirectoryForTests(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsEmpty() => Assert.Empty(PaletteStore.Load());

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        Assert.True(PaletteStore.Save(new[] { "FF0000", "00FF00" }).Success);
        Assert.Equal(new[] { "FF0000", "00FF00" }, PaletteStore.Load());
    }

    [Fact]
    public void Add_NormalisesToSixUppercaseDigits()
    {
        var list = new List<string>();
        Assert.True(PaletteStore.Add(list, "#f83e64"));
        Assert.Equal("F83E64", Assert.Single(list));
    }

    // Guardar dos veces el mismo color llenaria la paleta de duplicados que el usuario no
    // puede distinguir de un vistazo.
    [Fact]
    public void Add_RejectsAColourAlreadyInThePalette()
    {
        var list = new List<string> { "F83E64" };
        Assert.False(PaletteStore.Add(list, "f83e64"));
        Assert.Single(list);
    }

    [Fact]
    public void Add_RejectsWhatIsNotAColour()
    {
        var list = new List<string>();
        Assert.False(PaletteStore.Add(list, "no soy un color"));
        Assert.Empty(list);
    }

    // Al llegar al tope entra el nuevo y sale el mas viejo: una paleta es un historial.
    [Fact]
    public void Add_AtTheCap_DropsTheOldest()
    {
        var list = new List<string>();
        for (int i = 0; i < PaletteStore.MaxColours; i++)
            Assert.True(PaletteStore.Add(list, $"{i:X2}0000"));

        Assert.Equal(PaletteStore.MaxColours, list.Count);
        string oldest = list[0];

        Assert.True(PaletteStore.Add(list, "ABCDEF"));
        Assert.Equal(PaletteStore.MaxColours, list.Count);
        Assert.DoesNotContain(oldest, list);
        Assert.Equal("ABCDEF", list[^1]);
    }

    [Fact]
    public void Load_WithCorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        File.WriteAllText(PaletteStore.Path, "{ esto no es json");
        Assert.Empty(PaletteStore.Load());
    }

    // Un archivo editado a mano puede traer basura mezclada con colores validos. Se queda
    // lo que sea un color y se tira el resto, en vez de perder la paleta entera.
    [Fact]
    public void Load_KeepsOnlyTheEntriesThatAreColours()
    {
        File.WriteAllText(PaletteStore.Path, "[\"FF0000\",\"zzz\",\"#00FF00\",\"\"]");
        Assert.Equal(new[] { "FF0000", "00FF00" }, PaletteStore.Load());
    }
}
