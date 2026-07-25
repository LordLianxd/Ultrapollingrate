using System.Linq;
using HidusbfModernGui;
using Xunit;

public class PadDiagramTests
{
    [Fact]
    public void Anchors_CoverTheSixteenRemappableButtons()
    {
        Assert.Equal(16, PadDiagram.Anchors.Count);
        Assert.Equal(8, PadDiagram.Anchors.Count(a => a.Left));
        Assert.Equal(8, PadDiagram.Anchors.Count(a => !a.Left));
        Assert.Contains(PadDiagram.Anchors, a => a.Button == PadButton.Cross);
        Assert.Contains(PadDiagram.Anchors, a => a.Button == PadButton.L3);
        // PS y el click del touchpad no se remapean desde aqui.
        Assert.DoesNotContain(PadDiagram.Anchors, a => a.Button == PadButton.PS);
        Assert.DoesNotContain(PadDiagram.Anchors, a => a.Button == PadButton.TouchpadClick);
    }

    [Fact]
    public void Anchors_AreInsideTheImage()
    {
        Assert.All(PadDiagram.Anchors, a =>
        {
            Assert.InRange(a.X, 0, PadDiagram.DiagramWidth);
            Assert.InRange(a.Y, 0, PadDiagram.DiagramHeight);
        });
    }

    [Fact]
    public void Anchors_HaveNoDuplicateButtons()
        => Assert.Equal(PadDiagram.Anchors.Count,
                        PadDiagram.Anchors.Select(a => a.Button).Distinct().Count());

    [Fact]
    public void LayoutLabels_KeepsTheMinimumGap()
    {
        var left = PadDiagram.Anchors.Where(a => a.Left);
        var placed = PadDiagram.LayoutLabels(left, 70).OrderBy(p => p.Y).ToList();

        for (int i = 1; i < placed.Count; i++)
            Assert.True(placed[i].Y - placed[i - 1].Y >= 70 - 0.001,
                        $"{placed[i - 1].Button} y {placed[i].Button} se pisan");
    }

    [Fact]
    public void LayoutLabels_KeepsTheVerticalOrderOfTheAnchors()
    {
        var left = PadDiagram.Anchors.Where(a => a.Left).OrderBy(a => a.Y).Select(a => a.Button).ToList();
        var placed = PadDiagram.LayoutLabels(PadDiagram.Anchors.Where(a => a.Left), 70)
                               .OrderBy(p => p.Y).Select(p => p.Button).ToList();
        Assert.Equal(left, placed);
    }

    [Fact]
    public void LayoutLabels_PutsEachColumnOnItsSide()
    {
        Assert.All(PadDiagram.LayoutLabels(PadDiagram.Anchors.Where(a => a.Left), 70),
                   p => Assert.Equal(PadDiagram.LabelColumnLeft, p.X, 3));
        Assert.All(PadDiagram.LayoutLabels(PadDiagram.Anchors.Where(a => !a.Left), 70),
                   p => Assert.Equal(PadDiagram.LabelColumnRight, p.X, 3));
    }

    [Fact]
    public void LayoutLabels_Empty_ReturnsEmpty()
        => Assert.Empty(PadDiagram.LayoutLabels(System.Array.Empty<PadAnchor>(), 70));

    [Fact]
    public void Canvas_IsWiderThanTheImage_SoLabelsHaveRoom()
    {
        Assert.True(PadDiagram.CanvasWidth > PadDiagram.DiagramWidth);
        // La imagen queda centrada en el lienzo.
        Assert.Equal((PadDiagram.CanvasWidth - PadDiagram.DiagramWidth) / 2, PadDiagram.ImageOffsetX, 3);
    }

    [Fact]
    public void LabelColumns_AreOutsideThePadSilhouette()
    {
        // La silueta ocupa x 200..2200 DE LA IMAGEN; en el lienzo, +ImageOffsetX.
        double padLeft = 200 + PadDiagram.ImageOffsetX;
        double padRight = 2200 + PadDiagram.ImageOffsetX;
        Assert.True(PadDiagram.LabelColumnLeft < padLeft - 100,
            "la columna izquierda debe quedar bien fuera del mando");
        Assert.True(PadDiagram.LabelColumnRight > padRight + 100,
            "la columna derecha debe quedar bien fuera del mando");
    }

    [Fact]
    public void LabelColumns_AreInsideTheCanvas()
    {
        Assert.InRange(PadDiagram.LabelColumnLeft, 0, PadDiagram.CanvasWidth);
        Assert.InRange(PadDiagram.LabelColumnRight, 0, PadDiagram.CanvasWidth);
    }

    [Fact]
    public void AnchorX_TranslatesImageCoordsToCanvas()
    {
        var a = PadDiagram.Anchors.First(z => z.Button == PadButton.Cross);
        Assert.Equal(a.X + PadDiagram.ImageOffsetX, PadDiagram.AnchorX(a), 3);
    }
}
