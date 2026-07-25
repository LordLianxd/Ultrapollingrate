using System;
using System.IO;
using HidusbfModernGui;
using Xunit;

public class PadSkinTests : IDisposable
{
    private readonly string _dir;

    public PadSkinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "UltraPollingSkin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteSkin(string json, params string[] files)
    {
        File.WriteAllText(Path.Combine(_dir, "skin.json"), json);
        foreach (var f in files) File.WriteAllBytes(Path.Combine(_dir, f), new byte[] { 1, 2, 3 });
        return _dir;
    }

    private const string ValidJson = """
    {
      "Name": "Prueba",
      "BaseFile": "base.png",
      "BaseWidth": 1050,
      "BaseHeight": 850,
      "StickRadius": 22,
      "Parts": {
        "stick.left":  { "File": "sticks.png", "Src": { "X": 0, "Y": 0, "W": 117, "H": 116 }, "Dst": { "X": 310, "Y": 435, "W": 117, "H": 116 } },
        "btn.cross":   { "File": "faces.png",  "Src": { "X": 70, "Y": 140, "W": 71, "H": 71 }, "Dst": { "X": 795, "Y": 400, "W": 71, "H": 71 } }
      }
    }
    """;

    [Fact]
    public void Load_ValidSkin_ReturnsSkinWithParts()
    {
        var dir = WriteSkin(ValidJson, "base.png", "sticks.png", "faces.png");
        var (skin, err) = PadSkinLoader.Load(dir);

        Assert.Null(err);
        Assert.NotNull(skin);
        Assert.Equal("Prueba", skin!.Name);
        Assert.Equal(1050, skin.BaseWidth, 3);
        Assert.Equal(2, skin.Parts.Count);
        Assert.Equal(117, skin.Parts["stick.left"].Src.W, 3);
    }

    [Fact]
    public void Load_MissingManifest_ReturnsError()
    {
        var (skin, err) = PadSkinLoader.Load(_dir);   // directorio vacio
        Assert.Null(skin);
        Assert.NotNull(err);
    }

    [Fact]
    public void Load_MissingBaseImage_ReturnsError()
    {
        var dir = WriteSkin(ValidJson);   // sin PNGs
        var (skin, err) = PadSkinLoader.Load(dir);
        Assert.Null(skin);
        Assert.Contains("base.png", err!);
    }

    [Fact]
    public void Load_BrokenJson_ReturnsErrorNotException()
    {
        File.WriteAllText(Path.Combine(_dir, "skin.json"), "{ esto no es json ");
        var (skin, err) = PadSkinLoader.Load(_dir);
        Assert.Null(skin);
        Assert.NotNull(err);
    }

    [Fact]
    public void Load_ZeroBaseSize_IsRejected()
    {
        var bad = ValidJson.Replace("\"BaseWidth\": 1050", "\"BaseWidth\": 0");
        var dir = WriteSkin(bad, "base.png", "sticks.png", "faces.png");
        var (skin, err) = PadSkinLoader.Load(dir);
        Assert.Null(skin);
        Assert.NotNull(err);
    }

    [Fact]
    public void Load_PartWithMissingFile_IsDroppedNotFatal()
    {
        // Una parte que apunta a un PNG inexistente se descarta; el resto del skin sirve.
        var dir = WriteSkin(ValidJson, "base.png", "sticks.png");   // falta faces.png
        var (skin, err) = PadSkinLoader.Load(dir);

        Assert.Null(err);
        Assert.NotNull(skin);
        Assert.True(skin!.Parts.ContainsKey("stick.left"));
        Assert.False(skin.Parts.ContainsKey("btn.cross"));
    }

    [Fact]
    public void FindFirstSkinDir_PicksDirectoryContainingManifest()
    {
        string root = Path.Combine(_dir, "skins");
        string mine = Path.Combine(root, "ps5");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "skin.json"), ValidJson);

        Assert.Equal(mine, PadSkinLoader.FindFirstSkinDir(root));
    }

    [Fact]
    public void FindFirstSkinDir_NoSkins_ReturnsNull()
    {
        Assert.Null(PadSkinLoader.FindFirstSkinDir(Path.Combine(_dir, "no-existe")));
    }
}
