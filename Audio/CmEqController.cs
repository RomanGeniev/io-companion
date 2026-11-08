using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GraphiteCompanion.Models;

namespace GraphiteCompanion.Audio
{
    /// <summary>
    /// Controls the built-in 10-band equalizer of the C-Media CM2025 APO (io Graphite 2 Pro and
    /// other headsets on the same chip). Values are written to the endpoint's user effects
    /// property store - the same place the official Audio Center writes to.
    /// Property {bd341ffb-…},0 = EQ on/off, ,1..,10 = gains in dB for 30 Hz … 16 kHz (-20…+20).
    /// </summary>
    public sealed class CmEqController : IEqualizer
    {
        public static readonly Guid StoreContext = new("4c6c316b-c24f-421c-a266-690540cabd11");
        private static readonly Guid EqFormat = new("bd341ffb-9fd1-4ed4-8159-7999d546ea8a");
        private const uint EnablePid = 0;

        private IAudioSystemEffectsPropertyStore? fx;
        private IPropertyStore? user;
        private IPropertyStore? defaults;
        private string? registryPath;

        public string Engine => "C-Media";

        private CmEqController(IAudioSystemEffectsPropertyStore fx, IPropertyStore user, IPropertyStore? defaults)
        {
            this.fx = fx;
            this.user = user;
            this.defaults = defaults;
        }

        internal static IAudioSystemEffectsPropertyStore? ActivateFx(IMMDevice device)
        {
            var iid = typeof(IAudioSystemEffectsPropertyStore).GUID;
            IntPtr guidPtr = Marshal.AllocCoTaskMem(16);
            IntPtr pv = Marshal.AllocCoTaskMem(24);
            try
            {
                Marshal.StructureToPtr(StoreContext, guidPtr, false);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, (short)Native.VtClsid);
                Marshal.WriteIntPtr(pv, 8, guidPtr);
                int hr = device.Activate(ref iid, Native.ClsCtxInproc, pv, out object obj);
                return hr == 0 ? obj as IAudioSystemEffectsPropertyStore : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pv);
                Marshal.FreeCoTaskMem(guidPtr);
            }
        }

        /// <summary>Read-only check: does this endpoint expose the C-Media EQ properties?</summary>
        internal static bool Probe(IMMDevice device, string? deviceId = null)
        {
            if (deviceId != null && ApoPropertyStore.ReadRegistry(ApoPropertyStore.FxRegistryPath(deviceId), EqFormat, 1).HasValue)
                return true;
            var store = ActivateFx(device);
            if (store == null) return false;
            try
            {
                if (HasBand(store.OpenUserPropertyStore(Native.StgmRead, out var u), u)) return true;
                return HasBand(store.OpenDefaultPropertyStore(Native.StgmRead, out var d), d);
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        private static bool HasBand(int hr, IPropertyStore? s)
        {
            if (hr != 0 || s == null) return false;
            try { return ReadInt(s, 1).HasValue; }
            finally { Marshal.ReleaseComObject(s); }
        }

        internal static CmEqController? TryOpen(IMMDevice device, string? deviceId = null)
        {
            if (!Probe(device, deviceId)) return null;
            var store = ActivateFx(device);
            if (store == null) return null;
            if (store.OpenUserPropertyStore(Native.StgmReadWrite, out var user) != 0 || user == null)
            {
                Marshal.ReleaseComObject(store);
                return null;
            }
            IPropertyStore? defaults = null;
            try
            {
                if (store.OpenDefaultPropertyStore(Native.StgmRead, out var d) == 0) defaults = d;
            }
            catch { /* optional */ }
            return new CmEqController(store, user, defaults) { registryPath = deviceId == null ? null : ApoPropertyStore.FxRegistryPath(deviceId) };
        }

        public int[] ReadBands()
        {
            var result = new int[EqBands.Count];
            for (int i = 0; i < EqBands.Count; i++)
            {
                int v = Read((uint)(i + 1)) ?? 0;
                result[i] = Math.Clamp(v, EqBands.Min, EqBands.Max);
            }
            return result;
        }

        public bool ReadEnabled() => (Read(EnablePid) ?? 1) != 0;

        public void SetEnabled(bool enabled)
        {
            Write(EnablePid, enabled ? 1 : 0);
            Native.Check(Store.Commit());
        }

        /// <summary>Writes bands; when <paramref name="previous"/> is given only changed bands are sent.</summary>
        public void SetBands(IReadOnlyList<int> bands, IReadOnlyList<int>? previous)
        {
            bool changed = false;
            for (int i = 0; i < EqBands.Count; i++)
            {
                if (previous != null && previous[i] == bands[i]) continue;
                Write((uint)(i + 1), Math.Clamp(bands[i], EqBands.Min, EqBands.Max));
                changed = true;
            }
            if (changed) Native.Check(Store.Commit());
        }

        private IPropertyStore Store => user ?? throw new ObjectDisposedException(nameof(CmEqController));

        private int? Read(uint pid)
        {
            var v = ReadInt(Store, pid);
            v ??= ApoPropertyStore.ReadRegistry(registryPath, EqFormat, pid);
            if (v == null && defaults != null) v = ReadInt(defaults, pid);
            return v;
        }

        private void Write(uint pid, int value)
        {
            var key = new PropertyKey(EqFormat, pid);
            var pv = new PropVariant { vt = Native.VtUI4, intValue = value };
            Native.Check(Store.SetValue(ref key, ref pv));
        }

        private static int? ReadInt(IPropertyStore store, uint pid)
        {
            var key = new PropertyKey(EqFormat, pid);
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try
            {
                return pv.vt is Native.VtI4 or Native.VtUI4 ? pv.intValue : null;
            }
            finally
            {
                Native.PropVariantClear(ref pv);
            }
        }

        public void Dispose()
        {
            if (user != null) { Marshal.ReleaseComObject(user); user = null; }
            if (defaults != null) { Marshal.ReleaseComObject(defaults); defaults = null; }
            if (fx != null) { Marshal.ReleaseComObject(fx); fx = null; }
        }
    }
}
