using HidusbfModernGui;

// Read-only probe: exercises the real SystemManager code path against the live
// system and prints what it finds. Writes nothing.

Console.WriteLine("=== DriverDir resolution ===");
Console.WriteLine($"Resolved : {SystemManager.DriverDir}");
Console.WriteLine($"Exists   : {Directory.Exists(SystemManager.DriverDir)}");
Console.WriteLine();

Console.WriteLine("=== GetDriverState() ===");
var state = SystemManager.GetDriverState();
Console.WriteLine($"ServiceStatus        : {state.ServiceStatus}");
Console.WriteLine($"Build (by hash)      : {state.Build}");
Console.WriteLine($"PatchUSBXHCI         : {(state.PatchUsbXhci?.ToString() ?? "(not set)")}");
Console.WriteLine($"PatchUSBPort         : {(state.PatchUsbPort?.ToString() ?? "(not set)")}");
Console.WriteLine($"EffectiveMode        : {(state.EffectiveMode?.ToString() ?? "(unknown)")}");
Console.WriteLine($"ModeText (shown UI)  : {state.ModeText}");
Console.WriteLine($"MemoryIntegrity      : {state.MemoryIntegrityEnabled}");
Console.WriteLine($"CanOverclock >1k     : {state.CanOverclockBeyond1k}");
Console.WriteLine($"Warning              : {state.Warning ?? "(none)"}");
Console.WriteLine();

Console.WriteLine("=== ScanDevices() ===");
var mode = state.EffectiveMode ?? DriverMode.NoPatch;
var devices = SystemManager.ScanDevices(mode);
Console.WriteLine($"{devices.Count} devices found\n");

foreach (var d in devices)
{
    Console.WriteLine($"  {d.Name}");
    Console.WriteLine($"    Speed={d.BusSpeed} (known={d.SpeedKnown})  Filter={d.FilterActive}");
    Console.WriteLine($"    Rate={d.DisplayRate}  Latency={d.LatencyText}  {d.IntervalModeText}");

    // What the rate dropdown would offer for this device.
    var offered = new[] { 31, 62, 125, 250, 500, 1000 }
        .Select(r => new { r, ok = PollingCore.TryMapRateToBInterval(r, d.BusSpeed) != null })
        .Select(x => x.ok ? $"{PollingCore.ResolveHighRateSlot(x.r, mode, d.BusSpeed) ?? x.r}" : $"{x.r}(blocked)");
    Console.WriteLine($"    Offers: {string.Join(", ", offered)}");
    Console.WriteLine();
}

// What the 31/62 slots mean if the driver were switched to 4kHz-8kHz.
//
// This comment used to assert the opposite, and was wrong. The slot
// reinterpretation is the Low/Full Speed smuggling channel: there bInterval is in
// milliseconds and cannot express anything below 1ms, so the patched driver reads
// 31/62 as 4000/8000. High and Super Speed reach those rates natively through
// bInterval 1/2/3 (125/250/500us), so their 31/62 slots stay literally 31 and 62 Hz.
Console.WriteLine("=== Simulated: driver in 4kHz-8kHz mode ===");
foreach (var d in devices.DistinctBy(x => x.BusSpeed))
{
    var slots = new[] { 31, 62 }
        .Select(s => $"slot {s} -> {PollingCore.ResolveHighRateSlot(s, DriverMode.Rate4k8k, d.BusSpeed)} Hz");
    Console.WriteLine($"  {d.BusSpeed,-6} Speed: {string.Join("   ", slots)}");
}
Console.WriteLine();

// The regression that started this: asking for 2000Hz on a Full Speed device.
Console.WriteLine("=== Guard rail: 2000 Hz requested on each speed ===");
foreach (var speed in new[] { UsbSpeed.High, UsbSpeed.Full, UsbSpeed.Low, UsbSpeed.Unknown })
{
    var b = PollingCore.TryMapRateToBInterval(2000, speed);
    Console.WriteLine($"  {speed,-8} -> {(b == null ? "REFUSED (was: silently wrote bInterval=8 = 125 Hz)" : $"bInterval={b}")}");
}
