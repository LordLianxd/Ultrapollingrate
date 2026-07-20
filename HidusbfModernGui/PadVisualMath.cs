using System;

namespace HidusbfModernGui
{
    // Aritmetica pura del visualizador del mando: sin WPF, sin hardware. Convierte el estado
    // normalizado (-1..1 / 0..1) a numeros de dibujo. El control PadVisual y el punto vivo de
    // la curva la comparten; testeada aqui para no depender de inspeccion visual.
    public static class PadVisualMath
    {
        // Desplazamiento en pixeles del pulgar dentro del pozo del stick. Entrada en
        // convencion de ControllerState (Y=arriba positivo); salida en convencion de PANTALLA
        // (Dy positivo = hacia abajo), por eso Dy = -y*radius. La magnitud se acota a radius:
        // un stick a fondo en diagonal (magnitud 1.414) no puede sacar el pulgar del pozo.
        public static (double Dx, double Dy) StickOffset(double x, double y, double radius)
        {
            double dx = x * radius, dy = -y * radius;
            double mag = Math.Sqrt(dx * dx + dy * dy);
            if (mag > radius && mag > 0)
            {
                double k = radius / mag;
                dx *= k; dy *= k;
            }
            return (dx, dy);
        }

        // Relleno 0..1 para barras de gatillo / cualquier medidor lineal, acotado.
        public static double Fill01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
