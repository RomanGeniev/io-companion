using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GraphiteCompanion.Services
{
    /// <summary>
    /// Battery level of wireless headphones/speakers, best-effort and read-only.
    /// Sources tried in order: standard HID Battery Page (0x85) — works for many
    /// 2.4 GHz dongles — and the BLE GATT Battery Service (0x180F) — most TWS and
    /// BLE headsets. Devices that expose neither (proprietary vendor protocols,
    /// e.g. the io Graphite dongle, and Bluetooth Classic headsets, which Windows
    /// only serves over an RFCOMM connection that would interrupt playback) are
    /// reported as "unknown" rather than guessed at.
    /// </summary>
    public sealed class BatteryService : IDisposable
    {
        public sealed record Level(int Percent, string Source);

        private Level? cached;
        private DateTime lastAttempt = DateTime.MinValue;
        private string? lastDeviceId;
        private int lastLowNotify;          // 0 = none, 20/10 thresholds
        private readonly List<IDisposable> toRelease = new();

        private static readonly TimeSpan CacheSpan = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RetrySpan = TimeSpan.FromMinutes(5);

        /// <summary>Current level for the device, or null if it does not report one.
        /// Results are cached; a device that reports nothing is re-probed less often.</summary>
        public Level? Read(string deviceId, string deviceName)
        {
            var now = DateTime.UtcNow;
            if (deviceId == lastDeviceId)
            {
                if (cached != null && now - lastAttempt < CacheSpan) return cached;
                if (cached == null && now - lastAttempt < RetrySpan) return null;
            }

            lastDeviceId = deviceId;
            lastAttempt = now;
            cached = ReadHid(deviceName) ?? ReadBle(deviceName);
            return cached;
        }

        /// <summary>Tray/OSD hint for the level; returns null when there is nothing to show.</summary>
        public string? Describe(Level? level) => level == null ? null : $"Батарея: {level.Percent}%";

        /// <summary>Low-battery balloon once per threshold crossing; call from the poll tick.</summary>
        public bool ShouldNotifyLow(Level? level, out string message)
        {
            message = "";
            if (level == null) { lastLowNotify = 0; return false; }
            if (level.Percent > 20) { lastLowNotify = 0; return false; }
            int threshold = level.Percent <= 10 ? 10 : 20;
            if (lastLowNotify == threshold) return false;
            lastLowNotify = threshold;
            message = level.Percent <= 10
                ? $"Заряд наушников критический: {level.Percent}%. Пора на зарядку."
                : $"Заряд наушников садится: {level.Percent}%.";
            return true;
        }

        // ==================== HID Battery Page (0x85) ====================

        private Level? ReadHid(string deviceName)
        {
            // The audio endpoint id does not name the HID device, so match by product
            // name: dongles usually report the headset's name as their product string.
            // A device whose name matches anywhere in it wins over unmatched ones.
            Level? best = null, bestNamed = null;
            foreach (var (percent, source, product) in HidBattery.Enumerate())
            {
                var level = new Level(percent, source);
                if (NamesMatch(product ?? "", deviceName)) bestNamed ??= level;
                best ??= level;
            }
            return bestNamed ?? best;
        }

        private static bool NamesMatch(string hidName, string audioName)
        {
            string h = Simplify(hidName), a = Simplify(audioName);
            if (h.Length < 3) return false;
            return a.Contains(h, StringComparison.Ordinal) || h.Contains(a, StringComparison.Ordinal);
        }

        private static string Simplify(string s) =>
            new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // ==================== BLE GATT Battery Service ====================

        private Level? ReadBle(string deviceName)
        {
            try
            {
                return ReadBleCore(deviceName);
            }
            catch
            {
                return null; // WinRT throws when the radio or device is gone
            }
        }

        private Level? ReadBleCore(string deviceName)
        {
            // Windows.Devices.Bluetooth.Advertisement would be cleaner but the
            // BluetoothLEDevice API is the one that reaches the battery service.
            var selector = "System.DeviceInterface.Bluetooth.ServiceGuid:= \"{0000180F-0000-1000-8000-00805F9B34FB}\"";
            var devices = Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector, new[] { "System.ItemNameDisplay", "System.DeviceInterface.Bluetooth.DeviceAddress" })
                .GetAwaiter().GetResult();
            Level? best = null;
            string simple = Simplify(deviceName);
            foreach (var d in devices)
            {
                string name = d.Name ?? "";
                if (simple.Length >= 3 && !Simplify(name).Contains(simple, StringComparison.Ordinal)) continue;
                var le = Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(d.Id).GetAwaiter().GetResult();
                if (le == null) continue;
                toRelease.Add(le);
                var services = le.GetGattServicesAsync().GetAwaiter().GetResult();
                if (services.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success) continue;
                foreach (var s in services.Services)
                {
                    if (s.Uuid != GattBatteryServiceUuid) continue;
                    var chars = s.GetCharacteristicsAsync().GetAwaiter().GetResult();
                    if (chars.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success) continue;
                    foreach (var c in chars.Characteristics)
                    {
                        if (c.Uuid != GattBatteryLevelCharacteristicUuid) continue;
                        var read = c.ReadValueAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached).GetAwaiter().GetResult();
                        if (read.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success) continue;
                        var data = read.Value;
                        if (data == null || data.Length < 1) continue;
                        using var dr = Windows.Storage.Streams.DataReader.FromBuffer(data);
                        byte pct = dr.ReadByte();
                        if (pct is > 0 and <= 100)
                            best = best == null || pct > best.Percent ? new Level(pct, "BLE") : best;
                    }
                }
            }
            return best;
        }

        private static readonly Guid GattBatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid GattBatteryLevelCharacteristicUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

        public void Dispose()
        {
            foreach (var d in toRelease) try { d.Dispose(); } catch { }
            toRelease.Clear();
        }
    }

    /// <summary>Read-only enumeration of HID devices that expose the Battery Page (usage page 0x85).</summary>
    internal static class HidBattery
    {
        // usage 0x004D "State of Charge" is what hidbatt maps to a percentage
        private const ushort UsageStateOfCharge = 0x004D;
        private const ushort PageBattery = 0x85;

        [StructLayout(LayoutKind.Sequential)]
        private class HIDD_ATTRIBUTES { public int Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)); public ushort VendorID; public ushort ProductID; public short VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int Size; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        private const int HidPStatusSuccess = 0x110000;
        private const int HidPInput = 0, HidPFeature = 2;

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll")]
        private static extern void SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(IntPtr file, [In, Out] HIDD_ATTRIBUTES attrs);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(IntPtr file, StringBuilder s, uint len);
        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr file, out IntPtr preparsed);
        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);
        [DllImport("hid.dll")]
        private static extern int HidP_GetValueCaps(IntPtr preparsed, int reportType, byte[] caps, ref ushort count);
        [DllImport("hid.dll")]
        private static extern int HidP_GetUsageValue(IntPtr preparsed, int reportType, byte reportId, ushort usagePage, ushort usage, out ushort value, byte[] report, uint reportLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")]
        private static extern bool ReadFile(IntPtr file, byte[] buffer, uint count, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        // HIDP_VALUE_CAPS, 48 bytes, no pointers (same on x86 and x64)
        private const int OffUsagePage = 0, OffReportId = 2, OffIsRange = 5, OffBits = 18, OffUsageMin = 32, OffUsageMax = 34;
        private const int ValueCapsSize = 48;

        public static IEnumerable<(int Percent, string Source, string? Product)> Enumerate()
        {
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12); // DIGCF_PRESENT|DIGCF_DEVICEINTERFACE
            var data = new SP_DEVICE_INTERFACE_DATA { Size = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
            for (uint i = 0; ; i++)
            {
                Guid g = hidGuid;
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data)) break;
                string? path = DevicePath(set, ref data);
                if (path == null) continue;

                // GENERIC_READ only: this is a read-only probe, no reports are ever sent
                IntPtr file = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (file == (IntPtr)(-1)) continue;
                try
                {
                    if (!HidD_GetPreparsedData(file, out IntPtr pre) || pre == IntPtr.Zero) continue;
                    try
                    {
                        var caps = new HIDP_CAPS();
                        if (HidP_GetCaps(pre, ref caps) != HidPStatusSuccess) continue;
                        var sb = new StringBuilder(256);
                        HidD_GetProductString(file, sb, 256);
                        string product = sb.ToString();

                        foreach (int reportType in new[] { HidPInput, HidPFeature })
                        {
                            ushort count = 0;
                            if (HidP_GetValueCaps(pre, reportType, Array.Empty<byte>(), ref count) != HidPStatusSuccess || count == 0) continue;
                            byte[] buf = new byte[ValueCapsSize * count];
                            ushort c = count;
                            if (HidP_GetValueCaps(pre, reportType, buf, ref c) != HidPStatusSuccess) continue;
                            for (int k = 0; k < c; k++)
                            {
                                byte[] cap = new byte[ValueCapsSize];
                                Array.Copy(buf, k * ValueCapsSize, cap, 0, ValueCapsSize);
                                if (BitConverter.ToUInt16(cap, OffUsagePage) != PageBattery) continue;
                                bool range = BitConverter.ToUInt16(cap, OffIsRange) != 0;
                                byte rid = cap[OffReportId];
                                ushort bits = BitConverter.ToUInt16(cap, OffBits);
                                if (range)
                                {
                                    ushort lo = BitConverter.ToUInt16(cap, OffUsageMin), hi = BitConverter.ToUInt16(cap, OffUsageMax);
                                    if (lo != UsageStateOfCharge && hi < UsageStateOfCharge) continue;
                                }
                                else if (BitConverter.ToUInt16(cap, OffUsageMin) != UsageStateOfCharge) continue;

                                // Read the report: for feature reports use HidD_GetFeature via HidP on a
                                // feature buffer; input reports are read with a blocking ReadFile — skip
                                // those (a headset may never send one); only take feature reports, which
                                // is what dongles like Logitech use, plus unsolicited input already cached.
                                int len = reportType == HidPFeature ? caps.FeatureReportByteLength : caps.InputReportByteLength;
                                if (len <= 0) continue;
                                byte[] report = new byte[len];
                                if (reportType == HidPFeature)
                                {
                                    if (!ReadFeature(file, report)) continue;
                                }
                                else
                                {
                                    if (!TryReadInput(file, report)) continue;
                                }
                                if (HidP_GetUsageValue(pre, reportType, rid, PageBattery, UsageStateOfCharge, out ushort value, report, (uint)len) != HidPStatusSuccess) continue;
                                if (value is > 0 and <= 100)
                                    yield return (value, "HID", product);
                            }
                        }
                    }
                    finally { if (pre != IntPtr.Zero) HidD_FreePreparsedData(pre); }
                }
                finally { CloseHandle(file); }
            }
            SetupDiDestroyDeviceInfoList(set);
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(IntPtr file, byte[] buffer, uint length);

        private static bool ReadFeature(IntPtr file, byte[] report) =>
            report.Length > 0 && HidD_GetFeature(file, report, (uint)report.Length);

        private static bool TryReadInput(IntPtr file, byte[] report)
        {
            // Non-blocking input read is not possible with a plain ReadFile on a HID
            // handle opened without overlapped; instead of hanging on devices that
            // never report, skip input reports entirely — feature reports cover the
            // dongles that support the standard battery page.
            return false;
        }

        private static string? DevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
        {
            SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
            if (needed == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, needed, out _, IntPtr.Zero)) return null;
                // The path begins at offset 4 on both x86 and x64 (WCHAR array aligns to 2)
                foreach (int off in new[] { 4, 6, 8 })
                {
                    string? s = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + off));
                    if (s != null && s.StartsWith("\\\\?\\")) return s;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
