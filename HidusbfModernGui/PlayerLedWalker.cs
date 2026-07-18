using System;

namespace HidusbfModernGui
{
    // Efectos animados de las 5 luces de jugador. Cada luz es un bit del mask de 5 bits que
    // va en el byte 44 del reporte 0x02 (bit0 = izquierda .. bit4 = derecha). No es color:
    // son las luces blancas bajo el touchpad. La animacion es una secuencia de masks en bucle.
    public enum PlayerLedEffect { None, Charge, Twinkle, Breathe }

    public sealed class PlayerLedWalker
    {
        private readonly byte[] _frames;
        public int FrameMs { get; }

        public PlayerLedWalker(PlayerLedEffect effect)
        {
            var (frames, ms) = FramesFor(effect);
            _frames = frames;
            FrameMs = ms;
        }

        public int FrameCount => _frames.Length;

        // El mask para un indice de frame; hace wrap en ambos sentidos. None -> siempre 0.
        public byte MaskAt(int frameIndex)
        {
            if (_frames.Length == 0) return 0;
            int i = frameIndex % _frames.Length;
            if (i < 0) i += _frames.Length;
            return _frames[i];
        }

        public static int FrameMsFor(PlayerLedEffect effect) => FramesFor(effect).frameMs;

        private static (byte[] frames, int frameMs) FramesFor(PlayerLedEffect effect) => effect switch
        {
            // "1 y 4" = par exterior (bits 0,4 = 17). "2 y 3" = par interior (bits 1,3 = 10).
            // Enciende exterior, agrega interior, deja interior, apaga. Efecto de "carga".
            PlayerLedEffect.Charge => (new byte[] { 17, 27, 10, 0 }, 180),
            // Una sola luz que barre de izquierda a derecha y vuelve (tipo estrellas/knight-rider).
            PlayerLedEffect.Twinkle => (new byte[] { 1, 2, 4, 8, 16, 8, 4, 2 }, 110),
            // Centro hacia afuera y de vuelta: respiracion simetrica.
            PlayerLedEffect.Breathe => (new byte[] { 0, 4, 14, 31, 14, 4 }, 140),
            _ => (Array.Empty<byte>(), 150),
        };
    }
}
