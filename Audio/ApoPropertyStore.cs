using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GraphiteCompanion.Audio
{
    /// <summary>C-Media separates SFX, MFX and EFX properties; see CM2025 extension INF.</summary>
    public sealed class ApoPropertyStore : IDisposable
    {
        internal static readonly Guid StreamContext = new("c31a35a5-22ec-425e-9b74-b64700f067da");
        internal static readonly Guid EndpointContext = new("9c97fb8f-7c45-4ef3-a5a8-67d0ac630e8f");
        private static readonly Guid Xear = new("5e96e4d9-ff2c-4d84-9bd6-fe10cf815009");
        private static readonly Guid Surround = new("c020cdbf-756f-4b6d-9031-be6e20cf1c9f");
        private readonly Dictionary<Guid, Store> stores = new();
        private string? registryPath;
        private bool disposed;
        private sealed class Store(IAudioSystemEffectsPropertyStore fx, IPropertyStore user, IPropertyStore? defaults) : IDisposable
        {
            public IPropertyStore User => user;
            public IPropertyStore? Defaults => defaults;
            public void Dispose()
            {
                Marshal.ReleaseComObject(user);
                if (defaults != null) Marshal.ReleaseComObject(defaults);
                Marshal.ReleaseComObject(fx);
            }
        }
        // Smart Volume uses the same format GUID as SFX effects, but its PIDs belong to MFX.
        internal static Guid ContextFor(Guid format, uint pid) => format == Surround ? EndpointContext :
            format == Xear && pid is not (9 or 10 or 11) ? StreamContext : CmEqController.StoreContext;

        internal static ApoPropertyStore? Open(IMMDevice device, string deviceId)
        {
            var result = new ApoPropertyStore { registryPath = FxRegistryPath(deviceId) };
            var contexts = deviceId.StartsWith("{0.0.1", StringComparison.Ordinal)
                ? new[] { CmEqController.StoreContext } : new[] { CmEqController.StoreContext, StreamContext, EndpointContext };
            foreach (var context in contexts)
            {
                var fx = CmEqController.ActivateFx(device, context);
                if (fx == null) continue;
                IPropertyStore? user = null, defaults = null;
                try
                {
                    if (fx.OpenUserPropertyStore(Native.StgmReadWrite, out user) != 0 || user == null) continue;
                    try { if (fx.OpenDefaultPropertyStore(Native.StgmRead, out var d) == 0) defaults = d; } catch { }
                    result.stores.Add(context, new Store(fx, user, defaults));
                }
                catch { /* Unsupported contexts are unavailable, never redirected to the EQ store. */ }
                finally
                {
                    if (!result.stores.ContainsKey(context))
                    {
                        if (user != null) Marshal.ReleaseComObject(user);
                        if (defaults != null) Marshal.ReleaseComObject(defaults);
                        Marshal.ReleaseComObject(fx);
                    }
                }
            }
            return result.stores.Count > 0 ? result : null;
        }
        internal static string? FxRegistryPath(string deviceId)
        {
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
                return key?.GetValue($"{{{format}}},{pid}") switch { int i => i, long l => unchecked((int)l), _ => null };
            }
            catch { return null; }
        }
        public int? Read(Guid format, uint pid)
        {
            if (disposed || !stores.TryGetValue(ContextFor(format, pid), out var store)) return null;
            return ReadFrom(store.User, format, pid) ?? ReadRegistry(registryPath, format, pid)
                ?? (store.Defaults != null ? ReadFrom(store.Defaults, format, pid) : null);
        }
        public void Write(Guid format, uint pid, int value)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ApoPropertyStore));
            if (!stores.TryGetValue(ContextFor(format, pid), out var store))
                throw new InvalidOperationException("Драйвер не предоставляет хранилище этого эффекта.");
            var key = new PropertyKey(format, pid);
            var pv = new PropVariant { vt = Native.VtUI4, intValue = value };
            Native.Check(store.User.SetValue(ref key, ref pv));
            Native.Check(store.User.Commit());
            if (ReadFrom(store.User, format, pid) != value)
                throw new InvalidOperationException("Драйвер не подтвердил новое значение эффекта.");
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
            if (disposed) return;
            disposed = true;
            foreach (var store in stores.Values) store.Dispose();
            stores.Clear();
        }
    }
}
