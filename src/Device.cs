// Device page: what Lenovo Vantage shows under Device details, Audio and Network, read with Windows
// APIs only (WMI, Core Audio, WLAN API). Native ARM64, no service, no network: the only connection is
// the Lenovo product lookup started by the WARRANTY / PARTS / SUPPORT buttons.
using System;
using System.Collections.Generic;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace SnapControl
{
    // Model, serial, firmware and components. Rows are { Italian label, value, English label }:
    // the window switches language without rereading WMI.
    static class DeviceInfo
    {
        static string Q(string ns, string query, string prop)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(ns, query))
                using (var res = s.Get())
                    foreach (ManagementObject o in res) using (o) return (Convert.ToString(o[prop]) ?? "").Trim();
            }
            catch { }
            return "";
        }

        static string headline;

        // Top bar text, e.g. "YOGA SLIM 7 14Q8X9  ·  SNAPDRAGON X ELITE".
        public static string Headline
        {
            get
            {
                if (headline != null) return headline;
                string model = Q(@"root\CIMV2", "SELECT Version FROM Win32_ComputerSystemProduct", "Version");
                headline = ((model.Length > 0 ? model : "Lenovo") + "  ·  " + CpuFamily()).ToUpperInvariant();
                return headline;
            }
        }

        // "Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU" -> "Snapdragon X Elite"
        public static string CpuFamily()
        {
            string cpu = Q(@"root\CIMV2", "SELECT Name FROM Win32_Processor", "Name").Replace("(R)", "").Replace("(TM)", "");
            int dash = cpu.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) cpu = cpu.Substring(0, dash);
            return cpu.Trim();
        }

        public static string Serial() { return Q(@"root\CIMV2", "SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"); }

        static string Wmi(ManagementObject o, string p) { try { return (Convert.ToString(o[p]) ?? "").Trim(); } catch { return ""; } }

        // WmiMonitorID strings are arrays of UInt16 characters, zero-padded.
        static string Chars(object o)
        {
            var a = o as ushort[];
            if (a == null) return "";
            var sb = new StringBuilder();
            foreach (ushort c in a) if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
            return sb.ToString().Trim();
        }

        public static List<string[]> Rows(string lenovoModel)
        {
            var rows = new List<string[]>();
            string version = Q(@"root\CIMV2", "SELECT Version FROM Win32_ComputerSystemProduct", "Version");
            string type = Q(@"root\CIMV2", "SELECT Model FROM Win32_ComputerSystem", "Model");
            rows.Add(new[] { "Modello", (Q(@"root\CIMV2", "SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer") + " " + version).Trim(), "Model" });
            rows.Add(new[] { "Tipo macchina", type + (lenovoModel != null ? "  ·  " + lenovoModel + " (Lenovo)" : ""), "Machine type" });
            rows.Add(new[] { "Numero di serie", Serial(), "Serial number" });

            string date = "";
            try
            {
                string raw = Q(@"root\CIMV2", "SELECT ReleaseDate FROM Win32_BIOS", "ReleaseDate");
                if (raw.Length > 0) date = "  ·  " + ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd");
            }
            catch { }
            rows.Add(new[] { "BIOS", Q(@"root\CIMV2", "SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion") + date, "BIOS" });
            rows.Add(new[] { "Scheda madre", (Q(@"root\CIMV2", "SELECT Product FROM Win32_BaseBoard", "Product") + "  ·  SN " + Q(@"root\CIMV2", "SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber")).Trim(), "Mainboard" });

            double ram = 0;
            double.TryParse(Q(@"root\CIMV2", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory"), out ram);
            string ramSpeed = Q(@"root\CIMV2", "SELECT Speed FROM Win32_PhysicalMemory", "Speed");
            rows.Add(new[] { "Memoria", string.Format("{0:0} GB", Math.Round(ram / (1024.0 * 1024 * 1024))) + (ramSpeed.Length > 0 ? "  ·  " + ramSpeed + " MT/s" : ""), "Memory" });

            try
            {
                using (var s = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Model, Size FROM Win32_DiskDrive"))
                using (var res = s.Get())
                    foreach (ManagementObject o in res)
                        using (o)
                        {
                            double gb = 0;
                            double.TryParse(Wmi(o, "Size"), out gb);
                            rows.Add(new[] { "Disco", Wmi(o, "Model") + string.Format("  ·  {0:0} GB", gb / 1e9), "Storage" });
                        }
            }
            catch { }

            try
            {
                using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorID"))
                using (var res = s.Get())
                    foreach (ManagementObject o in res)
                        using (o)
                        {
                            string made = Chars(o["ManufacturerName"]), code = Chars(o["ProductCodeID"]), name = Chars(o["UserFriendlyName"]);
                            string year = Wmi(o, "YearOfManufacture");
                            var dm = new Native.DEVMODE { Size = (short)Marshal.SizeOf(typeof(Native.DEVMODE)) };
                            string mode = Native.EnumDisplaySettings(null, -1, ref dm) ? "  ·  " + dm.PelsWidth + "×" + dm.PelsHeight + " @ " + dm.DisplayFrequency + " Hz" : "";
                            rows.Add(new[] { "Schermo", (made + code + " " + name).Trim() + (year.Length > 0 ? "  ·  " + year : "") + mode, "Display" });
                        }
            }
            catch { }

            var bat = BatteryInfo.Read();
            if (bat.DesignWh > 0)
                rows.Add(new[] { "Batteria", ((bat.Maker + " " + bat.Model).Trim() + string.Format("  ·  {0:0} Wh", bat.DesignWh)).Trim(), "Battery" });

            return rows;
        }
    }

    // ---------------------------------------------------------------------------------------- audio

    // Default and active endpoints through Core Audio (the same API as the Windows volume flyout).
    static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid Fmt; public int Pid; }

        // PROPVARIANT: 8-byte header and a 16-byte union on 64-bit Windows (ARM64 included).
        [StructLayout(LayoutKind.Sequential)]
        struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr P1, P2; }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant v);

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        public const int Render = 0, Capture = 1;
        const int Active = 1, ClsCtxAll = 23, Multimedia = 1;
        static readonly PropertyKey FriendlyName = new PropertyKey { Fmt = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
        static Guid VolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        public sealed class Endpoint
        {
            public string Id, Name;
            public int Flow;
            public bool Default, Mute;
            public int Volume = -1;              // 0..100, -1 = unreadable
            public List<int> Channels = new List<int>();
        }

        static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }

        static void Release(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }

        static IMMDeviceEnumerator Enumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCo(); }

        static string Name(IMMDevice d)
        {
            IPropertyStore store;
            if (d.OpenPropertyStore(0, out store) != 0 || store == null) return "?";
            try
            {
                var key = FriendlyName;
                PropVariant v;
                if (store.GetValue(ref key, out v) != 0) return "?";
                try { return v.Vt == 31 ? Marshal.PtrToStringUni(v.P1) : "?"; }   // VT_LPWSTR
                finally { PropVariantClear(ref v); }
            }
            finally { Release(store); }
        }

        static IAudioEndpointVolume Volume(IMMDevice d)
        {
            object o;
            Check(d.Activate(ref VolumeIid, ClsCtxAll, IntPtr.Zero, out o));
            return (IAudioEndpointVolume)o;
        }

        static int Pct(float f) { return (int)Math.Round(Math.Max(0f, Math.Min(1f, f)) * 100); }

        // Active endpoints, default first for each direction.
        public static List<Endpoint> Read()
        {
            var res = new List<Endpoint>();
            var en = Enumerator();
            try
            {
                foreach (int flow in new[] { Render, Capture })
                {
                    string def = null;
                    IMMDevice dd;
                    if (en.GetDefaultAudioEndpoint(flow, Multimedia, out dd) == 0 && dd != null) { dd.GetId(out def); Release(dd); }
                    IMMDeviceCollection col;
                    if (en.EnumAudioEndpoints(flow, Active, out col) != 0 || col == null) continue;
                    try
                    {
                        uint n;
                        col.GetCount(out n);
                        var list = new List<Endpoint>();
                        for (uint i = 0; i < n; i++)
                        {
                            IMMDevice d;
                            if (col.Item(i, out d) != 0 || d == null) continue;
                            try
                            {
                                var e = new Endpoint { Flow = flow, Name = Name(d) };
                                d.GetId(out e.Id);
                                e.Default = e.Id == def;
                                try
                                {
                                    var v = Volume(d);
                                    try
                                    {
                                        float f;
                                        if (v.GetMasterVolumeLevelScalar(out f) == 0) e.Volume = Pct(f);
                                        v.GetMute(out e.Mute);
                                        uint ch;
                                        if (v.GetChannelCount(out ch) == 0)
                                            for (uint c = 0; c < ch && c < 8; c++)
                                                if (v.GetChannelVolumeLevelScalar(c, out f) == 0) e.Channels.Add(Pct(f));
                                    }
                                    finally { Release(v); }
                                }
                                catch { }
                                list.Add(e);
                            }
                            finally { Release(d); }
                        }
                        list.Sort((a, b) => a.Default == b.Default ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) : a.Default ? -1 : 1);
                        res.AddRange(list);
                    }
                    finally { Release(col); }
                }
            }
            finally { Release(en); }
            return res;
        }

        static void With(string id, Action<IAudioEndpointVolume> act)
        {
            var en = Enumerator();
            IMMDevice d = null;
            try
            {
                Check(en.GetDevice(id, out d));
                var v = Volume(d);
                try { act(v); }
                finally { Release(v); }
            }
            finally { Release(d); Release(en); }
        }

        public static void SetVolume(string id, int pct)
        {
            With(id, v => { Guid ctx = Guid.Empty; Check(v.SetMasterVolumeLevelScalar(pct / 100f, ref ctx)); });
        }

        public static void SetMute(string id, bool mute)
        {
            With(id, v => { Guid ctx = Guid.Empty; Check(v.SetMute(mute, ref ctx)); });
        }

        // Left and right back to the same level: an off-center balance makes a working speaker sound dead.
        public static void Center(string id)
        {
            With(id, v =>
            {
                Guid ctx = Guid.Empty;
                uint ch;
                Check(v.GetChannelCount(out ch));
                float max = 0, f;
                for (uint c = 0; c < ch; c++) if (v.GetChannelVolumeLevelScalar(c, out f) == 0) max = Math.Max(max, f);
                for (uint c = 0; c < ch; c++) Check(v.SetChannelVolumeLevelScalar(c, 1f, ref ctx));
                Check(v.SetMasterVolumeLevelScalar(max, ref ctx));
            });
        }

        // Noise bursts on one channel (0 = left, 1 = right, -1 = both): easier to locate than a tone on small speakers.
        public static void Test(int channel)
        {
            const int rate = 44100, bursts = 4;
            int on = rate * 3 / 10, off = rate / 5, n = (on + off) * bursts;
            var ms = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(ms);
            bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + n * 4); bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(n * 4);
            var rnd = new Random(7);
            double last = 0;
            for (int i = 0; i < n; i++)
            {
                int t = i % (on + off);
                double env = t < on ? Math.Min(1.0, Math.Min(t / 400.0, (on - t) / 800.0)) : 0;
                last = 0.6 * last + 0.4 * (rnd.NextDouble() * 2 - 1);
                short s = (short)(16000 * last * env);
                bw.Write(channel == 1 ? (short)0 : s);
                bw.Write(channel == 0 ? (short)0 : s);
            }
            bw.Flush();
            ms.Position = 0;
            using (var p = new System.Media.SoundPlayer(ms)) p.PlaySync();
        }

        // Audio processing that Vantage controls through its own add-ins: here only whether it is running.
        public static List<string[]> Processing()
        {
            var rows = new List<string[]>();
            var names = new Dictionary<string, string> { { "DolbyDAXAPI", "Dolby Atmos" }, { "ElevocService", "Elevoc noise cancellation" } };
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, State FROM Win32_Service WHERE Name = 'DolbyDAXAPI' OR Name = 'ElevocService'"))
                using (var res = s.Get())
                    foreach (ManagementObject o in res)
                        using (o)
                        {
                            string n = Convert.ToString(o["Name"]), st = Convert.ToString(o["State"]);
                            bool run = st == "Running";
                            rows.Add(new[] { names[n], run ? "Attivo" : "Fermo (" + st + ")", names[n], run ? "Running" : "Stopped (" + st + ")" });
                        }
            }
            catch { }
            return rows;
        }
    }

    // ---------------------------------------------------------------------------------------- network

    // Wi-Fi through the WLAN API (wlanapi.dll), addresses through System.Net. Offsets follow the
    // documented WLAN structures; a read that fails leaves the field empty.
    static class NetInfo
    {
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")] static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool secure, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr mem);

        public sealed class Wifi
        {
            public string Adapter = "", State = "", Ssid = "", Bssid = "", Standard = "", Security = "";
            public int Signal = -1, Rssi = 0, Channel = 0, Rx = 0, Tx = 0;
            public double FreqGhz = 0;
            public bool Connected;
        }

        static readonly string[] States = { "not ready", "connected", "ad hoc", "disconnecting", "disconnected", "associating", "discovering", "authenticating" };

        static string Phy(int p)
        {
            switch (p)
            {
                case 4: return "802.11a";
                case 5: return "802.11b";
                case 6: return "802.11g";
                case 7: return "Wi-Fi 4 · 802.11n";
                case 8: return "Wi-Fi 5 · 802.11ac";
                case 10: return "Wi-Fi 6 · 802.11ax";
                case 11: return "Wi-Fi 7 · 802.11be";
                default: return "PHY " + p;
            }
        }

        static string Auth(int a)
        {
            switch (a)
            {
                case 1: return "Open";
                case 2: return "WEP";
                case 3: return "WPA";
                case 4: return "WPA-Personal";
                case 6: return "WPA2-Enterprise";
                case 7: return "WPA2-Personal";
                case 8: case 11: return "WPA3-Enterprise";
                case 9: return "WPA3-Personal";
                case 10: return "OWE";
                default: return "auth " + a;
            }
        }

        static string Mac(IntPtr p, int off)
        {
            var b = new string[6];
            for (int i = 0; i < 6; i++) b[i] = Marshal.ReadByte(p, off + i).ToString("X2");
            return string.Join(":", b);
        }

        public static List<Wifi> ReadWifi()
        {
            var res = new List<Wifi>();
            IntPtr h, list;
            uint ver;
            if (WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0) return res;
            try
            {
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return res;
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < count; i++)
                    {
                        // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] + state (4) = 532 bytes, list header 8 bytes
                        IntPtr item = new IntPtr(list.ToInt64() + 8 + i * 532);
                        Guid id = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                        var w = new Wifi { Adapter = Marshal.PtrToStringUni(new IntPtr(item.ToInt64() + 16)) };
                        int st = Marshal.ReadInt32(item, 16 + 512);
                        w.State = st >= 0 && st < States.Length ? States[st] : st.ToString();
                        w.Connected = st == 1;
                        if (w.Connected) ReadConnection(h, id, w);
                        res.Add(w);
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
            return res;
        }

        static void ReadConnection(IntPtr h, Guid id, Wifi w)
        {
            int size;
            IntPtr p;
            // wlan_intf_opcode_current_connection = 7: WLAN_CONNECTION_ATTRIBUTES
            if (WlanQueryInterface(h, ref id, 7, IntPtr.Zero, out size, out p, IntPtr.Zero) == 0)
                try
                {
                    const int a = 520;                                     // WLAN_ASSOCIATION_ATTRIBUTES
                    int len = Math.Min(32, Marshal.ReadInt32(p, a));
                    var ssid = new byte[len];
                    Marshal.Copy(new IntPtr(p.ToInt64() + a + 4), ssid, 0, len);
                    w.Ssid = Encoding.UTF8.GetString(ssid);
                    w.Bssid = Mac(p, a + 40);
                    w.Standard = Phy(Marshal.ReadInt32(p, a + 48));
                    w.Signal = Marshal.ReadInt32(p, a + 56);
                    w.Rx = Marshal.ReadInt32(p, a + 60) / 1000;            // kbps -> Mbps
                    w.Tx = Marshal.ReadInt32(p, a + 64) / 1000;
                    if (size >= a + 68 + 16 && Marshal.ReadInt32(p, a + 68) != 0) w.Security = Auth(Marshal.ReadInt32(p, a + 76));
                    else w.Security = "Open";
                }
                finally { WlanFreeMemory(p); }
            // wlan_intf_opcode_channel_number = 8
            if (WlanQueryInterface(h, ref id, 8, IntPtr.Zero, out size, out p, IntPtr.Zero) == 0)
                try { w.Channel = Marshal.ReadInt32(p); } finally { WlanFreeMemory(p); }
            // wlan_intf_opcode_rssi = 0x10000102
            if (WlanQueryInterface(h, ref id, 0x10000102, IntPtr.Zero, out size, out p, IntPtr.Zero) == 0)
                try { w.Rssi = Marshal.ReadInt32(p); } finally { WlanFreeMemory(p); }
            // centre frequency of the connected access point, from the cached scan: tells 5 GHz from 6 GHz
            IntPtr bss;
            if (WlanGetNetworkBssList(h, ref id, IntPtr.Zero, 3, false, IntPtr.Zero, out bss) == 0)
                try
                {
                    int n = Marshal.ReadInt32(bss, 4);
                    for (int i = 0; i < n; i++)
                    {
                        long e = bss.ToInt64() + 8 + (long)i * 360;            // WLAN_BSS_ENTRY = 360 bytes
                        if (Mac(new IntPtr(e), 40) != w.Bssid) continue;
                        w.FreqGhz = Marshal.ReadInt32(new IntPtr(e), 92) / 1e6;  // kHz
                        break;
                    }
                }
                finally { WlanFreeMemory(bss); }
        }

        public static string Band(Wifi w)
        {
            if (w.FreqGhz > 5.9) return "6 GHz";
            if (w.FreqGhz > 4.9) return "5 GHz";
            if (w.FreqGhz > 2.3) return "2.4 GHz";
            if (w.Channel > 0 && w.Channel <= 14) return "2.4 GHz";
            return w.Channel > 14 ? "5/6 GHz" : "";
        }

        // Adapters that are up, without loopback and tunnels: { name, details }.
        public static List<string[]> Interfaces()
        {
            var rows = new List<string[]>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var ip = ni.GetIPProperties();
                var v4 = new List<string>();
                string v6 = "";
                foreach (var a in ip.UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) v4.Add(a.Address + "/" + a.PrefixLength);
                    else if (v6.Length == 0 && !a.Address.IsIPv6LinkLocal) v6 = a.Address.ToString();
                string gw = "";
                foreach (var g in ip.GatewayAddresses) if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { gw = g.Address.ToString(); break; }
                var dns = new List<string>();
                foreach (var d in ip.DnsAddresses) if (d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) dns.Add(d.ToString());
                var mac = ni.GetPhysicalAddress().GetAddressBytes();
                var parts = new List<string>();
                if (v4.Count > 0) parts.Add(string.Join(", ", v4));
                if (v6.Length > 0) parts.Add(v6);
                if (gw.Length > 0) parts.Add("GW " + gw);
                if (dns.Count > 0) parts.Add("DNS " + string.Join(", ", dns));
                if (mac.Length == 6) parts.Add("MAC " + BitConverter.ToString(mac).Replace('-', ':'));
                rows.Add(new[] { ni.Name, ni.Description + (ni.Speed > 0 ? string.Format("  ·  {0:0} Mbps", ni.Speed / 1e6) : ""), string.Join("  ·  ", parts) });
            }
            return rows;
        }

        // Bluetooth adapters and their Device Manager status.
        public static List<string> Bluetooth()
        {
            var res = new List<string>();
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'"))
                using (var r = s.Get())
                    foreach (ManagementObject o in r)
                        using (o)
                        {
                            string n = Convert.ToString(o["Name"]) ?? "";
                            if (n.IndexOf("Adapter", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Radio", StringComparison.OrdinalIgnoreCase) >= 0)
                                res.Add(n + "  ·  " + Convert.ToString(o["Status"]));
                        }
            }
            catch { }
            return res;
        }

        // Wi-Fi power saving in the active power plan (0 = maximum performance … 3 = maximum saving).
        public static uint WifiSaving(bool ac)
        {
            return Soc.Read(new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1"), new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a"), ac);
        }
    }
}

namespace SnapControl
{
    // ---------------------------------------------------------------------------------------- color

    // Factory-calibrated panel profiles (TPLCD_<panel>_<name>.icm, installed with X-Rite Color Assistant) set as the
    // display default through the Windows color APIs, the same switch X-Rite makes. Windows then loads the calibration
    // itself (ICM\Calibration\CalibrationManagementEnabled), so X-Rite does not need to start with Windows.
    static class ColorProfiles
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint Low; public int High; }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] static extern int ColorProfileAddDisplayAssociation(int scope, string profile, LUID adapter, uint source, [MarshalAs(UnmanagedType.Bool)] bool setDefault, [MarshalAs(UnmanagedType.Bool)] bool advanced);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string profile, int type, int subtype, LUID adapter, uint source);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] static extern int ColorProfileRemoveDisplayAssociation(int scope, string profile, LUID adapter, uint source, [MarshalAs(UnmanagedType.Bool)] bool advanced);
        [DllImport("mscms.dll")] static extern int ColorProfileGetDisplayDefault(int scope, LUID adapter, uint source, int type, int subtype, out IntPtr name);
        [DllImport("mscms.dll")] static extern int ColorProfileGetDisplayUserScope(LUID adapter, uint source, out int scope);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool InstallColorProfileW(string machine, string profile);

        const int CptIcc = 0, CpstStandard = 7, CpstExtended = 8;

        public sealed class State
        {
            public string Panel = "", Code = "", Sdr = "", Advanced = "", Error;
            public bool AdvancedSupported, AdvancedOn, PerUser;
            public List<string> Files = new List<string>();     // file names, e.g. TPLCD_8AC2_sRGB.icm
            public int Saved;                                   // profiles in the backup
            public List<string> Missing = new List<string>();   // in the backup, no longer installed
        }

        // Copy of the factory profiles kept by SnapControl: X-Rite's uninstaller may remove the originals.
        public static string BackupDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"SnapControl\color"); } }

        static readonly System.Text.RegularExpressions.Regex FileRx = new System.Text.RegularExpressions.Regex(@"^TPLCD_[0-9A-Fa-f]{4}_[A-Za-z0-9]+(\.icm|_Quality\.txt)$");

        // Saves the panel's profiles and quality reports that are not saved yet (or changed).
        static void Backup(string code)
        {
            string[] src = System.IO.Directory.GetFiles(Dir, "TPLCD_" + code + "_*");
            if (src.Length == 0) return;
            System.IO.Directory.CreateDirectory(BackupDir);
            foreach (string f in src)
            {
                string name = System.IO.Path.GetFileName(f), dst = System.IO.Path.Combine(BackupDir, name);
                if (!FileRx.IsMatch(name)) continue;
                if (System.IO.File.Exists(dst) && new System.IO.FileInfo(dst).Length == new System.IO.FileInfo(f).Length) continue;
                System.IO.File.Copy(f, dst, true);
            }
        }

        // ICC header: "acsp" at byte 36, declared size equal to the file size.
        static bool IccValid(string path)
        {
            byte[] b = System.IO.File.ReadAllBytes(path);
            if (b.Length < 132 || b.Length > 1 << 20 || Encoding.ASCII.GetString(b, 36, 4) != "acsp") return false;
            long size = ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
            return size == b.Length;
        }

        // Elevated (--color-restore): installs back the saved profiles that are missing. Only TPLCD_*.icm files
        // with a valid ICC header are installed, through the Windows API that copies them into the color folder.
        public static int Restore(string dir)
        {
            int n = 0;
            foreach (string f in System.IO.Directory.GetFiles(dir, "TPLCD_*"))
            {
                string name = System.IO.Path.GetFileName(f), dst = System.IO.Path.Combine(Dir, name);
                if (!FileRx.IsMatch(name) || System.IO.File.Exists(dst)) continue;
                if (name.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IccValid(f)) throw new Exception(L.T("not a valid ICC profile: ", "profilo ICC non valido: ") + name);
                    if (!InstallColorProfileW(null, f)) throw new System.ComponentModel.Win32Exception();
                    n++;
                }
                else if (new System.IO.FileInfo(f).Length < 1 << 20) System.IO.File.Copy(f, dst);
            }
            return n;
        }

        static string Dir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color"); } }

        // The built-in panel: path whose target is internal/embedded, else the first active one.
        static bool Target(out LUID adapter, out uint source, out LUID tAdapter, out uint target)
        {
            adapter = tAdapter = new LUID(); source = target = 0;
            uint np, nm;
            if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0 || np == 0) return false;   // QDC_ONLY_ACTIVE_PATHS
            IntPtr paths = Marshal.AllocHGlobal((int)np * 72), modes = Marshal.AllocHGlobal((int)nm * 64);
            try
            {
                if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0 || np == 0) return false;
                int pick = 0;
                for (int i = 0; i < np; i++)
                {
                    uint tech = (uint)Marshal.ReadInt32(paths, i * 72 + 36);   // targetInfo.outputTechnology
                    if (tech == 0x80000000 || tech == 11 || tech == 13) { pick = i; break; }   // internal, eDP, UDI embedded
                }
                int o = pick * 72;
                adapter = new LUID { Low = (uint)Marshal.ReadInt32(paths, o), High = Marshal.ReadInt32(paths, o + 4) };
                source = (uint)Marshal.ReadInt32(paths, o + 8);
                tAdapter = new LUID { Low = (uint)Marshal.ReadInt32(paths, o + 20), High = Marshal.ReadInt32(paths, o + 24) };
                target = (uint)Marshal.ReadInt32(paths, o + 28);
                return true;
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        }

        static string Default(int scope, LUID a, uint s, int subtype)
        {
            IntPtr p;
            if (ColorProfileGetDisplayDefault(scope, a, s, CptIcc, subtype, out p) != 0 || p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { LocalFree(p); }
        }

        // e.g. "LEN8AC2" -> code "8AC2"
        static string PanelId()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName FROM WmiMonitorID"))
                using (var res = s.Get())
                    foreach (ManagementObject o in res)
                        using (o)
                        {
                            string inst = Convert.ToString(o["InstanceName"]) ?? "";   // DISPLAY\LEN8AC2\...
                            string[] p = inst.Split('\\');
                            if (p.Length > 1 && p[1].Length >= 7) return p[1].ToUpperInvariant();
                        }
            }
            catch { }
            return "";
        }

        public static State Read()
        {
            var st = new State { Panel = PanelId() };
            st.Code = st.Panel.Length >= 7 ? st.Panel.Substring(3) : "";
            try
            {
                if (st.Code.Length > 0)
                {
                    foreach (string f in System.IO.Directory.GetFiles(Dir, "TPLCD_" + st.Code + "_*.icm"))
                        st.Files.Add(System.IO.Path.GetFileName(f));
                    try { Backup(st.Code); } catch { }
                    if (System.IO.Directory.Exists(BackupDir))
                        foreach (string f in System.IO.Directory.GetFiles(BackupDir, "TPLCD_" + st.Code + "_*.icm"))
                        {
                            st.Saved++;
                            string name = System.IO.Path.GetFileName(f);
                            if (!st.Files.Exists(x => x.Equals(name, StringComparison.OrdinalIgnoreCase))) st.Missing.Add(name);
                        }
                }
                st.Files.Sort(StringComparer.OrdinalIgnoreCase);

                LUID a, ta; uint s, t;
                if (!Target(out a, out s, out ta, out t)) { st.Error = "QueryDisplayConfig"; return st; }
                int scope;
                if (ColorProfileGetDisplayUserScope(a, s, out scope) != 0) scope = 1;
                st.PerUser = scope == 1;
                st.Sdr = Default(scope, a, s, CpstStandard);
                st.Advanced = Default(scope, a, s, CpstExtended);

                ReadAdvanced(st);
            }
            catch (Exception ex) { st.Error = ex.Message; }
            return st;
        }

        static void ReadAdvanced(State st)
        {
            LUID a, ta; uint s, t;
            if (!Target(out a, out s, out ta, out t)) return;
            // DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (type 9): header 20 bytes + value + encoding + bits
            IntPtr pk = Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(pk, i, 0);
                Marshal.WriteInt32(pk, 0, 9);
                Marshal.WriteInt32(pk, 4, 32);
                Marshal.WriteInt32(pk, 8, (int)ta.Low);
                Marshal.WriteInt32(pk, 12, ta.High);
                Marshal.WriteInt32(pk, 16, (int)t);
                if (DisplayConfigGetDeviceInfo(pk) == 0)
                {
                    int v = Marshal.ReadInt32(pk, 20);
                    st.AdvancedSupported = (v & 1) != 0;
                    st.AdvancedOn = (v & 2) != 0;
                }
            }
            finally { Marshal.FreeHGlobal(pk); }
        }

        // Sets the profile as the display default; also for advanced color when Windows uses it on this panel.
        public static void Set(string file)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(file, @"^TPLCD_[0-9A-Fa-f]{4}_[A-Za-z0-9]+\.icm$") || !System.IO.File.Exists(System.IO.Path.Combine(Dir, file)))
                throw new ArgumentException(file);
            LUID a, ta; uint s, t;
            if (!Target(out a, out s, out ta, out t)) throw new Exception("QueryDisplayConfig");
            int scope;
            if (ColorProfileGetDisplayUserScope(a, s, out scope) != 0) scope = 1;
            // Add only associates a profile that is already associated: it does not make it the default again.
            // SetDisplayDefaultAssociation does; if Windows still reports another default, re-associate from scratch.
            ColorProfileAddDisplayAssociation(scope, file, a, s, true, false);
            Marshal.ThrowExceptionForHR(ColorProfileSetDisplayDefaultAssociation(scope, file, CptIcc, CpstStandard, a, s));
            if (!Default(scope, a, s, CpstStandard).Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                ColorProfileRemoveDisplayAssociation(scope, file, a, s, false);
                Marshal.ThrowExceptionForHR(ColorProfileAddDisplayAssociation(scope, file, a, s, true, false));
            }
            if (AdvancedOn())
            {
                ColorProfileAddDisplayAssociation(scope, file, a, s, true, true);
                Marshal.ThrowExceptionForHR(ColorProfileSetDisplayDefaultAssociation(scope, file, CptIcc, CpstExtended, a, s));
            }
            string now = Default(scope, a, s, CpstStandard);
            if (!now.Equals(file, StringComparison.OrdinalIgnoreCase))
                throw new Exception(L.T("Windows still uses ", "Windows usa ancora ") + (now.Length > 0 ? now : "?"));
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Settings.Key)) k.SetValue("ColorProfile", file); } catch { }
        }

        static bool AdvancedOn() { var st = new State(); ReadAdvanced(st); return st.AdvancedOn; }

        // Last profile chosen in SnapControl, set again after a restore.
        public static string Saved()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Settings.Key)) return (k == null ? null : k.GetValue("ColorProfile") as string) ?? ""; }
            catch { return ""; }
        }

        // "TPLCD_8AC2_DisplayP3.icm" -> "DisplayP3"
        public static string Mode(string file)
        {
            if (string.IsNullOrEmpty(file)) return "";
            string n = System.IO.Path.GetFileNameWithoutExtension(file);
            int i = n.IndexOf('_', 6);
            return i > 0 ? n.Substring(i + 1) : n;
        }

        public static string Label(string mode)
        {
            switch (mode.ToLowerInvariant())
            {
                case "srgb": return "sRGB";
                case "displayp3": return "DISPLAY P3";
                case "dcip3": return "DCI-P3";
                case "adobergb": return "ADOBE RGB";
                case "rec709": return "REC.709";
                case "default": return L.T("DEFAULT", "PREDEFINITO");
                case "native": return L.T("NATIVE", "NATIVO");
                default: return mode.ToUpperInvariant();
            }
        }

        // ------------------------------------------------ X-Rite Color Assistant at sign-in

        public const string Task = "XRiteColorAssistantStartup";

        // true / false, null if the task does not exist or cannot be read
        public static bool? XRiteAtStartup()
        {
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                dynamic t = svc.GetFolder("\\").GetTask(Task);
                return (bool)t.Enabled;
            }
            catch { return null; }
        }

        // The task belongs to the installer (SYSTEM): changing it needs administrator rights.
        public static void SetXRiteAtStartup(bool on)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Change /TN \"" + Task + "\" " + (on ? "/ENABLE" : "/DISABLE"))
            { Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using (var p = System.Diagnostics.Process.Start(psi)) p.WaitForExit();
            if (!on)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("XRiteColorAssistant"))
                    try { p.Kill(); } catch { } finally { p.Dispose(); }
        }

        // ------------------------------------------------ command line (tests without the window)

        public static int Cli(string[] a)
        {
            if (a.Length == 2 && a[0] == "--color-restore")
            {
                // elevated process started by the window
                try { Restore(a[1]); return 0; }
                catch (Exception ex) { System.Windows.Forms.MessageBox.Show("Profili colore: " + ex.Message, "SnapControl"); return 1; }
            }
            var sb = new StringBuilder();
            int code = 0;
            try
            {
                if (a.Length == 2 && a[0] == "--color-set") Set(a[1]);
                else if (a.Length != 1) return 1;
                var st = Read();
                sb.Append("pannello " + st.Panel + " · per utente " + st.PerUser + " · colore avanzato supportato " + st.AdvancedSupported + " attivo " + st.AdvancedOn + "\n");
                sb.Append("predefinito SDR: " + st.Sdr + "\npredefinito avanzato: " + st.Advanced + "\n");
                foreach (var f in st.Files) sb.Append((f.Equals(st.Sdr, StringComparison.OrdinalIgnoreCase) ? "* " : "  ") + f + "  (" + Label(Mode(f)) + ")\n");
                sb.Append("copia di sicurezza: " + st.Saved + " profili in " + BackupDir + (st.Missing.Count > 0 ? " · mancanti: " + string.Join(", ", st.Missing) : "") + "\n");
                var x = XRiteAtStartup();
                sb.Append("X-Rite all'avvio: " + (x == null ? "attivita' assente" : x.Value ? "si" : "no") + "\n");
                if (st.Error != null) { sb.Append("errore: " + st.Error + "\n"); code = 2; }
            }
            catch (Exception ex) { sb.Append("errore: " + ex.Message + "\n"); code = 1; }
            try { PowerSettings.Send(PowerSettings.StdOut, sb.ToString()); } catch { return 1; }
            return code;
        }
    }
}
