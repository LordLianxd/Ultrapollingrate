using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace HidusbfModernGui
{
    public static class SystemManager
    {
        private const string DriverPath = @"C:\Windows\System32\drivers\hidusbf.sys";
        private const string ServiceName = "hidusbf";
        private const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\hidusbf\Parameters";

        private static string? _driverDir;
        public static string DriverDir => _driverDir ??= ResolveDriverDir();

        // Walk up from the executable looking for the DRIVER folder. The old code
        // hardcoded BaseDirectory\..\DRIVER, which resolves outside the repo when the
        // exe sits at the root and one level too shallow when run from bin\Debug.
        private static string ResolveDriverDir()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "DRIVER");
                if (Directory.Exists(candidate) && FindArchDir(candidate) != null)
                    return candidate;
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DRIVER");
        }

        // Attestation-signed builds (_AS) are required from Windows 8.1 on; the plain
        // folders are for older systems. Resolved case-insensitively by enumeration
        // because the shipped folder casing is inconsistent (NoPatch vs nopatch).
        private static string? FindArchDir(string driverRoot)
        {
            if (!Directory.Exists(driverRoot)) return null;

            bool x64 = RuntimeInformation.OSArchitecture == Architecture.X64;
            bool modern = Environment.OSVersion.Version >= new Version(6, 3);

            var order = x64
                ? (modern ? new[] { "AMD64_AS", "AMD64" } : new[] { "AMD64", "AMD64_AS" })
                : (modern ? new[] { "NTx86_AS", "NTX86" } : new[] { "NTX86", "NTx86_AS" });

            foreach (var name in order)
            {
                var hit = FindSubDir(driverRoot, name);
                if (hit != null && File.Exists(Path.Combine(hit, "hidusbf.sys"))) return hit;
            }
            return null;
        }

        private static string? FindSubDir(string parent, string name)
        {
            if (!Directory.Exists(parent)) return null;
            return Directory.GetDirectories(parent)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        }

        // Source .sys for a mode. Note the shipped 1kHz and 4kHz-8kHz AMD64_AS builds
        // are byte-identical: per README.ENG.TXT (2025/11/05) the builds now differ
        // only by their default PatchUSBPort value, and we always write the patch
        // parameters explicitly, so which patching build we install does not decide
        // the mode. The registry does.
        private static string? GetSourceSys(DriverMode mode)
        {
            var arch = FindArchDir(DriverDir);
            if (arch == null) return null;

            string folder = mode switch
            {
                DriverMode.NoPatch => "NOPATCH",
                DriverMode.Rate1k => "1kHz",
                DriverMode.Rate2k4k => "2kHz-4kHz",
                DriverMode.Rate4k8k => "4kHz-8kHz",
                _ => "NOPATCH"
            };

            var sub = FindSubDir(arch, folder);
            if (sub != null)
            {
                var sys = Path.Combine(sub, "hidusbf.sys");
                if (File.Exists(sys)) return sys;
            }
            return null;
        }

        private static string? HashFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error hashing {path}: {ex.Message}");
                return null;
            }
        }

        // Identify the installed driver by content, not by what we last wrote.
        private static DriverBuild IdentifyInstalledBuild()
        {
            if (!File.Exists(DriverPath)) return DriverBuild.Missing;

            string? installed = HashFile(DriverPath);
            if (installed == null) return DriverBuild.Unrecognised;

            var noPatch = GetSourceSys(DriverMode.NoPatch);
            if (noPatch != null && HashFile(noPatch) == installed) return DriverBuild.NoPatch;

            foreach (var mode in new[] { DriverMode.Rate1k, DriverMode.Rate2k4k, DriverMode.Rate4k8k })
            {
                var src = GetSourceSys(mode);
                if (src != null && HashFile(src) == installed) return DriverBuild.Patching;
            }

            return DriverBuild.Unrecognised;
        }

        // Configured state of Memory Integrity (HVCI). README.ENG.TXT is explicit that
        // it must be off to load the patching builds on recent Windows 10/11 x64.
        public static bool IsMemoryIntegrityEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                if (key?.GetValue("Enabled") is int enabled) return enabled != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading Memory Integrity state: {ex.Message}");
            }
            return false;
        }

        public static DriverState GetDriverState()
        {
            var state = new DriverState
            {
                ServiceStatus = GetServiceStatus(),
                Build = IdentifyInstalledBuild(),
                MemoryIntegrityEnabled = IsMemoryIntegrityEnabled()
            };

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ParametersKey);
                if (key != null)
                {
                    if (key.GetValue("PatchUSBXHCI") is int xhci) state.PatchUsbXhci = xhci;
                    if (key.GetValue("PatchUSBPort") is int port) state.PatchUsbPort = port;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading hidusbf parameters: {ex.Message}");
            }

            state.EffectiveMode = state.Build switch
            {
                DriverBuild.Missing or DriverBuild.Unrecognised => null,
                _ => PollingCore.ResolveEffectiveMode(state.Build == DriverBuild.Patching, state.PatchUsbXhci)
            };

            state.Warning = BuildWarning(state);
            return state;
        }

        private static string? BuildWarning(DriverState state)
        {
            if (FindArchDir(DriverDir) == null)
                return $"DRIVER folder not found (looked under {DriverDir}). Install and mode changes are unavailable.";

            if (state.Build == DriverBuild.Missing)
                return "hidusbf.sys is not installed. Install the service to enable overclocking.";

            if (state.Build == DriverBuild.Unrecognised)
                return "The installed hidusbf.sys does not match any shipped build. Reinstall to get a known state.";

            // The case found on this machine: NoPatch installed while the registry
            // asked for a patched mode. The binary wins and rates stay capped.
            if (state.Build == DriverBuild.NoPatch && state.PatchUsbXhci is > 0)
                return "A NoPatch build is installed, so PatchUSBXHCI is ignored and rates above 1000Hz are unreachable. Switch mode to reinstall a patching build.";

            if (state.Build == DriverBuild.NoPatch)
                return "NoPatch build installed: downclocking and up to 1000Hz on High Speed devices work, but 2k-8k needs a patching build.";

            if (state.Build == DriverBuild.Patching && state.MemoryIntegrityEnabled)
                return "Memory Integrity is enabled. The patching driver will fail to load until it is turned off in Windows Security > Core isolation.";

            if (state.Build == DriverBuild.Patching && state.PatchUsbXhci == null)
                return "PatchUSBXHCI is not set, so the driver is using its built-in default. Pick a mode to set it explicitly.";

            return null;
        }

        // Scan connected USB and input devices
        public static List<UsbDeviceModel> ScanDevices(DriverMode activeMode)
        {
            var result = new List<UsbDeviceModel>();
            try
            {
                // Query present parent PNP USB devices and their bus speed
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\\*' -and $_.InstanceId -notlike '*&MI_*' -and $_.Service -notin @('USBHUB3', 'usbhub', 'hubmsp', 'pci', 'usbxhci', 'ROOT_HUB30') } | ForEach-Object { $speedProp = Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName '{3464F7A4-2444-40B1-980A-E0903CB6D912} 10' -ErrorAction SilentlyContinue; [PSCustomObject]@{ FriendlyName = $_.FriendlyName; InstanceId = $_.InstanceId; Status = $_.Status; Class = $_.Class; Speed = if ($speedProp -and $null -ne $speedProp.Data) { [int]$speedProp.Data } else { 0 } } } | ConvertTo-Json -Compress\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return result;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (string.IsNullOrWhiteSpace(output)) return result;

                // Handle single object vs array in json
                List<PnpDeviceRaw> rawDevices;
                if (output.Trim().StartsWith("["))
                {
                    rawDevices = JsonSerializer.Deserialize<List<PnpDeviceRaw>>(output) ?? new List<PnpDeviceRaw>();
                }
                else
                {
                    var single = JsonSerializer.Deserialize<PnpDeviceRaw>(output);
                    rawDevices = single != null ? new List<PnpDeviceRaw> { single } : new List<PnpDeviceRaw>();
                }

                foreach (var raw in rawDevices)
                {
                    if (string.IsNullOrEmpty(raw.InstanceId)) continue;

                    var model = new UsbDeviceModel
                    {
                        Name = string.IsNullOrEmpty(raw.FriendlyName) ? "Unknown Device" : raw.FriendlyName,
                        InstanceId = raw.InstanceId,
                        Class = raw.Class,
                        Status = raw.Status,
                        Speed = raw.Speed,
                        ActiveMode = activeMode
                    };

                    // Resolve child devices recursively using memory-based PInvokes
                    var childIds = GetDescendantInstanceIds(raw.InstanceId);
                    var childNamesList = new List<string>();
                    foreach (var childId in childIds)
                    {
                        var name = GetDeviceNameFromRegistry(childId);
                        if (!string.IsNullOrEmpty(name) &&
                            name != "USB Input Device" &&
                            name != "USB Composite Device" &&
                            !name.Equals("HID-compliant device", StringComparison.OrdinalIgnoreCase))
                        {
                            childNamesList.Add(name);
                        }
                    }
                    model.ChildrenSummary = string.Join("; ", childNamesList.Distinct());

                    // Check registry for LowerFilters
                    try
                    {
                        using var deviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{raw.InstanceId}");
                        if (deviceKey != null)
                        {
                            var lowerFilters = deviceKey.GetValue("LowerFilters") as string[];
                            model.FilterActive = lowerFilters != null && lowerFilters.Contains("hidusbf", StringComparer.OrdinalIgnoreCase);

                            var driver = deviceKey.GetValue("Driver") as string;
                            if (!string.IsNullOrEmpty(driver))
                            {
                                model.DriverKey = driver;
                                using var classKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}");
                                if (classKey != null)
                                {
                                    var bIntervalVal = classKey.GetValue("bInterval");
                                    if (bIntervalVal != null)
                                    {
                                        int bInterval = Convert.ToInt32(bIntervalVal);
                                        model.SelectedRate = PollingCore.TryMapBIntervalToRate(bInterval, model.BusSpeed);
                                    }
                                    else
                                    {
                                        model.SelectedRate = 0; // Default
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error reading registry for device {raw.InstanceId}: {ex.Message}");
                    }

                    result.Add(model);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error scanning devices: {ex.Message}");
            }
            return result;
        }

        // Toggle filter on device
        public static OpResult SetFilterActive(string instanceId, bool active)
        {
            try
            {
                using var deviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}", true);
                if (deviceKey == null)
                    return OpResult.Fail($"Device key not found for {instanceId}. Administrator rights are required.");

                var lowerFilters = deviceKey.GetValue("LowerFilters") as string[] ?? Array.Empty<string>();
                var filtersList = lowerFilters.ToList();

                if (active)
                {
                    if (!filtersList.Contains("hidusbf", StringComparer.OrdinalIgnoreCase))
                        filtersList.Add("hidusbf");
                }
                else
                {
                    filtersList.RemoveAll(f => f.Equals("hidusbf", StringComparison.OrdinalIgnoreCase));
                }

                if (filtersList.Count > 0)
                    deviceKey.SetValue("LowerFilters", filtersList.ToArray(), RegistryValueKind.MultiString);
                else
                    deviceKey.DeleteValue("LowerFilters", false);

                return OpResult.Ok();
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail("Access denied writing LowerFilters. Run as Administrator.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Failed to write filter: {ex.Message}");
            }
        }

        // Set interval rate on the device's driver class key.
        // Refuses rather than guessing when the rate is not reachable at this speed.
        public static OpResult SetDeviceRate(string instanceId, string driverKey, int rate, UsbSpeed speed)
        {
            if (rate != 0 && speed == UsbSpeed.Unknown)
                return OpResult.Fail("USB bus speed could not be determined for this device, so the interval cannot be computed safely. Reconnect the device and rescan.");

            int? bInterval = null;
            if (rate != 0)
            {
                bInterval = PollingCore.TryMapRateToBInterval(rate, speed);
                if (bInterval == null)
                {
                    return OpResult.Fail(
                        $"{rate} Hz is not representable on a {speed} Speed device. " +
                        (PollingCore.UsesMicroframes(speed)
                            ? "Pick one of the supported rates."
                            : "Rates above 1000 Hz require a High Speed device on an xHCI controller."));
                }
            }

            try
            {
                string classPath = driverKey;
                if (string.IsNullOrEmpty(classPath))
                {
                    using var deviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
                    if (deviceKey == null) return OpResult.Fail($"Device key not found for {instanceId}.");
                    classPath = deviceKey.GetValue("Driver") as string ?? "";
                }

                if (string.IsNullOrEmpty(classPath))
                    return OpResult.Fail("This device has no driver class key, so no interval can be stored.");

                using var classKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{classPath}", true);
                if (classKey == null)
                    return OpResult.Fail($"Class key {classPath} could not be opened. Run as Administrator.");

                if (bInterval == null)
                    classKey.DeleteValue("bInterval", false);
                else
                    classKey.SetValue("bInterval", bInterval.Value, RegistryValueKind.DWord);

                return OpResult.Ok();
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail("Access denied writing bInterval. Run as Administrator.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Failed to write bInterval: {ex.Message}");
            }
        }

        // Restart device programmatically via PowerShell PnpDevice cmdlets.
        // Exit code alone is not enough: the cmdlets can report a non-terminating
        // error and still exit 0, so failures are promoted to terminating.
        public static async Task<OpResult> RestartDevice(string instanceId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string script =
                        "$ErrorActionPreference='Stop'; " +
                        $"try {{ Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false; " +
                        "Start-Sleep -Milliseconds 600; " +
                        $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false; exit 0 }} " +
                        "catch { Write-Error $_.Exception.Message; exit 1 }";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -Command \"{script}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var process = Process.Start(psi);
                    if (process == null) return OpResult.Fail("Could not start PowerShell to restart the device.");

                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    return process.ExitCode == 0
                        ? OpResult.Ok()
                        : OpResult.Fail(string.IsNullOrWhiteSpace(stderr)
                            ? "The device refused to restart. Unplug and replug it."
                            : stderr.Trim());
                }
                catch (Exception ex)
                {
                    return OpResult.Fail($"Failed to restart device: {ex.Message}");
                }
            });
        }

        // Get global service status
        public static string GetServiceStatus()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                return sc.Status.ToString();
            }
            catch (InvalidOperationException)
            {
                return "NotInstalled";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // Control the service (Start / Stop)
        public static OpResult ControlService(string action)
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (action.Equals("start", StringComparison.OrdinalIgnoreCase))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                    }
                }
                else if (action.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                    }
                }
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Failed to {action} the hidusbf service: {ex.Message}");
            }
        }

        private static bool ServiceExists()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                _ = sc.Status;
                return true;
            }
            catch { return false; }
        }

        // Install the service, staging the .sys for the requested mode.
        public static OpResult InstallService(DriverMode mode = DriverMode.Rate1k)
        {
            var source = GetSourceSys(mode);
            if (source == null)
                return OpResult.Fail($"No hidusbf.sys found for {PollingCore.DescribeMode(mode)} under {DriverDir}.");

            var copy = CopyDriverFile(source);
            if (!copy.Success) return copy;

            if (!ServiceExists())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"create {ServiceName} binPath= System32\\drivers\\hidusbf.sys type= kernel start= demand DisplayName= \"USB Mouse Rate Adjuster Lower Filter by SweetLow\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null) return OpResult.Fail("Could not run sc.exe to create the service.");
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                    return OpResult.Fail($"sc create failed (exit {process.ExitCode}): {(stdout + stderr).Trim()}");
            }

            var parameters = WritePatchParameters(mode);
            if (!parameters.Success) return parameters;

            ControlService("start");
            return OpResult.Ok();
        }

        public static OpResult UninstallService()
        {
            ControlService("stop");

            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"delete {ServiceName}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return OpResult.Fail("Could not run sc.exe to delete the service.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 && ServiceExists())
                return OpResult.Fail($"sc delete failed (exit {process.ExitCode}): {(stdout + stderr).Trim()}");

            if (File.Exists(DriverPath))
            {
                try { File.Delete(DriverPath); }
                catch (Exception ex)
                {
                    return OpResult.Fail($"Service removed, but hidusbf.sys is still in use and could not be deleted ({ex.Message}). It will go away after a reboot.");
                }
            }

            return OpResult.Ok();
        }

        // Always refreshes the file, unlike the old code which skipped the copy when
        // any hidusbf.sys already existed and so pinned a stale build forever.
        private static OpResult CopyDriverFile(string source)
        {
            try
            {
                File.Copy(source, DriverPath, true);
                return OpResult.Ok();
            }
            catch (IOException ex)
            {
                // Windows locks a loaded kernel driver's file, and hidusbf stays loaded
                // for as long as any device keeps it as a lower filter. Stopping the
                // service is not enough on its own.
                return OpResult.Fail(
                    $"hidusbf.sys is in use and cannot be replaced ({ex.Message}). " +
                    "It stays loaded while any device has it as a lower filter. Turn FILTRO off on every " +
                    "filtered device and restart them, then try again - or just reboot, which always works.");
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail("Access denied copying hidusbf.sys to System32. Run as Administrator.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Failed to copy hidusbf.sys: {ex.Message}");
            }
        }

        // Writes both patch parameters explicitly, so the driver never falls back to
        // the default baked into whichever build happens to be installed.
        private static OpResult WritePatchParameters(DriverMode mode)
        {
            var (xhci, port) = PollingCore.GetPatchParams(mode);
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(ParametersKey);
                if (key == null) return OpResult.Fail("Could not create the hidusbf Parameters key. Run as Administrator.");
                key.SetValue("PatchUSBXHCI", xhci, RegistryValueKind.DWord);
                key.SetValue("PatchUSBPort", port, RegistryValueKind.DWord);
                return OpResult.Ok();
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail("Access denied writing hidusbf parameters. Run as Administrator.");
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"Failed to write hidusbf parameters: {ex.Message}");
            }
        }

        // Change the patching level. The registry decides the mode; the file copy only
        // makes sure a build capable of patching (or not) is in place.
        public static OpResult ChangeDriverMode(DriverMode mode)
        {
            if (mode != DriverMode.NoPatch && IsMemoryIntegrityEnabled())
                return OpResult.Fail("Memory Integrity is enabled, so the patching driver cannot load. Turn it off in Windows Security > Core isolation and reboot first.");

            var source = GetSourceSys(mode);
            if (source == null)
                return OpResult.Fail($"No hidusbf.sys found for {PollingCore.DescribeMode(mode)} under {DriverDir}.");

            if (!ServiceExists())
                return OpResult.Fail("The hidusbf service is not installed yet. Install it first.");

            // Nothing to replace if the build we want is already the one installed. This
            // is the common case for mode changes between patching levels, since the
            // level lives in the registry: skipping the copy avoids needing the file
            // unlocked at all.
            string? installedHash = File.Exists(DriverPath) ? HashFile(DriverPath) : null;
            bool needsFileSwap = installedHash == null || HashFile(source) != installedHash;

            bool wasRunning = GetServiceStatus().Equals("Running", StringComparison.OrdinalIgnoreCase);

            if (needsFileSwap)
            {
                ControlService("stop");

                var copy = CopyDriverFile(source);
                if (!copy.Success)
                {
                    // The copy failed, so nothing changed - but we already stopped the
                    // service. Put it back the way we found it rather than leaving the
                    // system worse off than before an operation that did not happen.
                    if (wasRunning) ControlService("start");
                    return copy;
                }
            }

            var parameters = WritePatchParameters(mode);
            if (!parameters.Success)
            {
                if (wasRunning) ControlService("start");
                return parameters;
            }

            var started = ControlService("start");
            if (!started.Success)
                return OpResult.Fail(
                    $"Mode written, but the hidusbf service would not start again: {started.Error} " +
                    "Reboot to load it.");

            return OpResult.Ok();
        }

        // Software replug: tear the device out of the tree and re-enumerate it.
        //
        // This is NOT what RestartDevice does. Disable-PnpDevice / Enable-PnpDevice
        // restarts the PnP node - Windows unloads and reloads the drivers - but the
        // device never leaves the USB bus, keeps its address, and its descriptors are
        // never re-read from the hardware. hidusbf rewrites bInterval in the endpoint
        // descriptor, so a rate change often does not take until the device is
        // genuinely re-enumerated.
        //
        // SweetLow's own README lists these as separate remedies: "you should either
        // reboot, plug-out and plug-in mouse cable or stop and then start your mouse
        // in Device Manager". They are not equivalent, and the user confirmed the
        // shallow one does not apply the overclock on their DualSense.
        //
        // CM_Query_And_Remove_SubTree is what "Safely Remove Hardware" calls. It can be
        // VETOED when something holds the device open (a game, an audio stream), in
        // which case nothing happens and we say so rather than pretending.
        // Cuts power to the port the device sits on, then lets it come back. This is the
        // only software operation equivalent to pulling the cable: everything else leaves
        // the device powered, so its firmware never resets.
        //
        // Microsoft documents IOCTL_USB_HUB_CYCLE_PORT as unsupported from Windows 8, but
        // that is a documentation claim, not an observed one. Probed on this machine it
        // returned ERROR_INVALID_PARAMETER for a deliberately invalid port index - which
        // means Windows understood the call and validated the argument. An absent IOCTL
        // answers ERROR_INVALID_FUNCTION. So it works here; it may not everywhere, which
        // is why the caller falls back rather than depending on it.
        private static OpResult CyclePort(string instanceId)
        {
            if (CM_Locate_DevNode(out uint dev, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                return OpResult.Fail("Device not present.");

            // DEVPKEY_Device_Address on a USB device is its port number on the parent
            // hub, which is exactly what the cycle IOCTL indexes by.
            var addrKey = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 30 };
            var addrBuf = new byte[16];
            int addrSize = addrBuf.Length;
            if (CM_Get_DevNode_Property(dev, ref addrKey, out _, addrBuf, ref addrSize, 0) != CR_SUCCESS)
                return OpResult.Fail("Could not read the device's port number.");
            uint port = BitConverter.ToUInt32(addrBuf, 0);
            if (port == 0) return OpResult.Fail("The device reports port 0, which is not a real port.");

            if (CM_Get_Parent(out uint parent, dev, 0) != CR_SUCCESS)
                return OpResult.Fail("Could not find the parent hub.");

            var sb = new StringBuilder(400);
            if (CM_Get_Device_ID(parent, sb, sb.Capacity, 0) != CR_SUCCESS)
                return OpResult.Fail("Could not identify the parent hub.");

            var hubPaths = GetInterfaces(sb.ToString(), GUID_DEVINTERFACE_USB_HUB);
            if (hubPaths.Count == 0)
                return OpResult.Fail("The parent does not expose a USB hub interface, so its ports cannot be cycled.");

            using var hub = CreateFileW(hubPaths[0], GENERIC_READ | GENERIC_WRITE,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hub.IsInvalid)
                return OpResult.Fail($"Could not open the parent hub (error {Marshal.GetLastWin32Error()}).");

            var prm = new USB_CYCLE_PORT_PARAMS { ConnectionIndex = port, StatusReturned = 0 };
            int size = Marshal.SizeOf(prm);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(prm, buf, false);
                if (!DeviceIoControl(hub, IOCTL_USB_HUB_CYCLE_PORT, buf, size, buf, size, out _, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    return OpResult.Fail(err == 1 || err == 50
                        ? $"This Windows does not support cycling hub ports (error {err})."
                        : $"Cycling port {port} failed (error {err}).");
                }
                return OpResult.Ok();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static async Task<OpResult> ReplugDevice(string instanceId, int settleMs = 2000)
        {
            // Try the strongest thing first. A port cycle drops VBUS, so the device
            // powers down and its firmware restarts - which is what pulling the cable
            // does and what removing the device node does not. If the hub or this
            // Windows will not do it, fall through to the software replug below rather
            // than failing: a weaker reset beats none.
            var cycled = await Task.Run(() => CyclePort(instanceId));
            if (cycled.Success)
            {
                await Task.Delay(settleMs);

                // The port cycle re-enumerates on its own, but the device needs to be
                // back before anything can be asked of it.
                for (int i = 0; i < 20; i++)
                {
                    if (CM_Locate_DevNode(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS) break;
                    await Task.Delay(250);
                }

                if (CM_Locate_DevNode(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                    return OpResult.Fail("The port was power-cycled but the device has not come back. Unplug it and plug it in again.");

                await Task.Delay(SettleAfterReenumerateMs);
                var restartedAfterCycle = await RestartDevice(instanceId);
                return restartedAfterCycle.Success
                    ? OpResult.Ok()
                    : OpResult.Fail($"Port cycled, but the follow-up PnP restart failed: {restartedAfterCycle.Error}");
            }

            Debug.WriteLine($"Port cycle unavailable, falling back to node removal: {cycled.Error}");

            uint dev = 0, parent = 0;

            var located = await Task.Run(() =>
            {
                if (CM_Locate_DevNode(out dev, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                    return OpResult.Fail($"Device {instanceId} is not present, so it cannot be replugged.");

                // Re-enumeration is issued at the parent (the hub): removing a node and
                // then asking that same node to re-enumerate would be asking something
                // that no longer exists.
                if (CM_Get_Parent(out parent, dev, 0) != CR_SUCCESS)
                    return OpResult.Fail("Could not find the device's parent hub, so it cannot be re-enumerated safely.");

                var vetoName = new StringBuilder(MAX_PATH);
                int cr = CM_Query_And_Remove_SubTree(dev, out PnpVetoType veto, vetoName, (uint)vetoName.Capacity,
                                                     CM_REMOVE_NO_RESTART);

                if (cr == CR_REMOVE_VETOED)
                {
                    string who = vetoName.Length > 0 ? $" ({vetoName})" : "";
                    return OpResult.Fail(
                        $"Windows refused to remove the device: {veto}{who}. " +
                        "Something is holding it open - close anything using it and try again.");
                }

                if (cr != CR_SUCCESS)
                    return OpResult.Fail($"Removing the device failed (CONFIGRET 0x{cr:X}).");

                return OpResult.Ok();
            });

            if (!located.Success) return located;

            // The device is gone from the tree right now. Everything below is about
            // getting it back.
            await Task.Delay(settleMs);

            var reenumerated = await Task.Run(() =>
            {
                int cr = CM_Reenumerate_DevNode(parent, CM_REENUMERATE_SYNCHRONOUS | CM_REENUMERATE_RETRY_INSTALLATION);

                // If the hub would not re-enumerate, fall back to the root. Leaving the
                // user's controller removed is a far worse outcome than a slow rescan.
                if (cr != CR_SUCCESS && CM_Locate_DevNode(out uint root, null!, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
                    cr = CM_Reenumerate_DevNode(root, CM_REENUMERATE_SYNCHRONOUS);

                // Verifying it actually came back is the whole point. Claiming success
                // while the device is still missing is exactly the kind of lie this app
                // was rebuilt to stop telling.
                if (CM_Locate_DevNode(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                    return OpResult.Fail(
                        "The device was removed but has not come back. Unplug it and plug it in again - " +
                        $"re-enumeration returned CONFIGRET 0x{cr:X}.");

                return cr == CR_SUCCESS
                    ? OpResult.Ok()
                    : OpResult.Fail($"The device is back, but re-enumeration reported CONFIGRET 0x{cr:X}. The rate may not have applied.");
            });

            if (!reenumerated.Success) return reenumerated;

            // Re-enumeration alone is not enough: the user established empirically that
            // the rate only takes once a PnP restart FOLLOWS the replug. Neither step
            // does it alone. Plausible reading: on re-enumeration the device is
            // configured before hidusbf has fully attached to the stack, so the
            // descriptor is read without the filter in place; the restart re-issues
            // SELECT_CONFIGURATION with the filter present. Chained here so the button
            // is the whole operation rather than half of it.
            await Task.Delay(SettleAfterReenumerateMs);

            var restarted = await RestartDevice(instanceId);
            if (!restarted.Success)
                return OpResult.Fail(
                    $"Re-enumerated, but the follow-up PnP restart failed: {restarted.Error} " +
                    "The rate has probably not applied.");

            return OpResult.Ok();
        }

        // Windows needs a moment after re-enumeration before it will accept a disable
        // on the device it has just brought back.
        private const int SettleAfterReenumerateMs = 800;

        // PInvokes for Configuration Manager to traverse the device tree in memory
        private const int CR_SUCCESS = 0;
        private const int CR_REMOVE_VETOED = 0x11;
        private const int MAX_PATH = 260;
        private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
        private const uint CM_REMOVE_NO_RESTART = 0x00000002;
        private const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;
        private const uint CM_REENUMERATE_RETRY_INSTALLATION = 0x00000020;

        // Why Windows refused a removal. Reported verbatim to the user - "PendingClose"
        // or "OutstandingOpen" tells them something still has the device open.
        private enum PnpVetoType
        {
            Unknown = 0,
            LegacyDevice,
            PendingClose,
            WindowsApp,
            WindowsService,
            OutstandingOpen,
            Device,
            Driver,
            IllegalDeviceRequest,
            InsufficientPower,
            NonDisableable,
            LegacyDriver,
            InsufficientRights,
            AlreadyRemoved
        }

        // GUID_DEVINTERFACE_USB_HUB
        private static Guid GUID_DEVINTERFACE_USB_HUB = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8");
        private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct USB_CYCLE_PORT_PARAMS { public uint ConnectionIndex; public uint StatusReturned; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code,
            IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Property(uint dev, ref DEVPROPKEY key, out uint type,
            byte[] buffer, ref int size, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_Size(out int len, ref Guid cls, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List(ref Guid cls, string deviceId, char[] buf, int len, uint flags);

        private static List<string> GetInterfaces(string deviceId, Guid cls)
        {
            var result = new List<string>();
            if (CM_Get_Device_Interface_List_Size(out int len, ref cls, deviceId, 0) != CR_SUCCESS || len < 2)
                return result;
            var buf = new char[len];
            if (CM_Get_Device_Interface_List(ref cls, deviceId, buf, len, 0) != CR_SUCCESS)
                return result;
            foreach (var s in new string(buf).Split('\0'))
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
            return result;
        }

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Query_And_Remove_SubTreeW", CharSet = CharSet.Unicode)]
        private static extern int CM_Query_And_Remove_SubTree(uint dnAncestor, out PnpVetoType pVetoType,
                                                              StringBuilder pszVetoName, uint ulNameLength, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID(uint dnDevInst, StringBuilder Buffer, int BufferLen, uint ulFlags);

        // Retrieve all descendants (children, grandchildren, etc.) of a given parent instance ID
        private static List<string> GetDescendantInstanceIds(string parentInstanceId)
        {
            var list = new List<string>();
            uint devInst;
            if (CM_Locate_DevNode(out devInst, parentInstanceId, 0) == CR_SUCCESS)
            {
                uint childInst;
                if (CM_Get_Child(out childInst, devInst, 0) == CR_SUCCESS)
                {
                    TraverseSiblingsAndChildren(childInst, list);
                }
            }
            return list;
        }

        private static void TraverseSiblingsAndChildren(uint devInst, List<string> list)
        {
            StringBuilder sb = new StringBuilder(200);
            if (CM_Get_Device_ID(devInst, sb, sb.Capacity, 0) == CR_SUCCESS)
            {
                list.Add(sb.ToString());
            }

            uint childInst;
            if (CM_Get_Child(out childInst, devInst, 0) == CR_SUCCESS)
            {
                TraverseSiblingsAndChildren(childInst, list);
            }

            uint siblingInst;
            if (CM_Get_Sibling(out siblingInst, devInst, 0) == CR_SUCCESS)
            {
                TraverseSiblingsAndChildren(siblingInst, list);
            }
        }

        // Fast helper to fetch DeviceDesc/FriendlyName directly from registry using device instance ID
        private static string GetDeviceNameFromRegistry(string instanceId)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
                if (key != null)
                {
                    var friendlyName = key.GetValue("FriendlyName") as string;
                    if (!string.IsNullOrEmpty(friendlyName)) return friendlyName;

                    var deviceDesc = key.GetValue("DeviceDesc") as string;
                    if (!string.IsNullOrEmpty(deviceDesc))
                    {
                        // DeviceDesc can be formatted as "@oemXX.inf,%key%;Name" - extract the suffix after the last semicolon if it exists
                        int semiIdx = deviceDesc.LastIndexOf(';');
                        if (semiIdx >= 0 && semiIdx < deviceDesc.Length - 1)
                        {
                            return deviceDesc.Substring(semiIdx + 1);
                        }
                        return deviceDesc;
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
