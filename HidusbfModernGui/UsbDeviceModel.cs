using System;

namespace HidusbfModernGui
{
    public class UsbDeviceModel
    {
        public string Name { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string Class { get; set; } = "";
        public string Status { get; set; } = "";
        public bool IsConnected => Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
        public bool FilterActive { get; set; }
        public int? SelectedRate { get; set; } // 0 = Default, 31, 62, 125, 250, 500, 1000
        public string DriverKey { get; set; } = "";

        // 0 = Unknown, 1 = Low, 2 = Full, 3 = High, 4 = Super.
        // Defaults to Unknown: guessing Full Speed here is what silently turned a
        // 2000Hz request into 125Hz.
        public int Speed { get; set; } = 0;
        public UsbSpeed BusSpeed => Enum.IsDefined(typeof(UsbSpeed), Speed) ? (UsbSpeed)Speed : UsbSpeed.Unknown;
        public bool SpeedKnown => BusSpeed != UsbSpeed.Unknown;

        // The driver mode in force when this device was scanned. Slots 31 and 62
        // mean different rates depending on it, so the model needs to know.
        public DriverMode ActiveMode { get; set; } = DriverMode.NoPatch;

        public string ChildrenSummary { get; set; } = "";
        public bool HasChildren => !string.IsNullOrEmpty(ChildrenSummary);

        // Rate as the user should read it, resolved through the active driver mode
        // and this device's bus speed.
        public int? ResolvedRate
        {
            get
            {
                // hidusbf is not in this device's LowerFilters, so it is not
                // polling at whatever bInterval happens to be sitting in the
                // registry. Asserting that stale rate here would be the same
                // shape of lie the old "100%" ring told next to "Stopped".
                if (!FilterActive) return null;
                if (SelectedRate is null or 0) return null;
                int slot = SelectedRate.Value;
                if (slot == 31 || slot == 62) return PollingCore.ResolveHighRateSlot(slot, ActiveMode, BusSpeed);
                return slot;
            }
        }

        public string DisplayRate => ResolvedRate is null ? "Default" : $"{ResolvedRate} Hz";

        public string LatencyText => ResolvedRate is null
            ? "--"
            : $"{PollingCore.LatencyMs(ResolvedRate.Value):0.0##} ms";

        public string IntervalModeText => BusSpeed switch
        {
            UsbSpeed.Super => "SuperSpeed (Microframes)",
            UsbSpeed.High => "High-Speed (Microframes)",
            UsbSpeed.Full => "Full-Speed (Milliseconds)",
            UsbSpeed.Low => "Low-Speed (Milliseconds)",
            _ => "Unknown (rate changes blocked)"
        };

        // Colour of this row's status dot. Delegated to PollingCore so the rule
        // lives under test.
        //
        // Named StatusDot rather than Status: this class already has a string
        // Status property holding the raw PnP status text ("OK", "Error", ...)
        // that IsConnected depends on, and that property is contractually frozen
        // (consumed by SystemManager.ScanDevices). Renamed here to avoid the
        // collision instead of overloading/replacing the existing Status.
        public StatusLevel StatusDot => PollingCore.DeviceStatusLevel(FilterActive, BusSpeed, ResolvedRate);

        public string SpeedText => BusSpeed switch
        {
            UsbSpeed.Super => "SuperSpeed",
            UsbSpeed.High => "High Speed",
            UsbSpeed.Full => "Full Speed",
            UsbSpeed.Low => "Low Speed",
            _ => "Unknown"
        };

        // The raw register value, shown because this is an instrument: the user
        // should be able to check our arithmetic against the USB spec.
        public string BIntervalText
        {
            get
            {
                if (SelectedRate is null or 0) return "--";
                var b = PollingCore.TryMapRateToBInterval(SelectedRate.Value, BusSpeed);
                return b?.ToString() ?? "--";
            }
        }

        public string FilterText => FilterActive ? "ON" : "OFF";

        public string IconKind
        {
            get
            {
                var lowerClass = Class.ToLower();
                var lowerName = Name.ToLower();
                var lowerChildren = ChildrenSummary.ToLower();

                if (lowerClass == "mouse" || lowerName.Contains("mouse") || lowerChildren.Contains("mouse"))
                    return "Mouse";
                if (lowerClass == "keyboard" || lowerName.Contains("keyboard") || lowerChildren.Contains("keyboard") || lowerChildren.Contains("kbd"))
                    return "Keyboard";
                if (lowerName.Contains("controller") || lowerName.Contains("dualsense") || lowerName.Contains("gamepad") || lowerName.Contains("joystick") ||
                    lowerChildren.Contains("controller") || lowerChildren.Contains("dualsense") || lowerChildren.Contains("gamepad") || lowerChildren.Contains("joystick"))
                    return "Gamepad";

                return "Usb";
            }
        }
    }

    public class PnpDeviceRaw
    {
        public string FriendlyName { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string Status { get; set; } = "";
        public string Class { get; set; } = "";
        public int Speed { get; set; } = 0;
    }
}
