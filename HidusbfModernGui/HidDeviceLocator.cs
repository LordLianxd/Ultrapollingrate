using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HidusbfModernGui
{
    // Maps a USB device instance ID - what the device list already holds - to the HID
    // interface paths underneath it. No VID/PID hardcoding: it walks the device tree.
    //
    // Verified against the real DualSense:
    //   USB\VID_054C&PID_0CE6\6&227ba791&0&4
    //    +- MI_00 (audio) -> SWD\MMDEVAPI...          no HID
    //    +- MI_03         -> HID\VID_054C&PID_0CE6... 1 HID interface
    // A composite device's functions are not all measurable; only the ones that
    // actually expose HID are.
    public static class HidDeviceLocator
    {
        private const int CR_SUCCESS = 0;
        private const uint CM_GETIDLIST_FILTER_NONE = 0;

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNode(out uint dev, string id, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Child(out uint child, uint dev, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Sibling(out uint sibling, uint dev, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID(uint dev, StringBuilder buffer, int len, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_Size(out int len, ref Guid cls, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List(ref Guid cls, string deviceId, char[] buffer, int len, uint flags);

        // Every HID interface path reachable from this USB device. An empty list means
        // the device has no HID interface at all - a webcam, an audio endpoint - which
        // is "not measurable", not a failure.
        public static List<string> FindHidPaths(string usbInstanceId)
        {
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(usbInstanceId)) return paths;

            try
            {
                HidD_GetHidGuid(out Guid hidGuid);

                if (CM_Locate_DevNode(out uint dev, usbInstanceId, 0) != CR_SUCCESS) return paths;

                // El propio nodo primero: si el id ya ES el nodo HID (la ruta en-proceso de las luces
                // con HidHide activo le pasa HID\VID_054C&PID_0CE6...), su interfaz esta aqui, no en
                // un hijo. Para un id USB compuesto (el camino clasico) esto no aporta nada y no rompe.
                paths.AddRange(InterfacesFor(usbInstanceId, hidGuid));

                if (CM_Get_Child(out uint child, dev, 0) != CR_SUCCESS) return paths;

                var descendants = new List<string>();
                Walk(child, descendants);

                foreach (var id in descendants)
                    paths.AddRange(InterfacesFor(id, hidGuid));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error locating HID paths for {usbInstanceId}: {ex.Message}");
            }

            return paths;
        }

        private static void Walk(uint dev, List<string> ids)
        {
            var sb = new StringBuilder(400);
            if (CM_Get_Device_ID(dev, sb, sb.Capacity, 0) == CR_SUCCESS) ids.Add(sb.ToString());
            if (CM_Get_Child(out uint c, dev, 0) == CR_SUCCESS) Walk(c, ids);
            if (CM_Get_Sibling(out uint s, dev, 0) == CR_SUCCESS) Walk(s, ids);
        }

        private static List<string> InterfacesFor(string deviceId, Guid cls)
        {
            var result = new List<string>();

            // Size 0 or 1 means the list is just its terminating null: no interfaces.
            if (CM_Get_Device_Interface_List_Size(out int len, ref cls, deviceId, CM_GETIDLIST_FILTER_NONE) != CR_SUCCESS || len < 2)
                return result;

            var buffer = new char[len];
            if (CM_Get_Device_Interface_List(ref cls, deviceId, buffer, len, CM_GETIDLIST_FILTER_NONE) != CR_SUCCESS)
                return result;

            foreach (var s in new string(buffer).Split('\0'))
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);

            return result;
        }
    }
}
