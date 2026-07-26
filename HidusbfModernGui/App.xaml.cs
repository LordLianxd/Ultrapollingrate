using System;
using System.Threading;
using System.Windows;

namespace HidusbfModernGui;

public partial class App : Application
{
    // Una sola instancia. Sin esto, y ahora que la app vive en la bandeja, abrir el acceso
    // directo otra vez -algo muy facil de hacer, porque la ventana escondida NO sale en la
    // barra de tareas- daria dos iconos en la bandeja y dos motores peleandose por el mismo
    // DualSense: dos escritores de luz desfasados y dos intentos de ocultar el fisico.
    //
    // El mutex marca "ya hay una"; el evento con nombre es como la segunda le pide a la
    // primera que se muestre. Hacen falta los dos: un mutex solo detecta, no comunica.
    private const string InstanceMutexName = @"Local\UltraPolling.SingleInstance";
    private const string ShowRequestEventName = @"Local\UltraPolling.ShowRequest";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showRequest;
    private CancellationTokenSource? _showListenerStop;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool esLaPrimera);

        if (!esLaPrimera)
        {
            // Ya hay una corriendo, probablemente escondida en la bandeja. Se le pide que se
            // muestre y esta segunda se va sin abrir ventana ni tocar un solo dispositivo.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowRequestEventName, out var existente))
                {
                    existente.Set();
                    existente.Dispose();
                }
            }
            catch (Exception ex)
            {
                // Si el aviso falla, la segunda instancia se va igual: quedarse seria peor que
                // no haber conseguido traer la ventana al frente.
                System.Diagnostics.Debug.WriteLine($"Aviso a la instancia existente: {ex.Message}");
            }

            // Environment.Exit y no Shutdown(): con StartupUri puesto, WPF ya tiene la creacion
            // de MainWindow encolada y llegaria a abrirla igualmente.
            Environment.Exit(0);
            return;
        }

        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        _showListenerStop = new CancellationTokenSource();
        EscucharPeticionesDeMostrar(_showListenerStop.Token);

        base.OnStartup(e);
    }

    // Hilo de fondo esperando sobre el evento. IsBackground para que no mantenga vivo el
    // proceso al salir: si fuera de primer plano, "Salir" cerraria las ventanas y el proceso
    // se quedaria en el administrador de tareas.
    private void EscucharPeticionesDeMostrar(CancellationToken stop)
    {
        var hilo = new Thread(() =>
        {
            var esperas = new WaitHandle[] { _showRequest!, stop.WaitHandle };
            while (true)
            {
                int cual = WaitHandle.WaitAny(esperas);
                if (cual != 0) return;   // se pidio parar

                // El evento llega en un hilo de fondo; tocar ventanas exige el de UI.
                Dispatcher.Invoke(() =>
                {
                    if (Current?.MainWindow is MainWindow ventana) ventana.ShowFromTrayRequest();
                });
            }
        })
        { IsBackground = true, Name = "UltraPolling.ShowRequest" };

        hilo.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showListenerStop?.Cancel();
        _showRequest?.Dispose();

        // ReleaseMutex solo vale desde el hilo que lo posee, y este es el mismo que corrio
        // OnStartup. Si el proceso muriera de otra forma, Windows lo libera al cerrar el
        // handle, asi que una instancia caida nunca deja bloqueada a la siguiente.
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
