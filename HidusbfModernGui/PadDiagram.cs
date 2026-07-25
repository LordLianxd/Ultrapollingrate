using System;
using System.Collections.Generic;
using System.Linq;

namespace HidusbfModernGui
{
    // Un boton del mando y el punto EXACTO del dibujo donde termina su linea guia, en
    // pixeles de la imagen del diagrama. 'Left' dice a que columna de etiquetas pertenece.
    public readonly record struct PadAnchor(PadButton Button, double X, double Y, bool Left);

    // Geometria de la pagina "Asignacion de botones": donde esta cada boton en el dibujo y
    // donde va su etiqueta. Las anclas se midieron sobre la imagen (2400x1792) dibujando una
    // cruz en cada una y comprobandolas contra el trazo; no son estimaciones a ojo.
    public static class PadDiagram
    {
        public const double DiagramWidth = 2400;
        public const double DiagramHeight = 1792;

        // El lienzo es MAS ANCHO que la imagen a proposito: la silueta del mando ocupa casi
        // todo el ancho de la imagen (x 200..2200 de 2400), asi que las etiquetas no caben
        // dentro sin pisarla. El margen extra es donde viven las dos columnas.
        public const double CanvasWidth = 3600;
        public const double CanvasHeight = 1792;

        // La imagen se dibuja centrada en el lienzo; todo lo medido sobre ella se desplaza
        // por aqui.
        public const double ImageOffsetX = (CanvasWidth - DiagramWidth) / 2;   // 600

        // Borde interior de cada columna de etiquetas, YA en coordenadas del lienzo. Deja
        // ~260 px de aire entre la etiqueta y el borde del mando: antes eran 0 y las
        // etiquetas caian sobre los mangos.
        public const double LabelColumnLeft = 540;
        public const double LabelColumnRight = 3060;

        // Las anclas se midieron sobre la IMAGEN; el dibujo vive en el LIENZO. Una sola suma,
        // en un solo sitio, para que no haya dos sistemas de coordenadas sueltos por el
        // code-behind.
        public static double AnchorX(PadAnchor a) => a.X + ImageOffsetX;

        public static readonly IReadOnlyList<PadAnchor> Anchors = new[]
        {
            new PadAnchor(PadButton.L2,        600,  395,  true),
            new PadAnchor(PadButton.L1,        600,  470,  true),
            new PadAnchor(PadButton.Share,     668,  710,  true),
            new PadAnchor(PadButton.DpadUp,    505,  822,  true),
            new PadAnchor(PadButton.DpadLeft,  385,  940,  true),
            new PadAnchor(PadButton.DpadRight, 645,  940,  true),
            new PadAnchor(PadButton.DpadDown,  515,  1055, true),
            new PadAnchor(PadButton.L3,        840,  1190, true),

            new PadAnchor(PadButton.R2,        1780, 395,  false),
            new PadAnchor(PadButton.R1,        1780, 470,  false),
            new PadAnchor(PadButton.Options,   1700, 705,  false),
            new PadAnchor(PadButton.Triangle,  1878, 768,  false),
            new PadAnchor(PadButton.Square,    1704, 894,  false),
            new PadAnchor(PadButton.Circle,    2028, 894,  false),
            new PadAnchor(PadButton.Cross,     1866, 1032, false),
            new PadAnchor(PadButton.R3,        1520, 1190, false),
        };

        // Reparte las etiquetas de UNA columna en vertical. Cada una quiere estar a la altura
        // de su ancla, pero varias anclas caen casi juntas (DpadLeft y DpadRight comparten Y
        // exacta), asi que se empujan hacia abajo hasta respetar minGap. Se conserva el orden
        // vertical de las anclas: una etiqueta nunca adelanta a otra, o las lineas guia se
        // cruzarian y el dibujo dejaria de leerse.
        public static IReadOnlyList<(PadButton Button, double X, double Y)> LayoutLabels(
            IEnumerable<PadAnchor> anchors, double minGap)
        {
            var sorted = (anchors ?? Enumerable.Empty<PadAnchor>()).OrderBy(a => a.Y).ToList();
            var result = new List<(PadButton, double, double)>(sorted.Count);

            double last = double.NegativeInfinity;
            foreach (var a in sorted)
            {
                double y = Math.Max(a.Y, last + minGap);
                double x = a.Left ? LabelColumnLeft : LabelColumnRight;
                result.Add((a.Button, x, y));
                last = y;
            }
            return result;
        }
    }
}
