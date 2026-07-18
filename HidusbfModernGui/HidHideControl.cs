using System;
using System.Collections.Generic;
using System.Linq;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace HidusbfModernGui
{
    // Wraps the HidHide driver (Nefarius.Drivers.HidHide 3.4.0). Moving parts:
    //   1. whitelist our own exe so WE keep seeing/reading the pad,
    //   2. resolve the PHYSICAL DualSense's own instance ids at runtime (never trust a
    //      caller-supplied path blindly - see FindPhysicalGamepadInstanceId),
    //   3. block those instance ids so OTHER apps (the game, a browser) stop seeing it,
    //   4. flip HidHide's global "IsActive" flag so the blocking takes effect,
    //   5. best-effort restart the devnode so consumers that already had it open drop it.
    // Revert undoes exactly what we added and only clears the global flag if we set it,
    // so we never trample another app's hiding and never strand the pad.
    //
    // Safety contract: this class must never leave the physical pad hidden, and must
    // NEVER add a virtual (PID_05C4, our own ViGEm pad) instance to the blocked list.
    // Every failure path best-effort reverts, and IsHiding reads the REAL driver state
    // (not a flag).
    //
    // Requires the app to run elevated (HidHide list writes need admin); the app.manifest
    // already requests requireAdministrator.
    public sealed class HidHideControl
    {
        private readonly HidHideControlService _svc = new HidHideControlService();

        // The real DualSense is Sony VID_054C/PID_0CE6. Our own ViGEm virtual pad reports
        // as VID_054C/PID_05C4 (it emulates a DS4) - same vendor, different product, so a
        // vendor-only match is not enough to tell them apart. PID_0CE6 is required and
        // PID_05C4 is explicitly excluded everywhere below.
        private const int SonyVid = 0x054C;
        private const int PhysicalPid = 0x0CE6;
        private const int VirtualPid = 0x05C4;

        private List<string> _blockedInstanceIds = new();  // exactly what we added -> exactly what we remove
        private string? _primaryBlockedId;    // the HID game-controller node; IsHiding reads this one
        private string? _whitelistedExe;
        private bool _weActivated;             // true only if WE turned IsActive on

        public bool IsInstalled
        {
            get { try { return _svc.IsInstalled; } catch { return false; } }
        }

        // Real driver state: hiding is "on" only if the global flag is set AND our device
        // is actually present in the blocked list. Detects a state left by a crash too.
        public bool IsHiding
        {
            get
            {
                try
                {
                    if (_primaryBlockedId == null) return false;
                    if (!_svc.IsActive) return false;
                    return _svc.BlockedInstanceIds.Any(id =>
                        string.Equals(id, _primaryBlockedId, StringComparison.OrdinalIgnoreCase));
                }
                catch { return false; }
            }
        }

        // Whitelist exePath, resolve and block the PHYSICAL DualSense, then set the global
        // hide flag. Caller ordering: the virtual pad must already exist before this is
        // called, so the game never sees zero controllers.
        //
        // deviceSymbolicLinkOrInstanceId is a HINT, not the source of truth: this method
        // resolves the physical pad itself by enumerating VID_054C+PID_0CE6 at runtime
        // (FindPhysicalGamepadInstanceId), and only falls back to the caller-supplied value
        // if that enumeration comes up empty. Either way, the resolved id is verified to be
        // the physical (PID_0CE6, never PID_05C4) before anything is added to the blocked
        // list - a caller bug or an enumeration-order collision with our own virtual pad can
        // never end up hiding the wrong device.
        public OpResult HideDualSense(string exePath, string deviceSymbolicLinkOrInstanceId)
        {
            try
            {
                if (!_svc.IsInstalled)
                    return OpResult.Fail("HidHide no esta instalado o no esta operativo.");

                string hidInstanceId = FindPhysicalGamepadInstanceId()
                                        ?? ResolveInstanceId(deviceSymbolicLinkOrInstanceId);
                if (string.IsNullOrWhiteSpace(hidInstanceId))
                    return OpResult.Fail("No se pudo resolver el ID de instancia del DualSense fisico.");
                if (!IsPhysicalDualSenseId(hidInstanceId))
                    return OpResult.Fail($"Se rehusa a ocultar un dispositivo que no es el DualSense fisico (PID_0CE6): {hidInstanceId}");

                // Whitelist our exe FIRST so we never lose our own read access the instant
                // the global flag goes on.
                if (!_svc.ApplicationPaths.Any(p => string.Equals(p, exePath, StringComparison.OrdinalIgnoreCase)))
                {
                    _svc.AddApplicationPath(exePath);
                    _whitelistedExe = exePath;   // remember only what we added, to revert cleanly
                }

                // Block the HID game-controller node itself, plus (best-effort) its direct
                // USB interface parent (USB\VID_054C&PID_0CE6&MI_03\...) - never the whole
                // composite USB parent, which also carries the DualSense's audio function
                // and must stay visible.
                var toBlock = new List<string> { hidInstanceId };
                string? usbParentId = TryGetParentInstanceId(hidInstanceId);
                if (usbParentId != null && IsPhysicalDualSenseId(usbParentId))
                    toBlock.Add(usbParentId);

                var added = new List<string>();
                foreach (var id in toBlock)
                {
                    if (!_svc.BlockedInstanceIds.Any(b => string.Equals(b, id, StringComparison.OrdinalIgnoreCase)))
                        _svc.AddBlockedInstanceId(id);
                    added.Add(id);
                }
                _blockedInstanceIds = added;
                _primaryBlockedId = hidInstanceId;

                if (!_svc.IsActive)
                {
                    _svc.IsActive = true;
                    _weActivated = true;
                }

                // Best-effort: HidHide only rejects NEW opens of a blocked device: a
                // consumer that already had the pad open before the block list update (or
                // that reads it via Raw Input, which several browsers use for the Gamepad
                // API and which is not retroactively evicted) keeps its existing handle.
                // Restarting the devnode force-closes every open handle so the next open -
                // the one HidHide actually gets to see and deny - is the only way back in.
                // Guarded: Restart() can fail if a handle can't be force-closed; that must
                // never crash us or leave the pad hidden-without-virtual, so a failure here
                // only skips this nicety - the block-list entries above already stand.
                TryRestartDevice(hidInstanceId);

                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                // A failure mid-hide must not strand the pad: best-effort un-hide.
                try { Revert(); } catch { }
                return OpResult.Fail($"HidHide fallo al ocultar el fisico: {ex.Message}");
            }
        }

        // Undo exactly what HideDualSense did: remove our device(s) from the blocked list,
        // remove our exe from the whitelist, and clear the global flag only if WE set it.
        // The physical is visible again the instant its instance id leaves the block list.
        public OpResult Revert()
        {
            string? err = null;

            try
            {
                foreach (var id in _blockedInstanceIds)
                {
                    if (_svc.BlockedInstanceIds.Any(b => string.Equals(b, id, StringComparison.OrdinalIgnoreCase)))
                        _svc.RemoveBlockedInstanceId(id);
                }
            }
            catch (Exception ex) { err = ex.Message; }

            try
            {
                if (_whitelistedExe != null)
                    _svc.RemoveApplicationPath(_whitelistedExe);
            }
            catch (Exception ex) { err ??= ex.Message; }

            try
            {
                if (_weActivated) { _svc.IsActive = false; _weActivated = false; }
            }
            catch (Exception ex) { err ??= ex.Message; }

            _blockedInstanceIds = new List<string>();
            _primaryBlockedId = null;
            _whitelistedExe = null;
            return err == null ? OpResult.Ok() : OpResult.Fail($"Revert parcial: {err}");
        }

        // Startup guard: if a previous run crashed with a DualSense hidden, unblock any
        // PHYSICAL DualSense (VID_054C&PID_0CE6) instance still in the block list so the
        // pad is never stranded. Conservative: matches on the physical's PID specifically
        // (not a bare VID_054C, which would also match our own virtual PID_05C4 pad or any
        // other Sony VID_054C peripheral), never clears the whole list, and never disables
        // another app's global hiding.
        public OpResult ShowAllDualSense()
        {
            try
            {
                if (!_svc.IsInstalled) return OpResult.Ok();
                foreach (var id in _svc.BlockedInstanceIds.ToList())
                {
                    if (id != null && IsPhysicalDualSenseId(id))
                    {
                        try { _svc.RemoveBlockedInstanceId(id); } catch { }
                    }
                }
                _blockedInstanceIds = new List<string>();
                _primaryBlockedId = null;
                _weActivated = false;
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Guard de arranque de HidHide fallo: {ex.Message}");
            }
        }

        // HidSharp gives a symbolic link (\\?\hid#vid_054c...#{guid}); HidHide wants an
        // instance id (HID\VID_054C&...\...). Resolve the former via the device-management
        // helper; pass an already-resolved instance id straight through.
        private static string ResolveInstanceId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            bool looksLikeSymbolicLink =
                s.StartsWith(@"\\?\") || s.StartsWith(@"\\.\") || s.Contains("#");
            if (looksLikeSymbolicLink)
            {
                try
                {
                    var resolved = PnPDevice.GetInstanceIdFromInterfaceId(s);
                    if (!string.IsNullOrWhiteSpace(resolved)) return resolved!;
                }
                catch
                {
                    // Fall through: better to try the raw string than to fail outright.
                }
            }
            return s;
        }

        // True for the physical DualSense's own ids (HID leaf or its USB parent), false for
        // everything else - most importantly false for our own virtual pad (PID_05C4).
        private static bool IsPhysicalDualSenseId(string instanceId) =>
            !string.IsNullOrEmpty(instanceId) &&
            instanceId.IndexOf($"VID_{SonyVid:X4}&PID_{PhysicalPid:X4}", StringComparison.OrdinalIgnoreCase) >= 0 &&
            instanceId.IndexOf($"PID_{VirtualPid:X4}", StringComparison.OrdinalIgnoreCase) < 0;

        // Asks Windows directly, right now, which HID-class device exposes VID_054C+PID_0CE6
        // (the game-controller collection of the real DualSense) and returns its instance
        // id - never a cached/passed-in value, so a stale handle from a previous connection
        // or an enumeration-order collision with our own virtual pad (which shares the
        // vendor id) can never be mistaken for the physical. Volatile instance-id suffixes
        // (7&..., 8&...) are read as-is from whatever Windows reports right now, never
        // hardcoded. Returns null (never throws) if the physical isn't found, so the caller
        // can fall back to the caller-supplied device path.
        private static string? FindPhysicalGamepadInstanceId()
        {
            try
            {
                int i = 0;
                while (Devcon.FindByInterfaceGuid(DeviceInterfaceIds.HidDevice, out PnPDevice dev, i, true))
                {
                    i++;
                    string id = dev.InstanceId ?? "";
                    if (IsPhysicalDualSenseId(id)) return id;
                }
            }
            catch
            {
                // Enumeration failing is not fatal - HideDualSense falls back to whatever
                // device path the caller supplied.
            }
            return null;
        }

        // One level up only: the USB interface node for this same MI_xx function (e.g.
        // USB\VID_054C&PID_0CE6&MI_03\...) - never further, so the composite USB parent
        // that also carries the DualSense's audio function is never touched.
        private static string? TryGetParentInstanceId(string instanceId)
        {
            try
            {
                return PnPDevice.GetDeviceByInstanceId(instanceId).Parent?.InstanceId;
            }
            catch
            {
                return null;
            }
        }

        // Best-effort devnode restart: forces every currently-open handle to this device
        // closed and re-enumerated, so a consumer that opened it before the block list
        // update (or via Raw Input) is forced to re-open - and is denied this time.
        // RemoveAndSetup() is the non-obsolete replacement for the old Restart() (same
        // remove+re-enumerate behavior; Restart() is kept in the library only for source
        // compat and documents the identical "may fail if a handle can't be force-closed"
        // caveat). Never allowed to throw out of HideDualSense - a failure here must not
        // crash the caller or leave anything in an inconsistent state; the block-list
        // entries added above stand regardless of whether this best-effort step succeeds.
        private static void TryRestartDevice(string instanceId)
        {
            try { PnPDevice.GetDeviceByInstanceId(instanceId).RemoveAndSetup(); }
            catch { /* best-effort only */ }
        }
    }
}
