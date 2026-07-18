using System;
using System.Linq;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace HidusbfModernGui
{
    // Wraps the HidHide driver (Nefarius.Drivers.HidHide 3.4.0). Three moving parts:
    //   1. whitelist our own exe so WE keep seeing/reading the pad,
    //   2. block the DualSense's device instance so OTHER apps (the game) stop seeing it,
    //   3. flip HidHide's global "IsActive" flag so the blocking takes effect.
    // Revert undoes exactly what we added and only clears the global flag if we set it,
    // so we never trample another app's hiding and never strand the pad.
    //
    // Safety contract: this class must never leave the physical pad hidden. Every failure
    // path best-effort reverts, and IsHiding reads the REAL driver state (not a flag).
    //
    // Requires the app to run elevated (HidHide list writes need admin); the app.manifest
    // already requests requireAdministrator.
    public sealed class HidHideControl
    {
        private readonly HidHideControlService _svc = new HidHideControlService();

        private string? _blockedInstanceId;   // exactly what we added -> exactly what we remove
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
                    if (_blockedInstanceId == null) return false;
                    if (!_svc.IsActive) return false;
                    return _svc.BlockedInstanceIds.Any(id =>
                        string.Equals(id, _blockedInstanceId, StringComparison.OrdinalIgnoreCase));
                }
                catch { return false; }
            }
        }

        // Whitelist exePath, block the DualSense (given either as its HidSharp symbolic
        // link or as an instance id), then set the global hide flag. Caller ordering: the
        // virtual pad must already exist before this is called, so the game never sees
        // zero controllers.
        public OpResult HideDualSense(string exePath, string deviceSymbolicLinkOrInstanceId)
        {
            try
            {
                if (!_svc.IsInstalled)
                    return OpResult.Fail("HidHide no esta instalado o no esta operativo.");

                string instanceId = ResolveInstanceId(deviceSymbolicLinkOrInstanceId);
                if (string.IsNullOrWhiteSpace(instanceId))
                    return OpResult.Fail("No se pudo resolver el ID de instancia del DualSense.");

                // Whitelist our exe FIRST so we never lose our own read access the instant
                // the global flag goes on.
                if (!_svc.ApplicationPaths.Any(p => string.Equals(p, exePath, StringComparison.OrdinalIgnoreCase)))
                {
                    _svc.AddApplicationPath(exePath);
                    _whitelistedExe = exePath;   // remember only what we added, to revert cleanly
                }

                if (!_svc.BlockedInstanceIds.Any(id => string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase)))
                    _svc.AddBlockedInstanceId(instanceId);
                _blockedInstanceId = instanceId;

                if (!_svc.IsActive)
                {
                    _svc.IsActive = true;
                    _weActivated = true;
                }

                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                // A failure mid-hide must not strand the pad: best-effort un-hide.
                try { Revert(); } catch { }
                return OpResult.Fail($"HidHide fallo al ocultar el fisico: {ex.Message}");
            }
        }

        // Undo exactly what HideDualSense did: remove our device from the blocked list,
        // remove our exe from the whitelist, and clear the global flag only if WE set it.
        // The physical is visible again the instant its instance id leaves the block list.
        public OpResult Revert()
        {
            string? err = null;

            try
            {
                if (_blockedInstanceId != null &&
                    _svc.BlockedInstanceIds.Any(id => string.Equals(id, _blockedInstanceId, StringComparison.OrdinalIgnoreCase)))
                    _svc.RemoveBlockedInstanceId(_blockedInstanceId);
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

            _blockedInstanceId = null;
            _whitelistedExe = null;
            return err == null ? OpResult.Ok() : OpResult.Fail($"Revert parcial: {err}");
        }

        // Startup guard: if a previous run crashed with a DualSense hidden, unblock any
        // USB DualSense (VID_054C) instance still in the block list so the pad is never
        // stranded. Conservative: it only touches DualSense entries, never clears the
        // whole list, and never disables another app's global hiding.
        public OpResult ShowAllDualSense()
        {
            try
            {
                if (!_svc.IsInstalled) return OpResult.Ok();
                foreach (var id in _svc.BlockedInstanceIds.ToList())
                {
                    if (id != null && id.IndexOf("VID_054C", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { _svc.RemoveBlockedInstanceId(id); } catch { }
                    }
                }
                _blockedInstanceId = null;
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
    }
}
