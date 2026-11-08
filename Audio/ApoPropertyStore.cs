using System;
using System.Runtime.InteropServices;

namespace GraphiteCompanion.Audio
{
    /// <summary>
    /// Generic read/write access to the C-Media APO settings of one endpoint
    /// (user effects property store, same context as the official Audio Center).
    /// </summary>
    public sealed class ApoPropertyStore : IDisposable
    {
        private IAudioSystemEffectsPropertyStore? fx;
        private IPropertyStore? user;
        private IPropertyStore? defaults;
        private string? registryPath;

        private ApoPropertyStore(IAudioSystemEffectsPropertyStore fx, IPropertyStore user, IPropertyStore? defaults)
        {
            this.fx = fx;
            this.user = user;
            this.defaults = defaults;
        }

        internal static ApoPropertyStore? Open(IMMDevice device, string deviceId)
        {
            var store = Open(device);
            if (store != null) store.registryPath = FxRegistryPath(deviceId);
            return store;
        }

        /// <summary>
        /// Audio Center also keeps effect values in the endpoint's FxProperties key (readable by
        /// any user). When a value is missing in the user store this is the state the driver uses.
        /// </summary>
        internal static string? FxRegistryPath(string deviceId)
        {
            // Device id looks like "{0.0.0.00000000}.{guid}" (render) or "{0.0.1.00000000}.{guid}" (capture).
            int dot = deviceId.LastIndexOf('.');
            if (dot < 0) return null;
            string flow = deviceId.StartsWith("{0.0.1", StringComparison.Ordinal) ? "Capture" : "Render";
            return $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}\{deviceId[(dot + 1)..]}\FxProperties";
        }

        internal static int? ReadRegistry(string? path, Guid format, uint pid)
        {
            if (path == null) return null;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                return key?.GetValue($"{{{format}}},{pid}") switch
                {
                    int i => i,
                    long l => unchecked((int)l),
                    _ => null,
                };
            }
            catch
            {
                return null;
            }
        }

        internal static ApoPropertyStore? Open(IMMDevice device)
        {
            var store = CmEqController.ActivateFx(device);
            if (store == null) return null;
            try
            {
                if (store.OpenUserPropertyStore(Native.StgmReadWrite, out var user) != 0 || user == null)
                {
                    Marshal.ReleaseComObject(store);
                    return null;
                }
                IPropertyStore? defaults = null;
                try { if (store.OpenDefaultPropertyStore(Native.StgmRead, out var d) == 0) defaults = d; } catch { }
                return new ApoPropertyStore(store, user, defaults);
            }
            catch
            {
                Marshal.ReleaseComObject(store);
                return null;
            }
        }

        public int? Read(Guid format, uint pid)
        {
            if (user == null) return null;
            return ReadFrom(user, format, pid)
                   ?? ReadRegistry(registryPath, format, pid)
                   ?? (defaults != null ? ReadFrom(defaults, format, pid) : null);
        }

        public void Write(Guid format, uint pid, int value)
        {
            if (user == null) throw new ObjectDisposedException(nameof(ApoPropertyStore));
            var key = new PropertyKey(format, pid);
            var pv = new PropVariant { vt = Native.VtUI4, intValue = value };
            Native.Check(user.SetValue(ref key, ref pv));
            Native.Check(user.Commit());
        }

        private static int? ReadFrom(IPropertyStore store, Guid format, uint pid)
        {
            var key = new PropertyKey(format, pid);
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt is Native.VtI4 or Native.VtUI4 ? pv.intValue : null; }
            finally { Native.PropVariantClear(ref pv); }
        }

        public void Dispose()
        {
            if (user != null) { Marshal.ReleaseComObject(user); user = null; }
            if (defaults != null) { Marshal.ReleaseComObject(defaults); defaults = null; }
            if (fx != null) { Marshal.ReleaseComObject(fx); fx = null; }
        }
    }
}
