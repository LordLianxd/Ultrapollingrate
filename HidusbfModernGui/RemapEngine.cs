using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Aplica una RemapSettings (lo que edita el usuario en la UI) a un ControllerState leido
    // del mando fisico y produce el ControllerState transformado que se empuja al DS4 virtual.
    //
    // Es PURO: entra estado + ajustes, sale estado. Sin hardware, sin hilos, sin WPF -> se
    // prueba a fondo con tests. El lazo de E/S vive fuera:
    //     DualSenseReader.Snapshot()  ->  RemapEngine.Transform(estado, settings)  ->  VirtualPad.Push()
    // Aqui solo esta la transformacion; toda la aritmetica fina (deadzone radial, curvas,
    // hair-trigger, remapeo, zonas) la hace InputTransform, que ya tiene sus propios tests.
    public static class RemapEngine
    {
        // El touchpad del DualSense reporta ~1920x1080; el centro parte las 4 zonas
        // (Arriba/Abajo x Izq/Der). InputTransform.ResolveTouchZone recibe estos cortes.
        private const int TouchXSplit = 960;
        private const int TouchYSplit = 540;

        public static ControllerState Transform(ControllerState input, RemapSettings s)
        {
            if (input == null) return new ControllerState();
            if (s == null) return input;   // sin ajustes: passthrough

            // 1. Sticks: deadzone radial (por magnitud, preserva el angulo) + alcance + curva
            //    de respuesta, con los ajustes propios de cada stick.
            var (lx, ly) = InputTransform.ApplyStick(
                input.Left, s.LeftInnerDeadzone, s.LeftOuterDeadzone, s.LeftCurve, s.LeftCurvaturePct, s.LeftCurvePoints);
            var (rx, ry) = InputTransform.ApplyStick(
                input.Right, s.RightInnerDeadzone, s.RightOuterDeadzone, s.RightCurve, s.RightCurvaturePct, s.RightCurvePoints);

            // 2. Gatillos: hair-trigger. Con punto 0 es passthrough (sin efecto); con punto>0
            //    el analog salta a 0 o a fondo segun cruce el umbral.
            double l2 = InputTransform.ApplyTrigger(input.L2, s.L2Point);
            double r2 = InputTransform.ApplyTrigger(input.R2, s.R2Point);

            // 3. Conjunto de botones EFECTIVO antes de remapear: parte del fisico y, si el
            //    hair-trigger esta activo, el bit del boton L2/R2 sigue al analog transformado
            //    (a fondo => pulsado; por debajo => suelto), para que el gatillo "dispare"
            //    completo -analog Y boton- antes del recorrido fisico.
            var effective = new HashSet<PadButton>(input.Pressed);
            ApplyTriggerButton(effective, PadButton.L2, s.L2Point, l2);
            ApplyTriggerButton(effective, PadButton.R2, s.R2Point, r2);

            // 4. Remapeo de botones: cada boton pulsado pasa por la tabla (identidad si no
            //    tiene entrada). Varios origenes pueden caer en el mismo destino (HashSet dedup).
            var pressed = new HashSet<PadButton>();
            foreach (var b in effective)
                pressed.Add(InputTransform.Remap(b, s.ButtonRemap));

            // 5. Zonas del touchpad -> boton virtual. Si el toque cae en una zona mapeada, ese
            //    boton se anade (ademas del remapeo normal de arriba).
            var zone = InputTransform.ResolveTouchZone(
                input.TouchActive, input.TouchX, input.TouchY, TouchXSplit, TouchYSplit);
            if (s.TouchZoneMap != null &&
                s.TouchZoneMap.TryGetValue(zone, out var zoneBtn) && zoneBtn != PadButton.None)
                pressed.Add(zoneBtn);

            return new ControllerState
            {
                Left = new StickInput(lx, ly),
                Right = new StickInput(rx, ry),
                L2 = l2,
                R2 = r2,
                Pressed = pressed,
                // Las coordenadas del toque pasan tal cual (el DS4 virtual no las usa; la parte
                // funcional del touchpad es la zona->boton de arriba), pero mantenerlas deja el
                // ControllerState de salida coherente por si otra capa las lee.
                TouchActive = input.TouchActive,
                TouchX = input.TouchX,
                TouchY = input.TouchY,
            };
        }

        // Con hair-trigger activo (point>0), fuerza el bit del boton del gatillo segun el
        // analog ya transformado. Sin hair-trigger (point<=0), respeta el boton fisico tal
        // como venia en el conjunto.
        private static void ApplyTriggerButton(HashSet<PadButton> set, PadButton btn, double point, double analog)
        {
            if (point <= 0.0) return;          // passthrough: no tocar el boton fisico
            if (analog >= 1.0) set.Add(btn);   // disparo => boton pulsado
            else set.Remove(btn);              // por debajo del punto => suelto
        }
    }
}
