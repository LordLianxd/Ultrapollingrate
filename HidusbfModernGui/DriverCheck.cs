using System;
using System.ServiceProcess;

namespace HidusbfModernGui
{
    // Whether the two drivers the remapper engine depends on are installed:
    // ViGEmBus (creates the virtual DS4) and HidHide (hides the physical pad from
    // other apps). Both ship as Windows services/drivers registered with the SCM,
    // exactly like hidusbf - so this reuses the same ServiceController probe
    // SystemManager.GetServiceStatus() already uses for hidusbf, rather than
    // reaching into the Nefarius client libraries.
    //
    // Deliberately side-effect free: this must never create a ViGEmClient (that
    // throws its own "bus not found" exception, which would work too, but also
    // spins up a connection this caller never asked for) or touch HidHide's
    // device list. It only asks the SCM whether the service is registered.
    public static class DriverCheck
    {
        private const string ViGEmBusServiceName = "ViGEmBus";
        private const string HidHideServiceName = "HidHide";

        public static (bool vigem, bool hidhide) Detect()
        {
            return (IsServiceInstalled(ViGEmBusServiceName), IsServiceInstalled(HidHideServiceName));
        }

        // Presence, not running state: both are drivers the engine starts/stops on
        // its own (ViGEmBus is demand-started per virtual pad; HidHide is toggled
        // by RemapEngine), so "registered with the SCM" is what "installed" means
        // here - never "currently running".
        private static bool IsServiceInstalled(string serviceName)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                _ = sc.Status;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Exception)
            {
                // Never let a detection helper throw and take the caller down with
                // it - an unreadable service state is not proof of absence, but it
                // is not proof of presence either, so it is reported as absent.
                return false;
            }
        }
    }
}
