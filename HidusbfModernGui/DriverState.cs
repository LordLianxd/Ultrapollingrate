using System;

namespace HidusbfModernGui
{
    // Which hidusbf binary is sitting in System32.
    public enum DriverBuild
    {
        Missing,    // no file at all
        NoPatch,    // matches a NOPATCH build - cannot patch the USB stack
        Patching,   // matches a 1kHz / 2kHz-4kHz / 4kHz-8kHz build
        Unrecognised // a file is there but it is not one we shipped
    }

    // The real state of the driver, derived from the installed binary and the
    // registry - never from what this app previously wrote.
    public class DriverState
    {
        public string ServiceStatus { get; set; } = "NotInstalled";
        public bool ServiceInstalled => !ServiceStatus.Equals("NotInstalled", StringComparison.OrdinalIgnoreCase);
        public DriverBuild Build { get; set; } = DriverBuild.Missing;
        public int? PatchUsbXhci { get; set; }
        public int? PatchUsbPort { get; set; }
        public DriverMode? EffectiveMode { get; set; }
        public bool MemoryIntegrityEnabled { get; set; }

        // What to show as the mode. Never invents a level we cannot prove.
        public string ModeText => Build switch
        {
            DriverBuild.Missing => "Not Installed",
            DriverBuild.Unrecognised => "Unrecognised driver",
            _ => EffectiveMode.HasValue ? PollingCore.DescribeMode(EffectiveMode.Value) : "Unknown (binary default)"
        };

        // Non-null when the reported state needs a caveat the user must see.
        public string? Warning { get; set; }

        // True when rates above 1000Hz are actually reachable right now.
        public bool CanOverclockBeyond1k =>
            EffectiveMode is DriverMode.Rate2k4k or DriverMode.Rate4k8k;

        // The one place that reports the driver being capped. Device rows must not
        // repeat it: whether a device does what was asked and whether the driver
        // can exceed 1000Hz are different facts.
        public StatusLevel HeaderStatus
        {
            get
            {
                // A .sys hashing to a Patching build proves nothing if hidusbf is
                // not loaded as a service: nothing is filtering anything. This is
                // the exact case left behind when UninstallService deletes the
                // service but the locked .sys survives until reboot.
                if (!ServiceInstalled) return StatusLevel.Error;
                if (Build is DriverBuild.Missing or DriverBuild.Unrecognised) return StatusLevel.Error;
                if (!EffectiveMode.HasValue) return StatusLevel.Error;
                if (MemoryIntegrityEnabled && Build == DriverBuild.Patching) return StatusLevel.Error;
                return CanOverclockBeyond1k ? StatusLevel.Ok : StatusLevel.Warn;
            }
        }

        public StatusLevel ServiceStatusLevel =>
            ServiceStatus.Equals("Running", StringComparison.OrdinalIgnoreCase)
                ? StatusLevel.Ok
                : StatusLevel.Error;
    }
}
