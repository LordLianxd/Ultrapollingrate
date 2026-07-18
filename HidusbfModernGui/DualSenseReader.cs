using System;
using System.Collections.Generic;
using System.Threading;
using HidSharp;

namespace HidusbfModernGui
{
    // Reads the physical DualSense over USB with HidSharp on a background thread and
    // parses each input report into a ControllerState. This is pure I/O: it never
    // transforms anything (deadzones/curves/remap belong to InputTransform/RemapEngine).
    //
    // The byte offsets below are for the USB input report id 0x01 (64 bytes). They were
    // reimplemented from the publicly documented DualSense layout - no DS4Windows code
    // was copied. Verified on the user's real pad in the spike.
    //
    // USB report 0x01 (index 0 = report id):
    //    1 LX, 2 LY, 3 RX, 4 RY   (0..255, 128 = centre)
    //    5 L2, 6 R2               (analog travel, 0..255)
    //    8 face + dpad: bit7 Triangle, bit6 Circle, bit5 Cross, bit4 Square,
    //                   low nibble = dpad hat (0 up, 2 right, 4 down, 6 left, 8 neutral)
    //    9 bit7 R3, bit6 L3, bit5 Options, bit4 Share, bit3 R2btn, bit2 L2btn,
    //                   bit1 R1, bit0 L1
    //   10 bit0 PS, bit1 touchpad click
    //   33 touch point 0: active = (b33 & 0x80) == 0
    //   34/35/36 touch0 X/Y, 12 bits each:
    //                   X = ((b35 & 0x0F) << 8) | b34
    //                   Y = (b36 << 4) | ((b35 & 0xF0) >> 4)
    public sealed class DualSenseReader
    {
        private const int SonyVid = 0x054C;
        // USB gives a 64-byte input report; Bluetooth is 78 and laid out differently, so
        // this spike deliberately only handles the USB pad the user has plugged in.
        private const int UsbReportLength = 64;

        private Thread? _thread;
        private volatile bool _running;
        private HidStream? _stream;
        private readonly object _gate = new();
        private ControllerState _latest = new();
        private long _reports;

        // True while the read thread has an open, live stream. Flips back to false when
        // Stop() closes it or the device is unplugged and the read loop ends.
        public bool Connected { get; private set; }

        // How many input reports we have parsed since Start(). Read from the UI thread;
        // written by the reader thread, hence Interlocked.
        public long ReportsRead => Interlocked.Read(ref _reports);

        // The HidSharp symbolic link of the opened pad, so the caller can hand it to
        // HidHide for blocking. Null until Start() succeeds.
        public string? DevicePath { get; private set; }

        // A defensive copy of the most recent parsed state: the caller (UI/engine) never
        // shares the mutable instance the reader thread keeps writing.
        public ControllerState Snapshot()
        {
            lock (_gate)
            {
                return new ControllerState
                {
                    Left = _latest.Left,
                    Right = _latest.Right,
                    L2 = _latest.L2,
                    R2 = _latest.R2,
                    Pressed = new HashSet<PadButton>(_latest.Pressed),
                    TouchActive = _latest.TouchActive,
                    TouchX = _latest.TouchX,
                    TouchY = _latest.TouchY,
                };
            }
        }

        // First USB DualSense HID collection (the 64-byte gamepad one). Static so the
        // caller can also use it just to test presence without starting a read thread.
        public static HidDevice? FindUsbDualSense()
        {
            foreach (var dev in DeviceList.Local.GetHidDevices(SonyVid))
            {
                try
                {
                    if (dev.GetMaxInputReportLength() == UsbReportLength) return dev;
                }
                catch
                {
                    // A collection we cannot query is not the one we want; keep scanning.
                }
            }
            return null;
        }

        public OpResult Start()
        {
            if (_running) return OpResult.Ok();

            var dev = FindUsbDualSense();
            if (dev == null)
                return OpResult.Fail("No se encontro un DualSense por USB (VID 054C, reporte de 64 bytes).");

            if (!dev.TryOpen(out var stream))
                return OpResult.Fail("No se pudo abrir el DualSense (otra app podria tenerlo en exclusiva).");

            _stream = stream;
            // Short on purpose: the read loop only notices _running=false went false once
            // the current Read() call returns, so this timeout is also the ceiling on how
            // long Stop()'s Join() blocks (previously up to 2000ms - long enough to read as
            // a UI freeze when Stop() ran on the UI thread). The pad streams at up to ~8kHz,
            // so a 150ms window without a single report basically only happens at shutdown.
            _stream.ReadTimeout = 150;
            DevicePath = dev.DevicePath;
            Interlocked.Exchange(ref _reports, 0);
            Connected = true;
            _running = true;
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "DualSenseReader" };
            _thread.Start();
            return OpResult.Ok();
        }

        public void Stop()
        {
            _running = false;
            try { _stream?.Close(); } catch { }
            try { _thread?.Join(1500); } catch { }
            _stream = null;
            _thread = null;
            Connected = false;
        }

        private void ReadLoop()
        {
            var buf = new byte[UsbReportLength];
            try
            {
                while (_running)
                {
                    int n;
                    try { n = _stream!.Read(buf); }
                    catch (TimeoutException) { continue; }   // no report this window; retry
                    catch { break; }                          // closed on Stop, or unplugged

                    // Need through the touch bytes (index 36) and the right report id.
                    if (n >= 37 && buf[0] == 0x01)
                    {
                        var s = Parse(buf);
                        lock (_gate) { _latest = s; }
                        Interlocked.Increment(ref _reports);
                    }
                }
            }
            finally
            {
                Connected = false;
            }
        }

        // Pure parse from a raw USB report to a normalized ControllerState. Static and
        // side-effect free so it can be unit-tested from a captured dump later.
        public static ControllerState Parse(byte[] r)
        {
            var s = new ControllerState
            {
                // Sticks normalized to -1..1. Y is negated so "up" is +1 (the intuitive
                // math convention); VirtualPad negates it back to the DS4 0=up byte.
                Left = new StickInput(AxisToUnit(r[1]), -AxisToUnit(r[2])),
                Right = new StickInput(AxisToUnit(r[3]), -AxisToUnit(r[4])),
                L2 = r[5] / 255.0,
                R2 = r[6] / 255.0,
            };

            byte b8 = r[8];
            if ((b8 & 0x80) != 0) s.Pressed.Add(PadButton.Triangle);
            if ((b8 & 0x40) != 0) s.Pressed.Add(PadButton.Circle);
            if ((b8 & 0x20) != 0) s.Pressed.Add(PadButton.Cross);
            if ((b8 & 0x10) != 0) s.Pressed.Add(PadButton.Square);
            AddDpad(s, (byte)(b8 & 0x0F));

            byte b9 = r[9];
            if ((b9 & 0x80) != 0) s.Pressed.Add(PadButton.R3);
            if ((b9 & 0x40) != 0) s.Pressed.Add(PadButton.L3);
            if ((b9 & 0x20) != 0) s.Pressed.Add(PadButton.Options);
            if ((b9 & 0x10) != 0) s.Pressed.Add(PadButton.Share);
            if ((b9 & 0x08) != 0) s.Pressed.Add(PadButton.R2);
            if ((b9 & 0x04) != 0) s.Pressed.Add(PadButton.L2);
            if ((b9 & 0x02) != 0) s.Pressed.Add(PadButton.R1);
            if ((b9 & 0x01) != 0) s.Pressed.Add(PadButton.L1);

            byte b10 = r[10];
            if ((b10 & 0x01) != 0) s.Pressed.Add(PadButton.PS);
            if ((b10 & 0x02) != 0) s.Pressed.Add(PadButton.TouchpadClick);

            s.TouchActive = (r[33] & 0x80) == 0;
            s.TouchX = ((r[35] & 0x0F) << 8) | r[34];
            s.TouchY = (r[36] << 4) | ((r[35] & 0xF0) >> 4);

            return s;
        }

        // 0..255 (128 centre) -> -1..1, clamped.
        private static double AxisToUnit(byte v)
        {
            double d = (v - 128) / 127.0;
            return d < -1.0 ? -1.0 : d > 1.0 ? 1.0 : d;
        }

        // Clock hat -> individual dpad flags. 8 (and anything else) means neutral.
        private static void AddDpad(ControllerState s, byte hat)
        {
            switch (hat)
            {
                case 0: s.Pressed.Add(PadButton.DpadUp); break;
                case 1: s.Pressed.Add(PadButton.DpadUp); s.Pressed.Add(PadButton.DpadRight); break;
                case 2: s.Pressed.Add(PadButton.DpadRight); break;
                case 3: s.Pressed.Add(PadButton.DpadDown); s.Pressed.Add(PadButton.DpadRight); break;
                case 4: s.Pressed.Add(PadButton.DpadDown); break;
                case 5: s.Pressed.Add(PadButton.DpadDown); s.Pressed.Add(PadButton.DpadLeft); break;
                case 6: s.Pressed.Add(PadButton.DpadLeft); break;
                case 7: s.Pressed.Add(PadButton.DpadUp); s.Pressed.Add(PadButton.DpadLeft); break;
                default: break;   // 8 = centred
            }
        }
    }
}
