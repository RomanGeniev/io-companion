using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace GraphiteCompanion.Audio
{
    public sealed class AudioDeviceInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public bool HasCmEq { get; init; }
        public override string ToString() => Name;
    }

    public static class AudioDevices
    {
        private static readonly PropertyKey FriendlyNameKey =
            new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        private static readonly Type EnumeratorType =
            Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!;

        // Created by CLSID and cast to the interface only: NAudio registers its own wrapper class for
        // the same COM object, so casting to a concrete class would fail once NAudio was used.
        private static IMMDeviceEnumerator CreateEnumerator() =>
            (IMMDeviceEnumerator)Activator.CreateInstance(EnumeratorType)!;

        /// <summary>Active output devices; ones with the C-Media equalizer come first.</summary>
        public static List<AudioDeviceInfo> ListActiveOutputs() => ListActive(Native.DataFlowRender, probeEq: true);

        /// <summary>Active microphones.</summary>
        public static List<AudioDeviceInfo> ListActiveInputs() => ListActive(Native.DataFlowCapture, probeEq: false);

        /// <summary>Finds the microphone that belongs to the same headset as the given output device.</summary>
        public static AudioDeviceInfo? FindMatchingInput(AudioDeviceInfo output)
        {
            string name = output.Name;
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            string hardware = open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name;
            return ListActiveInputs().FirstOrDefault(d => d.Name.Contains(hardware, StringComparison.OrdinalIgnoreCase));
        }

        private static List<AudioDeviceInfo> ListActive(int dataFlow, bool probeEq)
        {
            var result = new List<AudioDeviceInfo>();
            var enumerator = CreateEnumerator();
            try
            {
                Native.Check(enumerator.EnumAudioEndpoints(dataFlow, Native.DeviceStateActive, out var collection));
                try
                {
                    collection.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var device) != 0 || device == null) continue;
                        try
                        {
                            if (device.GetId(out string id) != 0) continue;
                            result.Add(new AudioDeviceInfo
                            {
                                Id = id,
                                Name = ReadFriendlyName(device) ?? id,
                                HasCmEq = probeEq && CmEqController.Probe(device, id),
                            });
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(collection);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
            return result.OrderByDescending(d => d.HasCmEq).ThenBy(d => d.Name).ToList();
        }

        public static AudioDeviceSession Open(AudioDeviceInfo info)
        {
            var enumerator = CreateEnumerator();
            try
            {
                Native.Check(enumerator.GetDevice(info.Id, out var device));
                return new AudioDeviceSession(info, device);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private static string? ReadFriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(Native.StgmRead, out var store) != 0 || store == null) return null;
            try
            {
                var key = FriendlyNameKey;
                if (store.GetValue(ref key, out var pv) != 0) return null;
                try
                {
                    return pv.vt == Native.VtLpwstr ? Marshal.PtrToStringUni(pv.pointer) : null;
                }
                finally
                {
                    Native.PropVariantClear(ref pv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    public sealed class AudioDeviceSession : IDisposable
    {
        private IMMDevice? device;
        private IAudioEndpointVolume? volume;

        public AudioDeviceInfo Info { get; }
        public CmEqController? Eq { get; }
        public ApoPropertyStore? Effects { get; }

        internal AudioDeviceSession(AudioDeviceInfo info, IMMDevice device)
        {
            Info = info;
            this.device = device;

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, Native.ClsCtxAll, IntPtr.Zero, out object obj) == 0)
                volume = obj as IAudioEndpointVolume;

            Eq = CmEqController.TryOpen(device, info.Id);
            Effects = ApoPropertyStore.Open(device, info.Id);
        }

        public bool HasVolume => volume != null;

        public float GetVolume()
        {
            if (volume == null) return 0;
            Native.Check(volume.GetMasterVolumeLevelScalar(out float v));
            return v;
        }

        public void SetVolume(float value)
        {
            if (volume == null) return;
            var ctx = Guid.Empty;
            Native.Check(volume.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref ctx));
        }

        public bool GetMute()
        {
            if (volume == null) return false;
            Native.Check(volume.GetMute(out bool m));
            return m;
        }

        public void SetMute(bool mute)
        {
            if (volume == null) return;
            var ctx = Guid.Empty;
            Native.Check(volume.SetMute(mute, ref ctx));
        }

        public void Dispose()
        {
            Eq?.Dispose();
            Effects?.Dispose();
            if (volume != null) { Marshal.ReleaseComObject(volume); volume = null; }
            if (device != null) { Marshal.ReleaseComObject(device); device = null; }
        }
    }
}
