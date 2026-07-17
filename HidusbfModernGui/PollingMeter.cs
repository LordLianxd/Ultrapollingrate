using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace HidusbfModernGui
{
    // Measures the polling rate a device is ACTUALLY achieving, by timestamping the HID
    // reports it sends. Until this existed the app could only report the rate it asked
    // for - it had no way to tell the user whether anything actually happened.
    //
    // Proven against the real DualSense: 5019 reports in 5012 ms = 1001.2 Hz measured
    // against 1000 Hz configured, on a 100 ns/tick clock. Measurement is passive; the
    // pad answers every poll without being touched.
    //
    // Caveat the UI must respect: this measures report ARRIVAL. That equals the polling
    // rate only while the device answers every poll. A device with nothing new to say
    // can NAK, and then we measure low. That is a limit of the method, not a bug.
    public sealed class PollingMeter : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        // Roughly a second of history at 1000 Hz, an eighth at 8000 Hz. Enough for a
        // stable median without the readout lagging behind a change the user just made.
        private const int WindowSize = 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        // Marshalled from the real definition rather than hand-indexed out of a byte
        // array. Doing the offsets by hand is how this code previously read
        // FeatureReportByteLength and believed it was InputReportByteLength: on the
        // DualSense both are 64, so it worked by luck and would have broken on any
        // device where they differ.
        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);

        private readonly object _lock = new object();
        private readonly Queue<double> _gaps = new Queue<double>(WindowSize);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private CancellationTokenSource? _cts;
        private Task? _reader;
        private double _lastReportMs = -1;
        private double _lastArrivalMs = -1;

        public bool IsRunning => _reader != null && !_reader.IsCompleted;

        // Non-null once we know the device has no HID interface at all, or the handle
        // could not be opened. The UI shows this instead of a rate: "not measurable" is
        // a different statement from "0 Hz".
        public string? Unavailable { get; private set; }

        // Starts measuring. Returns false when the device cannot be measured at all -
        // the caller should show Unavailable rather than an empty meter.
        public bool Start(string usbInstanceId)
        {
            Stop();

            var paths = HidDeviceLocator.FindHidPaths(usbInstanceId);
            if (paths.Count == 0)
            {
                Unavailable = "sin interfaz HID";
                return false;
            }

            // A composite device exposes several HID interfaces; only some carry input
            // reports. Take the first that reports a non-zero input report length.
            foreach (var path in paths)
            {
                int len = InputReportLength(path);
                if (len <= 0) continue;

                var handle = CreateFile(path, GENERIC_READ,
                                        // Shared: a game must be able to keep using the
                                        // device while we watch it.
                                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                                        IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); continue; }

                lock (_lock)
                {
                    _gaps.Clear();
                    _lastReportMs = -1;
                    _lastArrivalMs = -1;
                }

                Unavailable = null;
                _cts = new CancellationTokenSource();
                _reader = Task.Run(() => ReadLoop(handle, len, _cts.Token));
                return true;
            }

            Unavailable = "sin reportes de entrada";
            return false;
        }

        public void Stop()
        {
            var cts = _cts;
            var reader = _reader;
            _cts = null;
            _reader = null;

            if (cts == null) return;
            try
            {
                cts.Cancel();
                // Bounded: never let closing a device hang the UI thread. The read loop
                // owns the handle and disposes it on its way out.
                reader?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex) { Debug.WriteLine($"PollingMeter.Stop: {ex.Message}"); }
            finally { cts.Dispose(); }
        }

        private void ReadLoop(SafeFileHandle handle, int reportLength, CancellationToken token)
        {
            try
            {
                using (handle)
                using (var stream = new FileStream(handle, FileAccess.Read, reportLength, isAsync: true))
                {
                    var buffer = new byte[reportLength];
                    while (!token.IsCancellationRequested)
                    {
                        int n = stream.ReadAsync(buffer, 0, reportLength, token).GetAwaiter().GetResult();
                        if (n <= 0) break;

                        double now = _clock.Elapsed.TotalMilliseconds;
                        lock (_lock)
                        {
                            if (_lastReportMs >= 0)
                            {
                                _gaps.Enqueue(now - _lastReportMs);
                                while (_gaps.Count > WindowSize) _gaps.Dequeue();
                            }
                            _lastReportMs = now;
                            _lastArrivalMs = now;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on Stop */ }
            catch (Exception ex)
            {
                // The device being unplugged mid-read lands here. It is a normal end,
                // not a crash: the meter simply has nothing left to measure.
                Debug.WriteLine($"PollingMeter read loop ended: {ex.Message}");
            }
        }

        // An immutable snapshot for the UI. Null means no reading - either nothing has
        // arrived yet, or the device has gone quiet. The caller must render that as
        // "no data" and never as a rate of zero.
        public RateSample? Snapshot(int quietMs = 2000)
        {
            lock (_lock)
            {
                if (_lastArrivalMs < 0) return null;

                // Stale window: reports stopped arriving. Keeping the last median on
                // screen would claim a rate the device is no longer achieving.
                if (_clock.Elapsed.TotalMilliseconds - _lastArrivalMs > quietMs) return null;

                return PollingCore.Summarise(_gaps.ToArray());
            }
        }

        // The most recent gaps, newest last, for the header spectrum. Empty when there
        // is nothing to show - the bars must then go flat and grey rather than inventing
        // motion, which is exactly what the deleted fake graph did.
        public double[] RecentGaps(int count)
        {
            lock (_lock)
            {
                if (_gaps.Count == 0) return Array.Empty<double>();
                var all = _gaps.ToArray();
                if (all.Length <= count) return all;
                var slice = new double[count];
                Array.Copy(all, all.Length - count, slice, 0, count);
                return slice;
            }
        }

        // Capabilities of a HID interface, or null when they cannot be read.
        internal static HIDP_CAPS? TryGetCaps(string path)
        {
            using var h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                     IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;

            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return null;
            try
            {
                // HIDP_STATUS_SUCCESS
                return HidP_GetCaps(pp, out HIDP_CAPS caps) == 0x110000 ? caps : (HIDP_CAPS?)null;
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        private static int InputReportLength(string path) => TryGetCaps(path)?.InputReportByteLength ?? 0;

        public void Dispose() => Stop();
    }
}
