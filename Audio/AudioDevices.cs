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
        public Guid? ContainerId { get; init; }
        public bool HasCmEq { get; init; }
        public override string ToString() => Name;
    }

    public static class AudioDevices
    {
        private static readonly PropertyKey FriendlyNameKey =
            new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        private static readonly PropertyKey ContainerIdKey =
            new(new Guid("8c7ed206-3f8a-4827-8846-1d61deb04b73"), 2);
        private static readonly Guid SystemContainer = new("00000000-0000-0000-ffff-ffffffffffff");

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
        internal static AudioDeviceInfo? FindMatchingInput(AudioDeviceInfo? output, IReadOnlyList<AudioDeviceInfo> inputs)
        {
            if (output == null) return null;
            var container = PhysicalContainer(output);
            if (container.HasValue)
            {
                var sameDevice = inputs.Where(input => PhysicalContainer(input) == container).ToList();
                if (sameDevice.Count == 1) return sameDevice[0];
                if (sameDevice.Count > 1) return UniqueNameMatch(output, sameDevice);
            }

            // Some drivers omit container metadata. A unique hardware name is the fallback.
            return UniqueNameMatch(output, inputs.Where(input => !container.HasValue || !PhysicalContainer(input).HasValue));
        }

        internal static AudioDeviceInfo? SelectInput(IReadOnlyList<AudioDeviceInfo> inputs, string? selectedId,
            AudioDeviceInfo? pairedInput, string? windowsDefaultId)
        {
            if (selectedId != null)
                return inputs.FirstOrDefault(input => SameId(input.Id, selectedId));
            return pairedInput ?? inputs.FirstOrDefault(input => SameId(input.Id, windowsDefaultId)) ?? inputs.FirstOrDefault();
        }

        internal static bool SameId(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private static Guid? PhysicalContainer(AudioDeviceInfo info) =>
            info.ContainerId == Guid.Empty || info.ContainerId == SystemContainer ? null : info.ContainerId;

        private static AudioDeviceInfo? UniqueNameMatch(AudioDeviceInfo output, IEnumerable<AudioDeviceInfo> inputs)
        {
            string hardware = HardwareName(output.Name);
            if (string.IsNullOrWhiteSpace(hardware)) return null;
            var matches = inputs.Where(input => string.Equals(HardwareName(input.Name), hardware, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static string HardwareName(string name)
        {
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            return (open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name).Trim();
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
                            var properties = ReadEndpointProperties(device);
                            result.Add(new AudioDeviceInfo
                            {
                                Id = id,
                                Name = properties.Name ?? id,
                                ContainerId = properties.ContainerId,
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

        private static (string? Name, Guid? ContainerId) ReadEndpointProperties(IMMDevice device)
        {
            if (device.OpenPropertyStore(Native.StgmRead, out var store) != 0 || store == null) return (null, null);
            try
            {
                string? name = null;
                Guid? containerId = null;
                var key = FriendlyNameKey;
                if (store.GetValue(ref key, out var pv) == 0)
                {
                    try { if (pv.vt == Native.VtLpwstr) name = Marshal.PtrToStringUni(pv.pointer); }
                    finally { Native.PropVariantClear(ref pv); }
                }
                key = ContainerIdKey;
                if (store.GetValue(ref key, out pv) == 0)
                {
                    try
                    {
                        if (pv.vt == Native.VtClsid && pv.pointer != IntPtr.Zero)
                            containerId = Marshal.PtrToStructure<Guid>(pv.pointer);
                    }
                    finally { Native.PropVariantClear(ref pv); }
                }
                return (name, containerId);
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
        /// <summary>Headset C-Media equalizer, or Equalizer APO for any other output device.</summary>
        public IEqualizer? Eq { get; }
        public ApoPropertyStore? Effects { get; }

        internal AudioDeviceSession(AudioDeviceInfo info, IMMDevice device)
        {
            Info = info;
            this.device = device;

            try
            {
                Native.Check(device.GetState(out int state));
                if ((state & Native.DeviceStateActive) == 0)
                    throw new InvalidOperationException("Аудиоустройство отключено.");

                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, Native.ClsCtxAll, IntPtr.Zero, out object obj) == 0)
                    volume = obj as IAudioEndpointVolume;

                if (info.Id.StartsWith("{0.0.0", StringComparison.Ordinal))
                    Eq = (IEqualizer?)CmEqController.TryOpen(device, info.Id) ?? EqApoController.TryOpen(info.Id);
                Effects = ApoPropertyStore.Open(device, info.Id);
            }
            catch
            {
                Dispose();
                throw;
            }
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
