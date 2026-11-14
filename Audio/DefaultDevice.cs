using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Audio
{
    /// <summary>
    /// Reads and changes the Windows default playback / recording device (all roles).
    /// Uses the IPolicyConfig interface that the Windows Sound control panel itself uses.
    /// </summary>
    public static class DefaultDevice
    {
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(string id, IntPtr format);
            [PreserveSig] int GetDeviceFormat(string id, int defaultFormat, IntPtr format);
            [PreserveSig] int ResetDeviceFormat(string id);
            [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod(string id, int defaultPeriod, IntPtr defPeriod, IntPtr minPeriod);
            [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
            [PreserveSig] int GetShareMode(string id, IntPtr mode);
            [PreserveSig] int SetShareMode(string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility(string id, int visible);
        }

        private static readonly Type PolicyConfigType =
            Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!;
        private static readonly Type EnumeratorType =
            Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!;

        /// <summary>Id of the current default device (multimedia role) for render (0) or capture (1).</summary>
        public static string? Get(int dataFlow) => Get(dataFlow, 1);

        private static string? Get(int dataFlow, int role)
        {
            object? en = null;
            try
            {
                en = Activator.CreateInstance(EnumeratorType);
                if (((IMMDeviceEnumerator)en!).GetDefaultAudioEndpoint(dataFlow, role, out var dev) < 0 || dev == null) return null;
                try { return dev.GetId(out var id) == 0 ? id : null; }
                finally { Marshal.ReleaseComObject(dev); }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        public sealed record ChangeResult(bool PlaybackSelected, bool AllRolesSelected, string Details);

        /// <summary>Makes the device the default for all playback roles and verifies the result.</summary>
        public static ChangeResult Set(string deviceId)
        {
            object? pc = null;
            try
            {
                pc = Activator.CreateInstance(PolicyConfigType);
                var policy = (IPolicyConfig)pc!;
                var result = Apply(deviceId, role => policy.SetDefaultEndpoint(deviceId, role), role => Get(0, role));
                if (!result.AllRolesSelected || result.Details.Length > 0)
                    SettingsStore.LogError(new InvalidOperationException("Switch playback endpoint: " + result.Details));
                return result;
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                // Even if policy activation fails, an already current endpoint can be
                // selected in the app. The verified Windows state remains authoritative.
                return Apply(deviceId, _ => ex.HResult, role => Get(0, role));
            }
            finally
            {
                if (pc != null) Marshal.ReleaseComObject(pc);
            }
        }

        // Separate policy calls from verification so partial success cannot leave the
        // application's controls attached to a different device than Windows multimedia.
        internal static ChangeResult Apply(string deviceId, Func<int, int> setRole, Func<int, string?> getRole)
        {
            var errors = new List<string>();
            for (int role = 0; role < 3; role++)
            {
                try
                {
                    int hr = setRole(role);
                    if (hr < 0) errors.Add($"role={role} HRESULT=0x{hr:X8}");
                }
                catch (Exception ex) { errors.Add($"role={role}: {ex.Message}"); }
            }
            bool playback = false, all = true;
            for (int role = 0; role < 3; role++)
            {
                bool selected = false;
                try { selected = string.Equals(getRole(role), deviceId, StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) { errors.Add($"read role={role}: {ex.Message}"); }
                if (!selected) errors.Add($"role={role}: requested endpoint is not default");
                if (role == 1) playback = selected;
                all &= selected;
            }
            return new(playback, all, string.Join("; ", errors));
        }
    }
}
