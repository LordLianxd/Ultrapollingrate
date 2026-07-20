namespace HidusbfModernGui
{
    // Fuente de estado fisico para el visualizador cuando el MOTOR esta apagado (el fisico
    // esta visible y lo podemos abrir de solo-lectura). Cuando el motor esta encendido NO se
    // usa: MainWindow lee el snapshot del propio lector del motor, porque abrir un segundo
    // handle competiria con el reinicio de devnode del arranque (leccion L1). Envoltorio
    // delgado de DualSenseReader para aislar ese ciclo de vida del motor.
    public sealed class VisualizerFeed
    {
        private DualSenseReader? _reader;
        public bool OwnReaderActive => _reader != null;

        public void StartOwnReader()
        {
            if (_reader != null) return;
            var r = new DualSenseReader();
            if (r.Start().Success) _reader = r;
        }

        public void StopOwnReader()
        {
            try { _reader?.Stop(); } catch { }
            _reader = null;
        }

        // Snapshot fisico crudo o null si no hay lector propio vivo.
        public ControllerState? PhysicalSnapshot() => _reader?.Snapshot();
    }
}
