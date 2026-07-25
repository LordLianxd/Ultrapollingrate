namespace HidusbfModernGui
{
    // Las paginas del configurador del mando. El hub muestra tarjetas; cada tarjeta abre
    // una pagina con su cabecera (atras + titulo + ayuda).
    public enum ConfigPage { Hub, Botones, Sticks, Gatillos, Touchpad }

    // Navegacion de un solo nivel: del hub se entra a una pagina y de una pagina se vuelve
    // al hub. Deliberadamente NO es una pila: no hay caminos de pagina a pagina, asi que
    // una pila solo podria desincronizarse. Pura (sin WPF) para poder probarla.
    public sealed class ConfigNav
    {
        public ConfigPage Current { get; private set; } = ConfigPage.Hub;
        public bool CanGoBack => Current != ConfigPage.Hub;

        public void Go(ConfigPage page) => Current = page;
        public void Back() => Current = ConfigPage.Hub;

        public static string TitleOf(ConfigPage page) => page switch
        {
            ConfigPage.Botones  => "Asignacion de botones",
            ConfigPage.Sticks   => "Sensibilidad y zona muerta de los sticks",
            ConfigPage.Gatillos => "Recorrido de los gatillos",
            ConfigPage.Touchpad => "Zonas del touchpad",
            _                   => "Configurar el mando",
        };
    }
}
