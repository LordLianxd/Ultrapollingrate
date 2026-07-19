using System;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;

namespace HidusbfModernGui
{
    // A virtual DualShock4 created through ViGEm (the ViGEmBus driver). Push(state) maps
    // a parsed ControllerState onto the virtual pad and submits one report. The engine
    // loop (EngineTick) feeds it RemapEngine.Transform's output, so what lands here is
    // the already-transformed state, never the raw physical read.
    public sealed class VirtualPad
    {
        private ViGEmClient? _client;
        private IDualShock4Controller? _pad;

        public bool Connected { get; private set; }

        public OpResult Connect()
        {
            if (Connected) return OpResult.Ok();
            try
            {
                _client = new ViGEmClient();                 // throws if ViGEmBus missing
                _pad = _client.CreateDualShock4Controller();
                // Batch a whole frame's worth of set-calls, then submit once, instead of
                // firing a report per property change.
                _pad.AutoSubmitReport = false;
                _pad.Connect();
                Connected = true;
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                Disconnect();
                return OpResult.Fail($"No se pudo crear el DS4 virtual (ViGEmBus): {ex.Message}");
            }
        }

        public void Disconnect()
        {
            try { _pad?.Disconnect(); } catch { }
            try { _client?.Dispose(); } catch { }
            _pad = null;
            _client = null;
            Connected = false;
        }

        // Mirror one ControllerState onto the virtual pad and submit.
        public void Push(ControllerState s)
        {
            var pad = _pad;
            if (pad == null || s == null) return;

            // Sticks: -1..1 -> 0..255 (128 centre). Y is negated back because the DS4
            // axis convention is 0 = up / 255 = down, while we store "up" as +1.
            pad.SetAxisValue(DualShock4Axis.LeftThumbX, ToByte(s.Left.X));
            pad.SetAxisValue(DualShock4Axis.LeftThumbY, ToByte(-s.Left.Y));
            pad.SetAxisValue(DualShock4Axis.RightThumbX, ToByte(s.Right.X));
            pad.SetAxisValue(DualShock4Axis.RightThumbY, ToByte(-s.Right.Y));

            // Analog triggers: 0..1 -> 0..255. Their digital L2/R2 button bits are set
            // separately below (the DS4 report carries both).
            pad.SetSliderValue(DualShock4Slider.LeftTrigger, ToTrigger(s.L2));
            pad.SetSliderValue(DualShock4Slider.RightTrigger, ToTrigger(s.R2));

            var p = s.Pressed;
            pad.SetButtonState(DualShock4Button.Cross, p.Contains(PadButton.Cross));
            pad.SetButtonState(DualShock4Button.Circle, p.Contains(PadButton.Circle));
            pad.SetButtonState(DualShock4Button.Square, p.Contains(PadButton.Square));
            pad.SetButtonState(DualShock4Button.Triangle, p.Contains(PadButton.Triangle));
            pad.SetButtonState(DualShock4Button.ShoulderLeft, p.Contains(PadButton.L1));
            pad.SetButtonState(DualShock4Button.ShoulderRight, p.Contains(PadButton.R1));
            pad.SetButtonState(DualShock4Button.TriggerLeft, p.Contains(PadButton.L2));
            pad.SetButtonState(DualShock4Button.TriggerRight, p.Contains(PadButton.R2));
            pad.SetButtonState(DualShock4Button.ThumbLeft, p.Contains(PadButton.L3));
            pad.SetButtonState(DualShock4Button.ThumbRight, p.Contains(PadButton.R3));
            pad.SetButtonState(DualShock4Button.Share, p.Contains(PadButton.Share));
            pad.SetButtonState(DualShock4Button.Options, p.Contains(PadButton.Options));

            pad.SetDPadDirection(Dpad(p));

            // PS + touchpad click share the "special buttons" byte (Ps = 0x01, Touchpad = 0x02).
            byte special = 0;
            if (p.Contains(PadButton.PS)) special |= 0x01;
            if (p.Contains(PadButton.TouchpadClick)) special |= 0x02;
            pad.SetSpecialButtonsFull(special);

            try { pad.SubmitReport(); }
            catch { /* pad may be mid-disconnect; the next frame recovers */ }
        }

        private static DualShock4DPadDirection Dpad(System.Collections.Generic.HashSet<PadButton> p)
        {
            bool up = p.Contains(PadButton.DpadUp);
            bool down = p.Contains(PadButton.DpadDown);
            bool left = p.Contains(PadButton.DpadLeft);
            bool right = p.Contains(PadButton.DpadRight);
            if (up && right) return DualShock4DPadDirection.Northeast;
            if (up && left) return DualShock4DPadDirection.Northwest;
            if (down && right) return DualShock4DPadDirection.Southeast;
            if (down && left) return DualShock4DPadDirection.Southwest;
            if (up) return DualShock4DPadDirection.North;
            if (down) return DualShock4DPadDirection.South;
            if (left) return DualShock4DPadDirection.West;
            if (right) return DualShock4DPadDirection.East;
            return DualShock4DPadDirection.None;
        }

        private static byte ToByte(double v)
        {
            double b = Math.Round(v * 127.0 + 128.0);
            return (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
        }

        private static byte ToTrigger(double v)
        {
            double b = Math.Round(v * 255.0);
            return (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
        }
    }
}
