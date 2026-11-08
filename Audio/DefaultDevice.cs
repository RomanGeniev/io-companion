using System;
using System.Runtime.InteropServices;

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
        public static string? Get(int dataFlow)
        {
            object? en = null;
            try
            {
                en = Activator.CreateInstance(EnumeratorType);
                if (((IMMDeviceEnumerator)en!).GetDefaultAudioEndpoint(dataFlow, 1, out var dev) != 0 || dev == null) return null;
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

        /// <summary>Makes the device the default for console, multimedia and communications.</summary>
        public static bool Set(string deviceId)
        {
            object? pc = null;
            try
            {
                pc = Activator.CreateInstance(PolicyConfigType);
                var policy = (IPolicyConfig)pc!;
                bool ok = true;
                for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(deviceId, role) == 0;
                return ok;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (pc != null) Marshal.ReleaseComObject(pc);
            }
        }
    }
}
