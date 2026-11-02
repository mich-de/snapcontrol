// SnapControl - lightweight control panel for Lenovo IdeaPad Slim 5 14Q8X9 (Snapdragon X Plus, ARM64).
// No networking, except the user-started BIOS/driver search (Windows Update, Lenovo API, Microsoft catalog). No telemetry.
// Built with the .NET Framework 4.8.1 csc (C# 5) shipped with Windows: runs native ARM64.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("SnapControl")]
[assembly: System.Reflection.AssemblyCompany("mich-de")]
[assembly: System.Reflection.AssemblyCopyright("mich-de - github.com/mich-de/snapcontrol")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
// Without this attribute .NET 4.8.1 starts AnyCPU apps under x64 emulation even on ARM64.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8.1", FrameworkDisplayName = ".NET Framework 4.8.1")]

namespace SnapControl
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustFileInfo
        {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string Path;
            public IntPtr File, Subject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyData, SipData;
            public uint UiChoice, Revocation, UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData, UrlReference;
            public uint ProvFlags, UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll")]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        // Authenticode check without UI: the signature matches the file, chains to a trusted root and is not revoked.
        public static bool SignatureValid(string path)
        {
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            IntPtr file = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(new WinTrustFileInfo { Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), Path = path }, file, false);
                var data = new WinTrustData { Size = (uint)Marshal.SizeOf(typeof(WinTrustData)), UiChoice = 2, Revocation = 1, UnionChoice = 1, FileInfo = file };
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure(file, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(file);
            }
        }

        [DllImport("powrprof.dll")]
        public static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
        // The GUID must be passed by reference: by value it works on x64 only by accident (structs >8 bytes
        // are passed via pointer), on ARM64 it lands in registers and the DLL reads an invalid address.
        [DllImport("powrprof.dll")]
        public static extern uint PowerSetActiveOverlayScheme(ref Guid scheme);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("powrprof.dll")]
        public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")]
        public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")]
        public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")]
        public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool GetPwrCapabilities([Out] byte[] caps);
        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr mem);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")]
        public static extern bool WriteFile(IntPtr file, byte[] data, int count, out int written, IntPtr overlapped);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion, DriverVersion, Size, DriverExtra;
            public int Fields, PositionX, PositionY, Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public int ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
    }

    // Windows power mode: on Snapdragon X it is the control recommended by Qualcomm/Microsoft
    // to balance Oryon core performance, NPU and power draw.
    static class PowerMode
    {
        public static readonly Guid Efficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        public static readonly Guid Balanced = Guid.Empty;
        public static readonly Guid Performance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        public static Guid Current()
        {
            Guid g;
            return Native.PowerGetEffectiveOverlayScheme(out g) == 0 ? g : Guid.Empty;
        }

        public static string Name(Guid g)
        {
            if (g == Efficiency) return L.T("Max efficiency", "Massima efficienza");
            if (g == Performance) return L.T("Max performance", "Prestazioni migliori");
            if (g == Balanced) return L.T("Balanced", "Bilanciata");
            return g.ToString();
        }
    }

    // Window language. Set by Main at startup: the command line always stays in Italian.
    static class L
    {
        public static bool It;
        public static string T(string en, string it) { return It ? it : en; }
    }

    static class Text
    {
        // Battery firmware strings may contain control bytes: keep printable characters only.
        public static string Clean(object o)
        {
            string s = Convert.ToString(o) ?? "";
            var sb = new StringBuilder();
            foreach (char c in s) if (c >= 0x20 && c < 0x7F) sb.Append(c);
            return sb.ToString().Trim();
        }
    }

    sealed class BatteryInfo
    {
        public int Percent = -1;
        public bool OnAc, Charging, Discharging;
        public double RateW, VoltageV, RemainingWh, FullWh, DesignWh, TempC = double.NaN;
        public int Cycles = -1;
        public string Maker = "", Model = "", Chemistry = "";

        public double WearPercent { get { return DesignWh > 0 && FullWh > 0 ? Math.Max(0, 100.0 * (1 - FullWh / DesignWh)) : double.NaN; } }

        public string State { get { return Charging ? L.T("Charging", "In carica") : OnAc ? L.T("Plugged in", "Alimentatore collegato") : L.T("On battery", "A batteria"); } }

        static ManagementObject First(string ns, string cls)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(ns, "SELECT * FROM " + cls))
                using (var res = s.Get())
                    foreach (ManagementObject o in res) return o;
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        static double U(ManagementObject o, string p) { try { return Convert.ToDouble(o[p]); } catch { return 0; } }
        static bool B(ManagementObject o, string p) { try { return Convert.ToBoolean(o[p]); } catch { return false; } }

        public static BatteryInfo Read()
        {
            var b = new BatteryInfo();
            // Read every 5 s: without Dispose the WMI handles stay open until GC.
            using (var st = First(@"root\WMI", "BatteryStatus"))
            if (st != null)
            {
                b.OnAc = B(st, "PowerOnline");
                b.Charging = B(st, "Charging");
                b.Discharging = B(st, "Discharging");
                b.RemainingWh = U(st, "RemainingCapacity") / 1000.0;
                b.VoltageV = U(st, "Voltage") / 1000.0;
                b.RateW = (b.Charging ? U(st, "ChargeRate") : -U(st, "DischargeRate")) / 1000.0;
            }
            using (var full = First(@"root\WMI", "BatteryFullChargedCapacity"))
            if (full != null) b.FullWh = U(full, "FullChargedCapacity") / 1000.0;
            using (var stat = First(@"root\WMI", "BatteryStaticData"))
            if (stat != null)
            {
                b.DesignWh = U(stat, "DesignedCapacity") / 1000.0;
                b.Maker = Text.Clean(stat["ManufactureName"]);
                b.Model = Text.Clean(stat["DeviceName"]);
                uint chem = (uint)U(stat, "Chemistry");
                b.Chemistry = Text.Clean(new string(new[] { (char)(chem & 0xFF), (char)((chem >> 8) & 0xFF), (char)((chem >> 16) & 0xFF), (char)((chem >> 24) & 0xFF) }));
            }
            using (var cyc = First(@"root\WMI", "BatteryCycleCount"))
            if (cyc != null) b.Cycles = (int)U(cyc, "CycleCount");
            using (var temp = First(@"root\WMI", "BatteryTemperature"))
            if (temp != null && U(temp, "Temperature") > 0) b.TempC = U(temp, "Temperature") / 10.0 - 273.15;
            using (var w = First(@"root\CIMV2", "Win32_Battery"))
            if (w != null) b.Percent = (int)U(w, "EstimatedChargeRemaining");
            return b;
        }

        public List<string[]> Rows(string autonomy)
        {
            return new List<string[]>
            {
                new[] { Charging ? L.T("Full charge", "Carica completa") : L.T("Time left", "Autonomia"), autonomy },
                new[] { L.T("Energy", "Energia"), string.Format(L.T("{0:0.0} Wh of {1:0.0} Wh", "{0:0.0} Wh di {1:0.0} Wh"), RemainingWh, FullWh) },
                new[] { L.T("Power", "Potenza"), RateW == 0 ? "0 W" : string.Format("{0:+0.0;-0.0} W", RateW) },
                new[] { L.T("Voltage", "Tensione"), string.Format("{0:0.00} V", VoltageV) },
                new[] { L.T("Health", "Salute"), double.IsNaN(WearPercent) ? L.T("n/a", "n/d") : string.Format(L.T("{0:0.0}% wear · {1:0.0} Wh design", "{0:0.0}% di usura · {1:0.0} Wh di progetto"), WearPercent, DesignWh) },
                new[] { L.T("Cycles", "Cicli"), Cycles >= 0 ? Cycles.ToString() : L.T("n/a", "n/d") },
                new[] { L.T("Temperature", "Temperatura"), double.IsNaN(TempC) ? L.T("n/a", "n/d") : string.Format("{0:0.0} °C", TempC) },
                new[] { L.T("Battery", "Batteria"), (Maker + " " + Model).Trim() + (Chemistry.Length > 0 ? " · " + Chemistry : "") },
            };
        }

        public string Advice()
        {
            var tips = new List<string>();
            if (OnAc && Percent >= 95 && !ChargeLimit.Enabled)
                tips.Add(L.T("Always charging to 100%: if it stays plugged in often, turn on the 80% charge limit.", "Sempre in carica al 100%: se resta spesso collegato, attiva il limite di carica all'80%."));
            if (!double.IsNaN(TempC) && TempC > 40)
                tips.Add(L.T("Battery hot (above 40 °C): avoid charging it under heavy load or on soft surfaces.", "Batteria calda (oltre 40 °C): evita di caricarla sotto carico pesante o su superfici morbide."));
            if (!OnAc && PowerMode.Current() == PowerMode.Performance)
                tips.Add(L.T("On battery in \"Max performance\" the Oryon cores stay at high frequency and battery life drops sharply.", "A batteria in «Prestazioni migliori» i core Oryon restano a frequenza alta e l'autonomia cala molto."));
            if (!double.IsNaN(WearPercent) && WearPercent > 20)
                tips.Add(L.T("Wear above 20%: consider a recalibration or a replacement.", "Usura oltre il 20%: valuta una ricalibrazione o la sostituzione."));
            return tips.Count == 0 ? L.T("Battery in good working condition.", "Batteria in buone condizioni d'uso.") : string.Join("\n", tips);
        }
    }

    // Real runtime estimate: instantaneous power swings a lot (CPU spikes, screen),
    // so use the energy actually consumed over the last minutes. Restarts from zero when
    // the state (plugged/unplugged, charging) or the power mode changes.
    sealed class Autonomy
    {
        struct Sample { public DateTime At; public double Wh, W; }

        static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        readonly Queue<Sample> samples = new Queue<Sample>();
        bool charging, discharging;
        Guid mode;

        public void Add(BatteryInfo b, Guid m)
        {
            if (b.Charging != charging || b.Discharging != discharging || m != mode) samples.Clear();
            charging = b.Charging; discharging = b.Discharging; mode = m;
            if (!charging && !discharging) return;
            var now = DateTime.UtcNow;
            samples.Enqueue(new Sample { At = now, Wh = b.RemainingWh, W = Math.Abs(b.RateW) });
            while (samples.Count > 1 && now - samples.Peek().At > Window) samples.Dequeue();
        }

        // Average power: from the energy delta if there is enough history, otherwise the mean of the readings.
        double AverageW(out TimeSpan span)
        {
            span = TimeSpan.Zero;
            if (samples.Count == 0) return 0;
            Sample first = samples.Peek(), last = first;
            double sum = 0;
            foreach (var s in samples) { sum += s.W; last = s; }
            span = last.At - first.At;
            double dWh = Math.Abs(last.Wh - first.Wh);
            if (span.TotalMinutes >= 2 && dWh >= 0.1) return dWh / span.TotalHours;
            return sum / samples.Count;
        }

        static string Hm(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0 || hours > 99) return L.T("n/a", "n/d");
            int min = (int)Math.Round(hours * 60);
            return min < 60 ? min + " min" : (min / 60) + " h " + (min % 60).ToString("00") + " min";
        }

        public string Text(BatteryInfo b)
        {
            if (!b.Charging && !b.Discharging) return b.OnAc ? L.T("Plugged in · battery not in use", "Alimentatore collegato · batteria non in uso") : L.T("n/a", "n/d");
            TimeSpan span;
            double w = AverageW(out span);
            if (w < 0.05) return L.T("calculating…", "calcolo in corso…");
            double h = b.Charging ? Math.Max(0, b.FullWh - b.RemainingWh) / w : b.RemainingWh / w;
            string basis = span.TotalMinutes >= 1
                ? string.Format(L.T("avg {0:0.0} W over the last {1} min", "media {0:0.0} W negli ultimi {1} min"), w, (int)Math.Round(span.TotalMinutes))
                : string.Format(L.T("{0:0.0} W now · the estimate improves within minutes", "{0:0.0} W ora · la stima si affina in qualche minuto"), w);
            return "≈ " + Hm(h) + " · " + basis;
        }

        public string Short(BatteryInfo b)
        {
            if (!b.Charging && !b.Discharging) return "";
            TimeSpan span;
            double w = AverageW(out span);
            if (w < 0.05) return "";
            return Hm(b.Charging ? Math.Max(0, b.FullWh - b.RemainingWh) / w : b.RemainingWh / w) + (b.Charging ? L.T(" to full charge", " alla carica completa") : L.T(" left", " rimanenti"));
        }
    }

    // "Tray" checkbox: stays active near the clock and starts with Windows.
    static class Settings
    {
        public const string Key = @"Software\SnapControl";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Tray
        {
            get
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key)) return k != null && Convert.ToInt32(k.GetValue("Tray", 0)) == 1; }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Tray", value ? 1 : 0);
                    using (var r = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                        if (value) r.SetValue("SnapControl", "\"" + Application.ExecutablePath + "\" --tray");
                        else r.DeleteValue("SnapControl", false);
                }
                catch { }
            }
        }

        // Window language: English if missing.
        public static bool Italian
        {
            get
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key)) return k != null && (k.GetValue("Lang") as string) == "it"; }
                catch { return false; }
            }
            set
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Lang", value ? "it" : "en"); }
                catch { }
            }
        }

        // Ids of the updates the user marked as possibly incompatible.
        public static string[] Flagged
        {
            get
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key)) return (k == null ? null : k.GetValue("FlaggedDrivers") as string[]) ?? new string[0]; }
                catch { return new string[0]; }
            }
            set
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                        if (value.Length > 0) k.SetValue("FlaggedDrivers", value, Microsoft.Win32.RegistryValueKind.MultiString);
                        else k.DeleteValue("FlaggedDrivers", false);
                }
                catch { }
            }
        }
    }

    // "Never sleep" checkbox: no sleep/hibernate on idle and Wi-Fi always connected
    // (also in modern standby, e.g. lid closed). Previous values are saved and restored.
    static class NoStandby
    {
        static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        static readonly Guid SubNone = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
        static readonly Guid SubWifi = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
        // subgroup, setting, forced value
        static readonly Guid[][] Items =
        {
            new[] { SubSleep, new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da") },   // sleep after: 0 = never
            new[] { SubSleep, new Guid("9d7815a6-7ee4-497e-8888-515a05f02364") },   // hibernate after: 0 = never
            new[] { SubNone, new Guid("f15576e8-98b7-4186-b944-eafa664402d9") },    // network in standby: 1 = on
            new[] { SubWifi, new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a") },    // Wi-Fi power saving: 0 = maximum performance
        };
        static readonly uint[] Forced = { 0, 0, 1, 0 };
        const string Fallback = "300,180;0,0;1,1;0,0";   // this PC's default, if the saved value is missing

        public static Guid Scheme()
        {
            IntPtr p;
            Check(Native.PowerGetActiveScheme(IntPtr.Zero, out p));
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { Native.LocalFree(p); }
        }

        public static void Check(uint r) { if (r != 0) throw new System.ComponentModel.Win32Exception((int)r); }

        // true if the setting is among those this feature forces and restores
        public static bool Controls(Guid set)
        {
            foreach (var i in Items) if (i[1] == set) return true;
            return false;
        }

        public static bool Enabled
        {
            get
            {
                try
                {
                    Guid s = Scheme(), sub = Items[0][0], set = Items[0][1];
                    uint ac, dc;
                    Check(Native.PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out ac));
                    Check(Native.PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out dc));
                    return ac == 0 && dc == 0;
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            Guid s = Scheme();
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Settings.Key))
            {
                string[] prev = ((k.GetValue("StandbyPrev") as string) ?? Fallback).Split(';');
                var saved = new List<string>();
                for (int i = 0; i < Items.Length; i++)
                {
                    Guid sub = Items[i][0], set = Items[i][1];
                    uint ac, dc;
                    if (on)
                    {
                        Check(Native.PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out ac));
                        Check(Native.PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out dc));
                        saved.Add(ac + "," + dc);
                        ac = dc = Forced[i];
                    }
                    else
                    {
                        string[] v = (i < prev.Length ? prev[i] : Fallback.Split(';')[i]).Split(',');
                        ac = uint.Parse(v[0]); dc = uint.Parse(v[1]);
                    }
                    Check(Native.PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, ac));
                    Check(Native.PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, dc));
                }
                if (on) k.SetValue("StandbyPrev", string.Join(";", saved));
            }
            Check(Native.PowerSetActiveScheme(IntPtr.Zero, ref s));   // applies the changes
        }
    }

    static class SystemInfo
    {
        static string Q(string query, string prop)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\CIMV2", query))
                    foreach (ManagementObject o in s.Get()) return Convert.ToString(o[prop]);
            }
            catch { }
            return "?";
        }

        public static List<string[]> Rows()
        {
            string model = Q("SELECT Version FROM Win32_ComputerSystemProduct", "Version");
            string type = Q("SELECT Model FROM Win32_ComputerSystem", "Model");
            double ramGb = 0;
            double.TryParse(Q("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory"), out ramGb);
            ramGb /= 1024.0 * 1024 * 1024;
            int ubr = 0;
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                if (k != null) ubr = Convert.ToInt32(k.GetValue("UBR", 0));
            TimeSpan up = TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
            return new List<string[]>
            {
                new[] { L.T("Model", "Modello"), model + " · " + type },
                new[] { L.T("UEFI firmware", "Firmware UEFI"), Q("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion") },
                new[] { L.T("Processor", "Processore"), Q("SELECT Name FROM Win32_Processor", "Name").Replace("(R)", "").Replace("(TM)", "") },
                new[] { L.T("Logical cores", "Core logici"), Q("SELECT NumberOfLogicalProcessors FROM Win32_Processor", "NumberOfLogicalProcessors") },
                new[] { L.T("Memory", "Memoria"), string.Format("{0:0.0} GB", ramGb) },
                new[] { L.T("System", "Sistema"), Q("SELECT Caption FROM Win32_OperatingSystem", "Caption").Replace("Microsoft ", "") + " · build " + Q("SELECT BuildNumber FROM Win32_OperatingSystem", "BuildNumber") + "." + ubr },
                new[] { L.T("Architecture", "Architettura"), RuntimeInformation.ProcessArchitecture + L.T(" native", " nativo") },
                new[] { L.T("Uptime", "Acceso da"), string.Format(L.T("{0} d {1} h {2} min", "{0} g {1} h {2} min"), up.Days, up.Hours, up.Minutes) },
            };
        }
    }

    // Charge limit via the Qualcomm battery driver: the Lenovo firmware does not expose it.
    // Tested on qcbattminiclass8380 with the AC adapter plugged in: BatteryDischarge stops charging without discharging
    // (power drops to 0 W and does not go negative), BatteryCharge resumes it. Requires an elevated process.
    static class ChargeLimit
    {
        public static readonly int[] Levels = { 50, 80, 85, 90, 95 };
        public const int Margin = 5;                           // charging resumes this far below the limit
        const string TaskName = "SnapControl Limite carica";
        public const int BatteryCharge = 1, BatteryDischarge = 2;
        const uint IoctlTag = 0x294040, IoctlSet = 0x298048;

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref Interface o);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref Interface d, IntPtr o, uint n, out uint r, IntPtr i);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr p, uint c, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint c, byte[] i, int ni, byte[] o, int no, out int r, IntPtr v);
        [StructLayout(LayoutKind.Sequential)] struct Interface { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }

        public static string DataDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SnapControl"); } }
        static string Flag { get { return System.IO.Path.Combine(DataDir, "charge-limit.on"); } }
        public static bool Enabled { get { return System.IO.File.Exists(Flag); } }

        // A SYSTEM task and elevated processes write here. Windows lets any user create this folder and files
        // in it, so: no reparse point, administrators as owner, and whatever another user left is removed.
        public static void SecureDataDir()
        {
            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            const System.Security.AccessControl.InheritanceFlags inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            var ds = new System.Security.AccessControl.DirectorySecurity();
            ds.SetOwner(admins);
            ds.SetAccessRuleProtection(true, false);
            ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(admins, System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(system, System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users, System.Security.AccessControl.FileSystemRights.ReadAndExecute, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));

            var di = new System.IO.DirectoryInfo(DataDir);
            // the folder of another user may hold handles that keep write access: it is replaced, not reused
            if (di.Exists && !Trusted(di, admins, system)) { if ((di.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) di.Delete(); else di.Delete(true); di.Refresh(); }
            if (di.Exists) di.SetAccessControl(ds);
            else System.IO.Directory.CreateDirectory(DataDir, ds);
            Sweep(di, admins, system);
        }

        static bool Trusted(System.IO.FileSystemInfo e, System.Security.Principal.SecurityIdentifier admins, System.Security.Principal.SecurityIdentifier system)
        {
            if ((e.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) return false;
            var dir = e as System.IO.DirectoryInfo;
            var owner = dir != null ? dir.GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner).GetOwner(typeof(System.Security.Principal.SecurityIdentifier))
                : ((System.IO.FileInfo)e).GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner).GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
            return owner == admins || owner == system;
        }

        static void Sweep(System.IO.DirectoryInfo di, System.Security.Principal.SecurityIdentifier admins, System.Security.Principal.SecurityIdentifier system)
        {
            foreach (var e in di.GetFileSystemInfos())
            {
                var dir = e as System.IO.DirectoryInfo;
                if (!Trusted(e, admins, system))
                {
                    if (dir == null || (e.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) e.Delete();
                    else dir.Delete(true);
                }
                else if (dir != null) Sweep(dir, admins, system);
            }
        }

        // The flag holds the chosen level. Empty, as written by earlier versions, or not valid: 80%.
        public static int Parse(string s)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), out v) && Array.IndexOf(Levels, v) >= 0 ? v : 80;
        }

        public static int Stop
        {
            get
            {
                try { return Parse(System.IO.File.ReadAllText(Flag)); }
                catch { return Parse(null); }
            }
        }

        // Command to send, 0 if none. Between the two thresholds leaves the state as is to avoid constant toggling.
        public static int Decide(int percent, bool onAc, bool charging, int stop)
        {
            if (percent < 0 || !onAc) return 0;
            if (charging && percent >= stop) return BatteryDischarge;
            if (!charging && percent <= stop - Margin) return BatteryCharge;
            return 0;
        }

        static void Send(int level)
        {
            var g = new Guid("72631e54-78A4-11d0-bcf7-00aa00b7b32a");
            IntPtr set = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            IntPtr buf = Marshal.AllocHGlobal(2048);
            string path;
            try
            {
                var d = new Interface { Size = Marshal.SizeOf(typeof(Interface)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, 0, ref d)) throw new System.ComponentModel.Win32Exception();
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                uint need;
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, 2048, out need, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
                path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            }
            finally { Marshal.FreeHGlobal(buf); SetupDiDestroyDeviceInfoList(set); }

            using (var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new System.ComponentModel.Win32Exception();
                var tag = new byte[4];
                int r;
                if (!DeviceIoControl(h, IoctlTag, new byte[4], 4, tag, 4, out r, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
                var input = new byte[12];
                tag.CopyTo(input, 0);
                BitConverter.GetBytes(level).CopyTo(input, 4);
                if (!DeviceIoControl(h, IoctlSet, input, 12, null, 0, out r, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            }
        }

        static void Log(string s)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(DataDir, "charge-limit.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + "\r\n"); } catch { }
        }

        // Scheduled task process. State is always rechecked because it can change on cable reconnect or suspend.
        public static void Run()
        {
            string lastError = null;
            // a failure here must not end the task: charging could stay stopped with nobody to resume it
            try { SecureDataDir(); }
            catch (Exception ex) { Log("errore: " + ex.Message); }
            bool wasOn = true;
            while (true)
            {
                try
                {
                    // no flag means off, whatever started this process: never a limit the window does not show
                    if (!Enabled)
                    {
                        if (wasOn) { Send(BatteryCharge); wasOn = false; }
                        Thread.Sleep(30000);
                        continue;
                    }
                    wasOn = true;
                    var b = BatteryInfo.Read();
                    int cmd = Decide(b.Percent, b.OnAc, b.Charging, Stop);
                    if (cmd != 0)
                    {
                        Send(cmd);
                        Log((cmd == BatteryDischarge ? "carica fermata a " : "carica ripresa a ") + b.Percent + "%");
                    }
                    lastError = null;
                }
                catch (Exception ex)
                {
                    if (ex.Message != lastError) Log("errore: " + ex.Message);
                    lastError = ex.Message;
                }
                Thread.Sleep(30000);
            }
        }

        static int Schtasks(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args) { UseShellExecute = false, CreateNoWindow = true };
            using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }

        // Runs elevated. The task runs as SYSTEM: from a user-writable path it would grant privileges to anyone replacing the exe.
        public static void Enable(int level)
        {
            if (Array.IndexOf(Levels, level) < 0) throw new Exception("livello non valido: " + level);
            string exe = Application.ExecutablePath, dir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\";
            if (!exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) throw new Exception("il limite di carica richiede SnapControl installato in " + dir);
            SecureDataDir();
            int previous = Enabled ? Stop : 0;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>"
                + "<Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
                + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
                + "<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>"
                + "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--charge-limit run</Arguments></Exec></Actions></Task>";
            string file = System.IO.Path.Combine(DataDir, Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                System.IO.File.WriteAllText(file, xml, Encoding.Unicode);
                int code = Schtasks("/create /f /tn \"" + TaskName + "\" /xml \"" + file + "\"");
                if (code != 0) throw new Exception("schtasks /create ha restituito " + code);
            }
            finally { System.IO.File.Delete(file); }
            // the flag comes first because the task reads it as soon as it starts; a running task is restarted
            System.IO.File.WriteAllText(Flag + ".tmp", level.ToString());
            System.IO.File.Delete(Flag);
            System.IO.File.Move(Flag + ".tmp", Flag);
            Schtasks("/end /tn \"" + TaskName + "\"");
            // /end is not immediate, and with IgnoreNew a /run during the exit would be dropped
            for (int i = 0; i < 20 && Array.Exists(System.Diagnostics.Process.GetProcessesByName("SnapControl"), p => p.SessionId == 0); i++) Thread.Sleep(250);
            int run = Schtasks("/run /tn \"" + TaskName + "\"");
            if (run != 0)
            {
                // nothing would watch the battery: back to off, with charging free
                try { Send(BatteryCharge); } catch { }
                Schtasks("/delete /f /tn \"" + TaskName + "\"");
                System.IO.File.Delete(Flag);
                throw new Exception("schtasks /run ha restituito " + run);
            }
            // a higher limit counts at once, even if charging was stopped above the new resume level
            if (level > previous && previous != 0 && BatteryInfo.Read().OnAc) Send(BatteryCharge);
        }

        // Runs elevated. If charging cannot be resumed nothing is removed: the limit stays on and the error is shown.
        public static void Disable()
        {
            Schtasks("/end /tn \"" + TaskName + "\"");
            try { Send(BatteryCharge); }
            catch { Schtasks("/run /tn \"" + TaskName + "\""); throw; }
            Schtasks("/delete /f /tn \"" + TaskName + "\"");
            if (System.IO.File.Exists(Flag)) System.IO.File.Delete(Flag);
        }
    }

    // State shown on the System page. Read once: ConsentPromptBehaviorAdmin 0 means "elevate without prompting", EnableLUA 0 means UAC is off.
    static class Uac
    {
        public static bool Prompts = Read();

        public static bool PromptsFrom(object enableLua, object consent)
        {
            return !Equals(enableLua, 0) && !Equals(consent, 0);
        }

        static bool Read()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                    return k == null || PromptsFrom(k.GetValue("EnableLUA"), k.GetValue("ConsentPromptBehaviorAdmin"));
            }
            catch { return true; }
        }
    }

    sealed class DriverRow
    {
        public string Device, Provider, Version, Inf;
        public DateTime Date;
    }

    static class Drivers
    {
        static readonly string[] SkipProviders = { "Microsoft", "Microsoft Corporation", "(Standard system devices)", "" };

        public static List<DriverRow> ReadOem()
        {
            var list = new List<DriverRow>();
            var seen = new HashSet<string>();
            using (var s = new ManagementObjectSearcher("SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver"))
                foreach (ManagementObject o in s.Get())
                {
                    string prov = Convert.ToString(o["DriverProviderName"]) ?? "";
                    if (Array.IndexOf(SkipProviders, prov) >= 0) continue;
                    var r = new DriverRow
                    {
                        Device = Text.Clean(o["DeviceName"]).Replace("(R)", "").Replace("(TM)", ""),
                        Provider = prov,
                        Version = Convert.ToString(o["DriverVersion"]),
                        Inf = Convert.ToString(o["InfName"])
                    };
                    string d = Convert.ToString(o["DriverDate"]);
                    if (!string.IsNullOrEmpty(d)) { try { r.Date = ManagementDateTimeConverter.ToDateTime(d); } catch { } }
                    if (seen.Add(r.Device + "|" + r.Version)) list.Add(r);
                }
            list.Sort((a, b) => string.Compare(a.Provider + a.Device, b.Provider + b.Device, StringComparison.OrdinalIgnoreCase));
            // the BIOS is not a PnP driver: first row, with version and release date
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                foreach (ManagementObject o in s.Get())
                {
                    var r = new DriverRow { Device = "BIOS", Provider = Text.Clean(o["Manufacturer"]), Version = (Convert.ToString(o["SMBIOSBIOSVersion"]) ?? "").Trim() };
                    string d = Convert.ToString(o["ReleaseDate"]);
                    if (!string.IsNullOrEmpty(d)) { try { r.Date = ManagementDateTimeConverter.ToDateTime(d); } catch { } }
                    list.Insert(0, r);
                }
            return list;
        }

        // col matches the page columns: 0 device, 1 vendor, 2 version, 3 date, 4 age.
        public static void Sort(List<DriverRow> list, int col, bool desc)
        {
            list.Sort((a, b) =>
            {
                int c = CompareBy(a, b, col, desc);
                return c != 0 ? c : string.Compare(a.Device + a.Version, b.Device + b.Version, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Rows without a date or with a non-numeric version (BIOS) always last, in both directions.
        static int CompareBy(DriverRow a, DriverRow b, int col, bool desc)
        {
            int c;
            if (col >= 3)
            {
                bool na = a.Date == DateTime.MinValue, nb = b.Date == DateTime.MinValue;
                if (na || nb) return na.CompareTo(nb);
                c = a.Date.CompareTo(b.Date);
                if (col == 4) c = -c;
            }
            else if (col == 2)
            {
                Version va, vb;
                bool pa = Version.TryParse(a.Version, out va), pb = Version.TryParse(b.Version, out vb);
                if (pa != pb) return pa ? -1 : 1;
                c = pa ? va.CompareTo(vb) : string.Compare(a.Version, b.Version, StringComparison.OrdinalIgnoreCase);
            }
            else c = string.Compare(col == 0 ? a.Device : a.Provider, col == 0 ? b.Device : b.Provider, StringComparison.OrdinalIgnoreCase);
            return desc ? -c : c;
        }

        const string WuQuery = "IsInstalled=0 and Type='Driver' and IsHidden=0";

        static dynamic Session()
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            session.ClientApplicationID = "SnapControl";
            return session;
        }

        // Updates from official vendor channels, only on user request:
        // Windows Update (where Lenovo publishes certified drivers for the 83HL), the Lenovo download API (BIOS and drivers)
        // and the Microsoft Update Catalog.
        public static List<UpdateItem> SearchUpdates(List<Func<string>> notes)
        {
            var res = new List<UpdateItem>();
            try
            {
                dynamic result = Session().CreateUpdateSearcher().Search(WuQuery);
                for (int i = 0; i < result.Updates.Count; i++)
                {
                    dynamic u = result.Updates.Item(i);
                    var it = new UpdateItem { Id = (string)u.Identity.UpdateID, Title = (string)u.Title, Provider = (string)u.DriverProvider, Source = "WINDOWS UPDATE", Url = MsCatalog.SearchUrl((string)u.Title) };
                    try { it.Date = ((DateTime)u.DriverVerDate).ToString("yyyy-MM-dd"); } catch { it.Date = ""; }
                    res.Add(it);
                }
                int n = res.Count;
                notes.Add(() => "Windows Update: " + (n == 0 ? L.T("no new drivers", "nessun driver nuovo") : n + L.T(n == 1 ? " driver" : " drivers", " driver")));
            }
            catch (Exception ex) { string why = ex.Message.Trim(); notes.Add(() => L.T("Windows Update unreachable (", "Windows Update non raggiungibile (") + why + ")"); }

            try
            {
                int before = res.Count;
                res.AddRange(Lenovo.Search());
                int n = res.Count - before;
                notes.Add(() => "Lenovo: " + (n == 0 ? L.T("BIOS up to date, no new drivers", "BIOS aggiornato, nessun driver nuovo") : n + (n == 1 ? L.T(" package", " pacchetto") : L.T(" packages", " pacchetti"))));
            }
            catch (Exception ex) { string why = ex.Message.Trim(); notes.Add(() => L.T("Lenovo unreachable (", "Lenovo non raggiungibile (") + why + ")"); }

            try
            {
                int before = res.Count;
                res.AddRange(MsCatalog.Search());
                int n = res.Count - before;
                int skipped = MsCatalog.Errors;
                notes.Add(() => L.T("Microsoft catalog: ", "Catalogo Microsoft: ") + (n == 0 ? L.T("no newer drivers", "nessun driver più nuovo") : n + L.T(n == 1 ? " driver" : " drivers", " driver")) + (skipped > 0 ? " (" + skipped + L.T(" devices not checked)", " dispositivi non controllati)") : ""));
            }
            catch (Exception ex) { string why = ex.Message.Trim(); notes.Add(() => L.T("Microsoft catalog unreachable (", "Catalogo Microsoft non raggiungibile (") + why + ")"); }

            // the generic Qualcomm Adreno driver is in no queryable source (requires a Qualcomm account)
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceName LIKE '%Adreno%'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string ver = Convert.ToString(o["DriverVersion"]);
                        notes.Add(() => "GPU Adreno " + ver + L.T(": newer drivers only from Qualcomm, by hand", ": driver più nuovi solo da Qualcomm, a mano"));
                        break;
                    }
            }
            catch { }
            return res;
        }

        // Runs in the elevated process: downloads and installs only the selected updates.
        public static string InstallWindowsUpdate(ICollection<string> ids)
        {
            dynamic session = Session();
            dynamic result = session.CreateUpdateSearcher().Search(WuQuery);
            dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl"));
            var titles = new List<string>();
            for (int i = 0; i < result.Updates.Count; i++)
            {
                dynamic u = result.Updates.Item(i);
                if (!ids.Contains((string)u.Identity.UpdateID)) continue;
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                coll.Add(u);
                titles.Add((string)u.Title);
            }
            if (titles.Count == 0) return L.T("None of the chosen updates is available any more.", "Nessuno degli aggiornamenti scelti è ancora disponibile.");
            dynamic dl = session.CreateUpdateDownloader();
            dl.Updates = coll;
            dl.Download();
            dynamic inst = session.CreateUpdateInstaller();
            inst.Updates = coll;
            dynamic r = inst.Install();
            var sb = new StringBuilder();
            int ok = 0;
            for (int i = 0; i < titles.Count; i++)
            {
                int code = (int)r.GetUpdateResult(i).ResultCode;   // 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted
                if (code == 2 || code == 3) ok++;
                sb.AppendLine((code == 2 || code == 3 ? "OK      " : L.T("ERROR   ", "ERRORE  ")) + titles[i]);
            }
            return L.T("Installed ", "Installati ") + ok + L.T(" of ", " di ") + titles.Count + ((bool)r.RebootRequired ? L.T(" · restart required", " · riavvio richiesto") : "") + "\n" + sb;
        }
    }

    sealed class UpdateItem
    {
        public string Id, Title, Provider, Date, Source, Url;
        public string Inst, By;                                // installed version and vendor, if known
        public string BiosUrl, BiosSha256;                     // package SnapControl may download
        public bool BiosFlash;                                 // newer than the installed BIOS: the installer may start
        public bool Installable { get { return Source == "WINDOWS UPDATE" || Source == MsCatalog.Source; } }

        // Source stays the value compared in Installable: here only the on-screen text.
        public string SourceText { get { return Source == MsCatalog.Source ? L.T("MS CATALOG", "CATALOGO MS") : Source; } }
        public string TitleText
        {
            get
            {
                if (Inst == null) return Title;
                return Title + "  (" + L.T("installed ", "installato ") + Inst + (string.IsNullOrEmpty(By) ? "" : " · " + L.T("by ", "di ") + By) + ")";
            }
        }

        // Prefilled GitHub issue, always in English: nothing is sent until the user submits it on GitHub.
        public string IssueUrl(string machine, string windows, string app)
        {
            string body = "**Driver:** " + Title + "\n**Source:** " + Source + "\n**Package:** " + Url + "\n**Installed version:** " + (Inst ?? "unknown")
                + "\n**Date:** " + Date + "\n**Machine type:** " + machine + "\n**Windows:** " + windows + "\n**SnapControl:** " + app + "\n\n**What goes wrong:**\n";
            return "https://github.com/mich-de/snapcontrol/issues/new?title=" + Uri.EscapeDataString("Possibly incompatible driver: " + Title) + "&body=" + Uri.EscapeDataString(body);
        }
    }

    // Microsoft Update Catalog: ARM64 drivers newer than the installed ones that Windows Update does not offer,
    // also those published for other brands (Foxconn, Huaqin, Surface…) with the same Snapdragon hardware.
    // Each device is searched by name and, if PCI/USB, by hardware ID; in name searches a package
    // counts only if its "Driver Model" lists the exact device name. The Adreno GPU is not in the catalog.
    // Item Ids have the "cat:" prefix.
    static class MsCatalog
    {
        public const string Source = "CATALOGO MS";
        const string Base = "https://www.catalog.update.microsoft.com/";

        // Search page: one row per package, with its Download button.
        public static string SearchUrl(string q) { return Base + "Search.aspx?q=" + Uri.EscapeDataString(q); }

        sealed class Dev
        {
            public string Name, NameQuery, HwQuery;
            public Version Inst;
            public DateTime InstDate;
        }

        public static int Errors;   // devices whose search failed (network, site limits)

        static System.Net.WebClient Client()
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288;
            return new Web { Encoding = Encoding.UTF8 };
        }

        // the default WebClient timeout (100 s) blocked the search when the catalog does not respond
        sealed class Web : System.Net.WebClient
        {
            protected override System.Net.WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                r.Timeout = 20000;
                return r;
            }
        }

        // without a User-Agent the catalog returns a "use Internet Explorer" page with no results;
        // WebClient clears headers on every request
        static void Ua(System.Net.WebClient wc) { wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "Mozilla/5.0"; }

        static readonly string CacheDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"SnapControl\catalog");

        // Catalog pages cached on disk: details of an updateid never change, searches are valid for maxAge.
        // Pages without validMark (site errors) are not cached.
        static string Cached(System.Net.WebClient wc, string url, TimeSpan maxAge, string validMark)
        {
            string f;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                f = System.IO.Path.Combine(CacheDir, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "") + ".html");
            try { if (System.IO.File.Exists(f) && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(f) < maxAge) return System.IO.File.ReadAllText(f, Encoding.UTF8); } catch { }
            Ua(wc);
            string s = wc.DownloadString(url);
            if (s.Contains(validMark)) try { System.IO.Directory.CreateDirectory(CacheDir); System.IO.File.WriteAllText(f, s, Encoding.UTF8); } catch { }
            return s;
        }

        static readonly TimeSpan SearchMaxAge = TimeSpan.FromHours(6), DetailMaxAge = TimeSpan.FromDays(3650);

        static string Plain(string html) { return System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")); }

        static string Field(string text, string name, string next)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, name + @":\s*(.*?)\s*" + next + ":");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        public static List<UpdateItem> Search()
        {
            var devs = new List<Dev>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var s = new ManagementObjectSearcher("SELECT DeviceName, HardWareID, DriverVersion, DriverProviderName, DriverDate FROM Win32_PnPSignedDriver"))
                foreach (ManagementObject o in s.Get())
                {
                    string name = Text.Clean(o["DeviceName"]), hw = Convert.ToString(o["HardWareID"]) ?? "", prov = Convert.ToString(o["DriverProviderName"]) ?? "";
                    Version inst;
                    if (prov.Length == 0 || prov.StartsWith("Microsoft") || prov.StartsWith("(") || name.Length == 0 || !names.Add(name)
                        || !Version.TryParse(Convert.ToString(o["DriverVersion"]), out inst)) continue;
                    var d = new Dev { Name = name, Inst = inst, NameQuery = System.Text.RegularExpressions.Regex.Replace(name.Replace("(R)", "").Replace("(TM)", ""), @"\s+", " ").Trim() };
                    try { d.InstDate = ManagementDateTimeConverter.ToDateTime(Convert.ToString(o["DriverDate"])); } catch { }
                    string[] p = hw.Split('&');
                    // e.g. PCI\VEN_17CB&DEV_1107: with SUBSYS the catalog finds nothing; ACPI IDs are not indexed,
                    // but for ACPI\VEN_QCOM&DEV_0C1B the Qualcomm package description contains "QCOM0C1B"
                    if ((hw.StartsWith("PCI\\") || hw.StartsWith("USB\\")) && p.Length >= 2) d.HwQuery = p[0] + "&" + p[1];
                    else if (hw.StartsWith("ACPI\\VEN_QCOM", StringComparison.OrdinalIgnoreCase) && p.Length >= 2 && p[1].StartsWith("DEV_", StringComparison.OrdinalIgnoreCase)) d.HwQuery = "QCOM" + p[1].Substring(4);
                    devs.Add(d);
                }

            var res = new List<UpdateItem>();
            Errors = 0;
            var details = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            System.Threading.Tasks.Parallel.ForEach(devs, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 }, d =>
            {
                try
                {
                    using (var wc = Client())
                    {
                        var it = Best(wc, d, details);
                        if (it != null) lock (res) if (!res.Exists(r => r.Id == it.Id)) res.Add(it);
                    }
                }
                catch { System.Threading.Interlocked.Increment(ref Errors); }   // an unreachable device does not stop the others
            });
            res.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return res;
        }

        // Highest version, newer than the installed one, for ARM64 and (if searched by name) for this device.
        static UpdateItem Best(System.Net.WebClient wc, Dev dev, System.Collections.Concurrent.ConcurrentDictionary<string, string> details)
        {
            var cands = new List<KeyValuePair<Version, string[]>>();   // version → id, date, "hw" if found by hardware ID
            var ids = new HashSet<string>();
            foreach (string q in new[] { dev.HwQuery, dev.NameQuery })
            {
                if (q == null) continue;
                string html = Cached(wc, Base + "Search.aspx?q=" + Uri.EscapeDataString(q), SearchMaxAge, "ctl00_catalogBody_ResultsHeaderTable");
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html,
                    "<tr id=\"([0-9a-f-]{36})_R\\d+\".*?_link'[^>]*>\\s*(.*?)\\s*</a>.*?_C4_R\\d+\">\\s*(.*?)\\s*</td>", System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    var vm = System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, @"\d+\.\d+\.\d+(\.\d+)?");
                    Version v;
                    if (vm.Count == 0 || !Version.TryParse(vm[vm.Count - 1].Value, out v) || v <= dev.Inst || !ids.Add(m.Groups[1].Value)) continue;
                    // packages older than the installed driver (e.g. for earlier Snapdragon generations, with
                    // different numbering) are not updates even if the version number is higher
                    DateTime pd;
                    if (DateTime.TryParse(m.Groups[3].Value.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out pd) && pd < dev.InstDate.Date) continue;
                    cands.Add(new KeyValuePair<Version, string[]>(v, new[] { m.Groups[1].Value, m.Groups[3].Value.Trim(), q == dev.HwQuery ? "hw" : "" }));
                }
            }
            // first the same major version as the installed one (ARM64 packages usually have their own line,
            // e.g. Wi-Fi 1.0.x vs 3.1.x for x64), then the highest: the architecture is only readable on the details page
            Version inst = dev.Inst;
            cands.Sort((a, b) => a.Key.Major == b.Key.Major ? b.Key.CompareTo(a.Key) : (a.Key.Major == inst.Major ? -1 : b.Key.Major == inst.Major ? 1 : b.Key.CompareTo(a.Key)));
            // shortcut: only the first 8 candidates (nearly all x64, each costs one page), raise if a known ARM64 is missed
            foreach (var c in cands.GetRange(0, Math.Min(8, cands.Count)))
            {
                string id = c.Value[0], url = Base + "ScopedViewInline.aspx?updateid=" + id;
                string text = details.GetOrAdd(id, k => Plain(Cached(wc, url, DetailMaxAge, "Architecture")));
                if (Field(text, "Architecture", "Classification").IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.Value[2] != "hw" && Array.FindIndex(Field(text, "Driver Model", "Driver Provider").Split(','), n => n.Trim().Equals(dev.Name, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                string company = Field(text, "Company", "Driver Manufacturer");
                DateTime d;
                string date = DateTime.TryParse(c.Value[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d) ? d.ToString("yyyy-MM-dd") : "";
                return new UpdateItem
                {
                    Id = "cat:" + id, Provider = company, Date = date, Source = Source, Url = SearchUrl(id),
                    Title = dev.Name + "  " + c.Key, Inst = inst.ToString(), By = company
                };
            }
            return null;
        }

        static int Run(string exe, string args, StringBuilder log)
        {
            string o;
            return Run(exe, args, log, out o);
        }

        static int Run(string exe, string args, StringBuilder log, out string output)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 && p.ExitCode != 3010) log.AppendLine(output.Trim());
                return p.ExitCode;
            }
        }

        // DeviceID → { InfName, DriverVersion, DeviceName } of the drivers in use.
        static Dictionary<string, string[]> InUse()
        {
            var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            using (var s = new ManagementObjectSearcher("SELECT DeviceID, InfName, DriverVersion, DeviceName FROM Win32_PnPSignedDriver"))
                foreach (ManagementObject o in s.Get())
                {
                    string id = Convert.ToString(o["DeviceID"]);
                    if (!string.IsNullOrEmpty(id)) d[id] = new[] { Convert.ToString(o["InfName"]) ?? "", Convert.ToString(o["DriverVersion"]) ?? "", Text.Clean(o["DeviceName"]) };
                }
            return d;
        }

        static HashSet<string> WithProblems()
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var s = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
                foreach (ManagementObject o in s.Get()) h.Add(Convert.ToString(o["PNPDeviceID"]));
            return h;
        }

        public static string BackupDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"SnapControl\backup"); } }

        // Runs in the elevated process: downloads the .cab from the catalog, extracts it and installs it with pnputil.
        // After install checks which devices moved to the new package: if none (Windows
        // prefers the Lenovo driver, more specific to this PC) the package is removed from the store; if a
        // device that moved ends up in error the package is uninstalled and Windows returns to the previous driver,
        // which stays in the store and is in any case exported to BackupDir before anything else.
        public static string Install(ICollection<string> ids)
        {
            var sb = new StringBuilder();
            int ok = 0;
            bool reboot = false;
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System), pnp = System.IO.Path.Combine(sys, "pnputil.exe");
            ChargeLimit.SecureDataDir();
            using (var wc = Client())
                foreach (string raw in ids)
                {
                    string id = raw.Substring(4);
                    // the list comes from a file in the user's temp folder, and the id becomes a path that gets deleted
                    if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$")) { sb.AppendLine(L.T("ERROR   invalid catalog id", "ERRORE  id catalogo non valido")); continue; }
                    string dir = System.IO.Path.Combine(ChargeLimit.DataDir, "work", Guid.NewGuid().ToString("N"));
                    try
                    {
                        Ua(wc);
                        wc.Headers[System.Net.HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                        string dlg = wc.UploadString(Base + "DownloadDialog.aspx", "updateIDs=" + Uri.EscapeDataString("[{\"size\":0,\"languages\":\"\",\"uidInfo\":\"" + id + "\",\"updateID\":\"" + id + "\"}]"));
                        var m = System.Text.RegularExpressions.Regex.Match(dlg, @"\.url = '([^']+\.cab)'");
                        if (!m.Success) throw new Exception(L.T("download link not found", "link di download non trovato"));
                        System.IO.Directory.CreateDirectory(dir);
                        string cab = System.IO.Path.Combine(dir, "driver.cab"), x = System.IO.Path.Combine(dir, "x");
                        System.IO.Directory.CreateDirectory(x);
                        Ua(wc);
                        wc.DownloadFile(m.Groups[1].Value, cab);
                        if (Run(System.IO.Path.Combine(sys, "expand.exe"), "\"" + cab + "\" -F:* \"" + x + "\"", sb) != 0) throw new Exception(L.T("extraction failed", "estrazione non riuscita"));
                        var before = InUse();
                        string outp;
                        int code = Run(pnp, "/add-driver \"" + x + "\\*.inf\" /subdirs /install", sb, out outp);
                        if (code != 0 && code != 3010) throw new Exception(L.T("pnputil code ", "pnputil codice ") + code);
                        var pub = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (System.Text.RegularExpressions.Match pm in System.Text.RegularExpressions.Regex.Matches(outp, @"oem\d+\.inf", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) pub.Add(pm.Value);
                        var after = InUse();
                        var moved = new List<string>();   // DeviceIDs moved to one of the packages just published
                        foreach (var kv in after)
                        {
                            string[] old;
                            if (pub.Contains(kv.Value[0]) && (!before.TryGetValue(kv.Key, out old) || !old[0].Equals(kv.Value[0], StringComparison.OrdinalIgnoreCase))) moved.Add(kv.Key);
                        }
                        if (moved.Count == 0)
                        {
                            // with a pending reboot the change may not be visible yet: the package stays
                            if (code == 3010) { reboot = true; ok++; sb.AppendLine("OK      " + L.T("catalog ", "catalogo ") + id + L.T(": added, check after restart", ": aggiunto, verifica dopo il riavvio")); continue; }
                            foreach (string p in pub) Run(pnp, "/delete-driver " + p, new StringBuilder());
                            sb.AppendLine(L.T("SKIPPED catalog ", "IGNORATO catalogo ") + id + L.T(": Windows keeps the current driver (better suited to this PC), package removed", ": Windows mantiene il driver attuale (più adatto a questo PC), pacchetto rimosso"));
                            continue;
                        }
                        string bdir = System.IO.Path.Combine(BackupDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        var olds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string dev in moved) { string[] old; if (before.TryGetValue(dev, out old) && old[0].Length > 0) olds.Add(old[0]); }
                        foreach (string inf in olds)
                        {
                            string to = System.IO.Path.Combine(bdir, inf);
                            System.IO.Directory.CreateDirectory(to);
                            Run(pnp, "/export-driver " + inf + " \"" + to + "\"", sb);
                        }
                        var bad = WithProblems();
                        if (moved.Exists(bad.Contains))
                        {
                            foreach (string p in pub) Run(pnp, "/delete-driver " + p + " /uninstall /force", sb);
                            sb.AppendLine(L.T("REVERTED catalog ", "ANNULLATO catalogo ") + id + L.T(": device failed with the new driver, previous one restored (copy in ", ": dispositivo in errore col nuovo driver, ripristinato il precedente (copia in ") + bdir + ")");
                            continue;
                        }
                        reboot |= code == 3010;
                        ok++;
                        var names = new List<string>();
                        foreach (string dev in moved) { string[] old; before.TryGetValue(dev, out old); names.Add(after[dev][2] + " " + (old != null ? old[1] : "?") + " → " + after[dev][1]); }
                        sb.AppendLine("OK      " + L.T("catalog ", "catalogo ") + id + ": " + string.Join("; ", names) + (olds.Count > 0 ? " · backup in " + bdir : ""));
                    }
                    catch (Exception ex) { sb.AppendLine(L.T("ERROR   catalog ", "ERRORE  catalogo ") + id + ": " + ex.Message); }
                    finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
                }
            return L.T("Catalog: installed ", "Catalogo: installati ") + ok + L.T(" of ", " di ") + ids.Count + (reboot ? L.T(" · restart required", " · riavvio richiesto") : "") + "\n" + sb;
        }
    }

    // Public pcsupport.lenovo.com API (the same as the download page) for the machine type:
    // BIOS and drivers. The Lenovo System Update catalog for the 83HL is empty, this API is not.
    // The BIOS appears only if newer than the installed one; diagnostics and generic utilities are excluded.
    static class Lenovo
    {
        public static string MachineType()
        {
            using (var s = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in s.Get())
                {
                    string m = Convert.ToString(o["Model"]) ?? "";
                    return m.Length >= 4 ? m.Substring(0, 4).ToUpperInvariant() : m;
                }
            return "";
        }

        public static string SupportPage { get { return "https://pcsupport.lenovo.com/products/laptops-and-netbooks/ideapad-s-series-netbooks/ideapad-slim-5-14q8x9/83hl/downloads/driver-list"; } }

        const string Api = "https://pcsupport.lenovo.com/us/en/api/v4/";
        static readonly string[] SkipCategories = { "Diagnostic", "Software and Utilities", "ThinkVantage Technology", "Tool" };

        public static string InstalledBios()
        {
            using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
                foreach (ManagementObject o in s.Get()) return (Convert.ToString(o["SMBIOSBIOSVersion"]) ?? "").Trim();
            return "";
        }

        // Lenovo BIOS versions like PUCN55WW: same prefix and length, ordinal comparison.
        public static bool BiosNewer(string avail, string installed)
        {
            if (avail.Length != installed.Length || avail.Length < 4 || string.Compare(avail, 0, installed, 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
                return !avail.Equals(installed, StringComparison.OrdinalIgnoreCase);
            return string.Compare(avail, installed, StringComparison.OrdinalIgnoreCase) > 0;
        }

        // Stricter than BiosNewer, which also shows versions it cannot compare: an installer is offered only for
        // the same prefix and a higher version, because Lenovo says not to flash back to an earlier BIOS.
        public static bool BiosFlashable(string avail, string installed)
        {
            return avail.Length == installed.Length && avail.Length >= 4
                && string.Compare(avail, 0, installed, 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                && string.Compare(avail, installed, StringComparison.OrdinalIgnoreCase) > 0;
        }

        // Only the EXE named after this version, from Lenovo's download host and with its hash, is accepted.
        static void SetBiosFile(UpdateItem it, object[] files, string ver)
        {
            if (files == null) return;
            foreach (object f in files)
            {
                string url = Convert.ToString(J(f, "URL")) ?? "", sha = Convert.ToString(J(f, "SHA256")) ?? "";
                if (Convert.ToString(J(f, "TypeString")) != "EXE" || !System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$")) continue;
                if (!url.StartsWith("https://download.lenovo.com/", StringComparison.OrdinalIgnoreCase)
                    || !url.EndsWith("/" + ver + ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                it.BiosUrl = url;
                it.BiosSha256 = sha;
                return;
            }
        }

        public static string BiosPath(UpdateItem u)
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"SnapControl\bios", u.BiosUrl.Substring(u.BiosUrl.LastIndexOf('/') + 1));
        }

        // Returns the reason for refusing the file, null if it is the expected Lenovo package.
        public static string VerifyBios(string path, string sha256)
        {
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var f = System.IO.File.OpenRead(path))
                hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
            if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return L.T("SHA-256 does not match", "SHA-256 non corrispondente");
            if (!Native.SignatureValid(path)) return L.T("invalid signature", "firma non valida");
            using (var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path)))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(cert.Subject, "(^|, )O=Lenovo(,|$)")) return L.T("not signed by Lenovo", "non firmato da Lenovo");
                // machine stores only: a root added to the user's own store must not count
                using (var chain = new System.Security.Cryptography.X509Certificates.X509Chain(true))
                {
                    chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                    chain.ChainPolicy.VerificationFlags = System.Security.Cryptography.X509Certificates.X509VerificationFlags.IgnoreNotTimeValid;
                    if (!chain.Build(cert)) return L.T("signature does not reach a trusted root of this PC", "la firma non arriva a un'autorità attendibile di questo PC");
                }
            }
            return null;
        }

        // Copy into a folder only administrators can write to, then check that copy: nothing can swap it or sit next to it.
        public static string PrepareBios(string source, string sha256)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$") || !System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(source), "^[a-zA-Z0-9]{1,16}\\.exe$"))
                throw new Exception(L.T("invalid package", "pacchetto non valido"));
            ChargeLimit.SecureDataDir();
            string root = System.IO.Path.Combine(ChargeLimit.DataDir, "bios-run");
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);   // left by an installer that restarted the PC
            string dir = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")), copy = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(source));
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.Copy(source, copy);
            string err = VerifyBios(copy, sha256);
            if (err != null) { System.IO.Directory.Delete(root, true); throw new Exception(err); }
            return copy;
        }

        // Elevated. Nothing from the caller is trusted but the file path: version and hash come from Lenovo, here.
        public static int RunBios(string source)
        {
            Search();
            if (LatestBios == null || !LatestBios.BiosFlash || LatestBios.BiosSha256 == null) throw new Exception(L.T("no newer BIOS from Lenovo", "nessun BIOS più nuovo da Lenovo"));
            var b = BatteryInfo.Read();
            if (!b.OnAc || b.Percent < 30) throw new Exception(L.T("charger and at least 30% battery are required", "servono alimentatore e almeno il 30% di batteria"));
            string copy = PrepareBios(source, LatestBios.BiosSha256), dir = System.IO.Path.GetDirectoryName(copy);
            var psi = new System.Diagnostics.ProcessStartInfo(copy) { UseShellExecute = false, WorkingDirectory = dir };
            // the installer unpacks into its temp folder: the user's one could be changed while it runs
            psi.EnvironmentVariables["TEMP"] = dir;
            psi.EnvironmentVariables["TMP"] = dir;
            int code;
            using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(); code = p.ExitCode; }
            try { System.IO.Directory.Delete(dir, true); } catch { }
            return code;
        }

        // Downloads the package if it is missing or not valid; returns the error, null if the file is ready.
        public static string EnsureBios(UpdateItem u)
        {
            string path = BiosPath(u), part = path + ".part";
            if (System.IO.File.Exists(path) && VerifyBios(path, u.BiosSha256) == null) return null;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288;
            using (var wc = new System.Net.WebClient())
            {
                wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                wc.DownloadFile(u.BiosUrl, part);
            }
            string err = VerifyBios(part, u.BiosSha256);
            if (err == null) { System.IO.File.Delete(path); System.IO.File.Move(part, path); }
            else System.IO.File.Delete(part);
            return err;
        }

        // Walks the Dictionary/arrays returned by JavaScriptSerializer.
        static object J(object o, params string[] path)
        {
            foreach (string k in path)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(k, out o)) return null;
            }
            return o;
        }

        public static UpdateItem LatestBios;   // what Lenovo publishes now, even when it is not newer than the installed BIOS

        public static List<UpdateItem> Search()
        {
            var res = new List<UpdateItem>();
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288;
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            using (var wc = new System.Net.WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                // same headers as the Lenovo download page, which otherwise returns 403; WebClient clears them on every request
                Action hdr = delegate { wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "Mozilla/5.0"; wc.Headers[System.Net.HttpRequestHeader.Referer] = "https://pcsupport.lenovo.com/"; };
                hdr();
                object prod = json.DeserializeObject(wc.DownloadString(Api + "mse/getproducts?productId=" + MachineType()));
                var arr = prod as object[];
                if (arr != null) prod = arr.Length > 0 ? arr[0] : null;
                string id = Convert.ToString(J(prod, "Id"));
                if (string.IsNullOrEmpty(id)) throw new Exception(L.T("machine type " + MachineType() + " unknown to Lenovo", "tipo macchina " + MachineType() + " sconosciuto a Lenovo"));
                hdr();
                object dl = json.DeserializeObject(wc.DownloadString(Api + "downloads/drivers?productId=" + Uri.EscapeDataString(id)));
                var items = J(dl, "body", "DownloadItems") as object[] ?? new object[0];
                string bios = InstalledBios();
                foreach (object i in items)
                {
                    string cat = Convert.ToString(J(i, "Category", "Name"));
                    if (Array.IndexOf(SkipCategories, cat) >= 0) continue;
                    string ver = (Convert.ToString(J(i, "SummaryInfo", "Version")) ?? "").Trim();
                    bool isBios = cat == "BIOS/UEFI";
                    if (isBios)
                    {
                        LatestBios = new UpdateItem { Id = Convert.ToString(J(i, "DocId")), Title = "BIOS " + ver, Source = "LENOVO", Provider = "Lenovo", Inst = bios, Date = "", By = ver, BiosFlash = BiosFlashable(ver, bios) };
                        SetBiosFile(LatestBios, J(i, "Files") as object[], ver);
                    }
                    if (isBios && !BiosNewer(ver, bios)) continue;
                    string doc = Convert.ToString(J(i, "DocId"));
                    var it = new UpdateItem
                    {
                        Id = doc, Source = "LENOVO", Provider = "Lenovo",
                        Url = "https://pcsupport.lenovo.com/downloads/" + doc,
                        Title = isBios ? "BIOS " + ver : Text.Clean(J(i, "Title")) + (ver.Length > 0 && ver != "-" ? "  " + ver : ""),
                        Inst = isBios ? bios : null,
                        Date = ""
                    };
                    object ms = J(i, "Date", "Unix");
                    if (ms != null) it.Date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(Convert.ToDouble(ms)).ToLocalTime().ToString("yyyy-MM-dd");
                    res.Add(it);
                }
            }
            return res;
        }
    }

    // Instantaneous CPU, Adreno GPU and Hexagon NPU load from Windows performance counters (WMI classes
    // with English names even on Italian Windows). Qualcomm exposes neither temperatures nor GPU/NPU clocks.
    sealed class SocSample
    {
        public int Cpu, Perf, MHz, Gpu3d, GpuCompute, GpuVideo, Npu;
        public long GpuSharedMb;
        public List<int> Cores = new List<int>();

        public static SocSample Read()
        {
            var s = new SocSample();
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, PercentProcessorTime, ProcessorFrequency, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation"))
                    foreach (ManagementObject o in q.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        int t = Convert.ToInt32(o["PercentProcessorTime"]);
                        if (n == "_Total")
                        {
                            s.Cpu = t;
                            s.Perf = Convert.ToInt32(o["PercentProcessorPerformance"]);
                            s.MHz = (int)(Convert.ToInt64(o["ProcessorFrequency"]) * s.Perf / 100);
                        }
                        else if (!n.EndsWith("_Total")) s.Cores.Add(t);
                    }
            }
            catch { }
            try
            {
                // sum per adapter and engine type; the NPU is the adapter with Compute engines only
                var util = new Dictionary<string, Dictionary<string, int>>();
                using (var q = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
                    foreach (ManagementObject o in q.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        int a = n.IndexOf("luid_"), e = n.IndexOf("_engtype_");
                        if (a < 0 || e < 0) continue;
                        string luid = n.Substring(a, n.IndexOf("_phys", a) - a), type = n.Substring(e + 9);
                        if (!util.ContainsKey(luid)) util[luid] = new Dictionary<string, int>();
                        int v;
                        util[luid].TryGetValue(type, out v);
                        util[luid][type] = v + Convert.ToInt32(o["UtilizationPercentage"]);
                    }
                string gpu = null;
                foreach (var kv in util)
                {
                    int v;
                    if (kv.Value.ContainsKey("VideoDecode")) { gpu = kv.Key; s.Gpu3d = Get(kv.Value, "3D"); s.GpuCompute = Get(kv.Value, "Compute"); s.GpuVideo = Math.Max(Get(kv.Value, "VideoDecode"), Get(kv.Value, "VideoEncode")); }
                    else if (kv.Value.Count == 1 && kv.Value.TryGetValue("Compute", out v)) s.Npu = Math.Min(100, v);
                }
                if (gpu != null)
                    using (var q = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
                        foreach (ManagementObject o in q.Get())
                            if (Convert.ToString(o["Name"]).StartsWith(gpu)) s.GpuSharedMb = Convert.ToInt64(o["SharedUsage"]) / (1024 * 1024);
            }
            catch { }
            return s;
        }

        static int Get(Dictionary<string, int> d, string k) { int v; return d.TryGetValue(k, out v) ? Math.Min(100, v) : 0; }
    }

    static class Soc
    {
        // main Qualcomm devices: label, part of the name in Device Manager, English label
        static readonly string[][] Parts =
        {
            new[] { "GPU", "Adreno", "GPU" }, new[] { "NPU", "Hexagon", "NPU" }, new[] { "Wi-Fi", "Wi-Fi", "Wi-Fi" }, new[] { "Bluetooth", "Bluetooth Adapter", "Bluetooth" },
            new[] { "DSP audio", "Audio DSP Subsystem", "Audio DSP" }, new[] { "DSP calcolo", "Compute DSP", "Compute DSP" }, new[] { "Batteria", "Battery Miniclass", "Battery" },
            new[] { "Termico", "Subsys Thermal Mitigation", "Thermal" },
        };

        public static List<string[]> QualcommDrivers()
        {
            var rows = new List<string[]>();
            var all = new List<ManagementObject>();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DriverProviderName LIKE '%Qualcomm%'"))
                    foreach (ManagementObject o in q.Get()) all.Add(o);
            }
            catch { }
            foreach (var p in Parts)
                foreach (var o in all)
                {
                    string n = Convert.ToString(o["DeviceName"]) ?? "";
                    if (n.IndexOf(p[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string d = Convert.ToString(o["DriverDate"]);
                    try { d = ManagementDateTimeConverter.ToDateTime(d).ToString("yyyy-MM-dd"); } catch { d = ""; }
                    rows.Add(new[] { p[0], Convert.ToString(o["DriverVersion"]) + (d.Length > 0 ? "  ·  " + d : ""), p[2] });
                    break;
                }
            return rows;
        }

        // ------------------------------------------------ CPU profile in the active power plan
        static readonly Guid SubCpu = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        public static readonly Guid MaxState = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");   // maximum processor state, %
        public static readonly Guid Boost = new Guid("be337238-0d82-4146-a960-4f3749d470c7");      // boost: 0 no, 1 yes, 2 aggressive

        public static uint Read(Guid set, bool ac) { return Read(SubCpu, set, ac); }

        public static uint Read(Guid sub, Guid set, bool ac)
        {
            Guid s = NoStandby.Scheme();
            uint v;
            uint r = ac ? Native.PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out v) : Native.PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out v);
            return r == 0 ? v : uint.MaxValue;
        }

        public static void Write(Guid set, bool ac, uint value) { Write(SubCpu, set, ac, value); }

        public static void Write(Guid sub, Guid set, bool ac, uint value)
        {
            Guid s = NoStandby.Scheme();
            NoStandby.Check(ac ? Native.PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, value) : Native.PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, value));
            NoStandby.Check(Native.PowerSetActiveScheme(IntPtr.Zero, ref s));
        }

        // ------------------------------------------------ built-in display
        public static int Brightness()
        {
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                    foreach (ManagementObject o in q.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return -1;
        }

        public static void SetBrightness(int pct)
        {
            using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                foreach (ManagementObject o in q.Get()) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)pct });
        }

        static Native.DEVMODE Mode(int i)
        {
            var dm = new Native.DEVMODE();
            dm.Size = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            if (!Native.EnumDisplaySettings(null, i, ref dm)) dm.Size = 0;
            return dm;
        }

        public static int RefreshRate() { return Mode(-1).DisplayFrequency; }   // -1 = ENUM_CURRENT_SETTINGS

        // refresh rates available at the current resolution and color depth
        public static List<int> RefreshRates()
        {
            var cur = Mode(-1);
            var res = new List<int>();
            for (int i = 0; ; i++)
            {
                var m = Mode(i);
                if (m.Size == 0) break;
                if (m.PelsWidth == cur.PelsWidth && m.PelsHeight == cur.PelsHeight && m.BitsPerPel == cur.BitsPerPel && m.DisplayFrequency > 1 && !res.Contains(m.DisplayFrequency))
                    res.Add(m.DisplayFrequency);
            }
            res.Sort();
            return res;
        }

        public static int SetRefreshRate(int hz)
        {
            var dm = Mode(-1);
            dm.DisplayFrequency = hz;
            dm.Fields = 0x400000;   // DM_DISPLAYFREQUENCY
            return Native.ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
        }
    }

    // Screen, sleep, hibernate, power saving, lid and power-on in the active scheme.
    static class PowerSettings
    {
        public sealed class Row
        {
            // Name and Kind are command line and logic identifiers: never translated.
            public string Name, Kind;
            readonly string en, it;
            public string Title { get { return L.T(en, it); } }
            public Guid Sub, Set;
            public Row(string name, string titleEn, string titleIt, string kind, string sub, string set)
            {
                Name = name; en = titleEn; it = titleIt; Kind = kind; Sub = new Guid(sub); Set = new Guid(set);
            }
        }

        const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20", SubButtons = "4f971e89-eebd-4455-a8de-9e59040e7347";

        public static readonly Row[] Rows =
        {
            new Row("schermo", "SCREEN", "SCHERMO", "tempo", "7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"),
            new Row("sospensione", "SLEEP", "SOSPENSIONE", "tempo", SubSleep, "29f6c1db-86da-48c5-9fdb-f2b67b1f44da"),
            new Row("ibernazione", "HIBERNATE", "IBERNAZIONE", "tempo", SubSleep, "9d7815a6-7ee4-497e-8888-515a05f02364"),
            new Row("risparmio", "SAVER", "RISPARMIO", "soglia", "de830923-a562-41af-a086-e3a2c6bad2da", "e69653ca-cf7f-4f05-aa73-cb833fa90ad4"),
            new Row("coperchio", "LID", "COPERCHIO", "azione", SubButtons, "5ca83367-6e45-459f-a27b-476b1d01c936"),
            new Row("accensione", "POWER BTN", "ACCENSIONE", "azione", SubButtons, "7648efa3-dd9c-4e3e-b566-50f929386280"),
        };

        // null if the value is not valid for that type
        public static string Label(string kind, uint v)
        {
            if (kind == "soglia") return v == 0 ? L.T("Never", "Mai") : v == 100 ? L.T("Always", "Sempre") : v < 100 ? v + "%" : null;
            if (kind == "azione")
            {
                string[] actions = { L.T("No action", "Nessuna azione"), L.T("Sleep", "Sospensione"), L.T("Hibernate", "Ibernazione"), L.T("Shut down", "Arresto"), L.T("Screen off", "Solo schermo") };
                return v < actions.Length ? actions[v] : null;
            }
            if (kind != "tempo") return null;
            if (v == 0) return L.T("Never", "Mai");
            var parts = new List<string>();
            if (v >= 3600) parts.Add(v / 3600 + " h");
            if (v % 3600 >= 60) parts.Add(v % 3600 / 60 + " min");
            if (v % 60 > 0) parts.Add(v % 60 + " s");
            return string.Join(" ", parts);
        }

        static bool Valid(Row r, uint v)
        {
            return v != uint.MaxValue && Label(r.Kind, v) != null && (r.Name != "coperchio" || v != 4);
        }

        // Window choices; the current value, if not among these, is appended at the end.
        public static List<uint> Choices(Row r, uint current, bool hibernate)
        {
            var res = new List<uint>();
            if (r.Kind == "tempo") res.AddRange(new uint[] { 60, 180, 300, 600, 900, 1800, 0 });
            else if (r.Kind == "soglia") res.AddRange(new uint[] { 0, 10, 20, 30, 50, 100 });
            else
            {
                res.AddRange(new uint[] { 0, 1 });
                if (hibernate) res.Add(2);
                res.Add(3);
                if (r.Name == "accensione") res.Add(4);
            }
            if (!res.Contains(current)) res.Add(current);
            return res;
        }

        public static bool HibernateAvailable()
        {
            // SYSTEM_POWER_CAPABILITIES: byte 6 = SystemS4, byte 8 = HiberFilePresent
            var caps = new byte[256];
            try { return Native.GetPwrCapabilities(caps) && caps[6] != 0 && caps[8] != 0; }
            catch { return false; }
        }

        public static uint Read(Row r, bool ac)
        {
            uint v = Soc.Read(r.Sub, r.Set, ac);
            if (v == uint.MaxValue) throw new Exception(L.T("read failed: " + r.Title, "lettura non riuscita: " + r.Name));
            return v;
        }

        // Writes, applies and returns the value read back. With "never sleep" on its rows
        // do not change: re-enabling it would save the manual value in place of those to restore.
        public static uint Write(Row r, bool ac, uint value)
        {
            if (NoStandby.Controls(r.Set) && NoStandby.Enabled && value != Read(r, ac))
                throw new InvalidOperationException(L.T("read-only while \"Never sleep\" is on", "sola lettura finché è attivo «mai in standby»"));
            // 0/0 is what NoStandby.Enabled reads as on, with stale saved values
            if (r.Name == "sospensione" && value == 0 && Read(r, !ac) == 0)
                throw new InvalidOperationException(L.T("\"Never\" on both AC and battery is set with the \"Never sleep\" check", "«Mai» su rete e batteria si imposta con la spunta «Mai in standby»"));
            Soc.Write(r.Sub, r.Set, ac, value);
            return Read(r, ac);
        }

        // ---------------------------------------------------------------- command line

        public static int Cli(string[] a)
        {
            if (a[0] == "--power-label" && a.Length == 1) return CliLabel();
            if (a[0] == "--power-settings" && a.Length == 2) return CliSettings(a[1]);
            if (a[0] == "--power-set" && a.Length == 4) return CliSet(a[1], a[2], a[3]);
            return 1;
        }

        static bool ParseUint(string s, out uint v)
        {
            return uint.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        public const int StdOut = -11, StdErr = -12;

        // Direct WriteFile: .NET streams silently ignore a closed pipe.
        public static void Send(int std, string s)
        {
            byte[] b = new UTF8Encoding(false).GetBytes(s);
            int written;
            if (!Native.WriteFile(Native.GetStdHandle(std), b, b.Length, out written, IntPtr.Zero) || written != b.Length)
                throw new System.IO.IOException("scrittura non riuscita");
        }

        // All or nothing: with an invalid row stdout stays empty.
        static int CliLabel()
        {
            try { return CliLabelCore(); }
            catch { return 1; }   // stdin, stdout or stderr unusable
        }

        static int CliLabelCore()
        {
            var sb = new StringBuilder();
            using (var input = new System.IO.StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
            {
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    string[] p = line.Split(' ');
                    uint v;
                    string label = p.Length == 2 && ParseUint(p[1], out v) ? Label(p[0], v) : null;
                    if (label == null)
                    {
                        Send(StdErr, "riga non valida: " + line + "\n");
                        return 1;
                    }
                    sb.Append(label).Append('\n');
                }
            }
            Send(StdOut, sb.ToString());
            return 0;
        }

        static int CliSettings(string file)
        {
            int code = 0;
            string text;
            try
            {
                var sb = new StringBuilder();
                foreach (var r in Rows) sb.Append(r.Name + " " + Read(r, true) + " " + Read(r, false) + "\n");
                text = sb.ToString();
            }
            catch (Exception ex) { text = ex.Message + "\n"; code = 1; }
            try { System.IO.File.WriteAllText(file, text); }
            catch { code = 1; }
            return code;
        }

        static int CliSet(string name, string power, string value)
        {
            Row row = Array.Find(Rows, r => r.Name == name);
            uint v;
            if (row == null || (power != "ca" && power != "cc") || !ParseUint(value, out v) || !Valid(row, v)) return 1;
            try { return Write(row, power == "ca", v) == v ? 0 : 2; }
            catch { return 1; }
        }
    }

    // Share of each app's own energy (screen and losses excluded) over the last 24 hours on battery, from the powercfg /srumutil CSV.
    static class AppUsage
    {
        public sealed class Entry
        {
            public string Name;
            public decimal Energy;   // decimal: the sum of 64-bit integers can exceed long
        }

        sealed class Report
        {
            public List<Entry> Entries;
            public decimal Screen, Totals;   // screen + losses and totals of counted rows; Totals 0 = no rows
            public int Ignored;
        }

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        static readonly string[] Sep = { ", " };   // a comma without a space is part of the value

        static bool Long(string s, out long v)
        {
            return long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, Inv, out v);
        }

        // null if the header is invalid. Entry order: own energy descending, then name.
        static Report Parse(System.IO.TextReader input, DateTime instant)
        {
            string head = input.ReadLine();
            if (head == null) return null;
            string[] cols = head.Split(Sep, StringSplitOptions.None);
            int cApp = Array.IndexOf(cols, "AppId"), cTime = Array.IndexOf(cols, "TimeStamp"), cBat = Array.IndexOf(cols, "OnBattery");
            int cTotal = Array.IndexOf(cols, "TotalEnergyConsumption"), cDisplay = Array.IndexOf(cols, "DisplayEnergyConsumption"), cLoss = Array.IndexOf(cols, "EnergyLoss");
            if (cApp < 0 || cTime < 0 || cBat < 0 || cTotal < 0 || cDisplay < 0 || cLoss < 0) return null;

            var rep = new Report();
            var byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] f = line.Split(Sep, StringSplitOptions.None);
                int extra = f.Length - cols.Length;   // extra fields: the AppId contains ", "
                if (extra < 0) { rep.Ignored++; continue; }
                Func<int, string> at = c => f[c < cApp ? c : c + extra];
                DateTime t;
                long total, display, loss;
                if (!DateTime.TryParseExact(at(cTime), "yyyy-MM-ddTHH:mm:ss.FFFFFFF", Inv, System.Globalization.DateTimeStyles.None, out t)
                    || !Long(at(cTotal), out total) || !Long(at(cDisplay), out display) || !Long(at(cLoss), out loss))
                {
                    rep.Ignored++;
                    continue;
                }
                decimal screen = (decimal)display + loss;   // two longs can exceed long
                // A non-positive integer total is never a skipped row, whatever the components.
                if (total > 0 && (display < 0 || loss < 0 || screen > total)) { rep.Ignored++; continue; }
                string id = string.Join(", ", f, cApp, extra + 1);
                // difference and not instant.AddHours(-24): near DateTime.MinValue it would go out of range
                TimeSpan age = instant - t;
                if (at(cBat) != "TRUE" || id.StartsWith("EMI_", StringComparison.Ordinal) || total <= 0
                    || age < TimeSpan.Zero || age >= TimeSpan.FromHours(24))
                    continue;
                rep.Screen += screen;
                rep.Totals += total;
                decimal own = total - screen;
                if (own == 0) continue;   // counts only in the screen row
                string name = Name(id);
                Entry entry;
                if (!byName.TryGetValue(name, out entry)) byName[name] = entry = new Entry { Name = name };
                entry.Energy += own;
            }
            rep.Entries = new List<Entry>(byName.Values);
            rep.Entries.Sort((a, b) =>
            {
                int c = b.Energy.CompareTo(a.Energy);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return rep;
        }

        static string Name(string id)
        {
            if (id.StartsWith("\\", StringComparison.Ordinal)) return id.Substring(id.LastIndexOf('\\') + 1);
            int u = id.IndexOf('_');
            return u >= 0 ? id.Substring(0, u) : id;
        }

        static decimal Total(List<Entry> list)
        {
            decimal total = 0;
            foreach (var en in list) total += en.Energy;
            return total;
        }

        // Integer tenths, no double: 6.25 must give 6.3 and not depend on the binary representation.
        static string Percent(decimal energy, decimal total)
        {
            return (Math.Floor((energy * 2000 + total) / (2 * total)) / 10m).ToString("0.0", Inv);
        }

        // ---------------------------------------------------------------- window

        // From a worker thread: requests elevation and blocks until done. Null rows = error, explained in status.
        // Status is a function: on language change it is rewritten without redoing the export.
        public static List<string[]> Refresh(out Func<string> status)
        {
            Func<string> text, why = null;
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapControl");
            string file = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".csv");
            List<string[]> rows = null;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--app-usage-export \"" + file + "\"") { Verb = "runas", UseShellExecute = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) { why = () => L.T("powercfg did not produce the file", "powercfg non ha prodotto il file"); throw new Exception(); }
                }
                Report rep;
                using (var reader = System.IO.File.OpenText(file)) rep = Parse(reader, DateTime.UtcNow);
                if (rep == null) { why = () => L.T("invalid header", "intestazione non valida"); throw new Exception(); }
                rows = Rows(rep.Entries);
                bool none = rep.Totals == 0;
                string time = DateTime.Now.ToString("HH:mm", Inv), screen = none ? "" : Percent(rep.Screen, rep.Totals);
                int ignored = rep.Ignored;
                text = () => (none ? L.T("No battery data in the last 24 hours", "Nessun dato a batteria nelle ultime 24 ore")
                    : L.T("Updated at ", "Aggiornato alle ") + time + L.T(" · Screen and losses: ", " · Schermo e perdite: ") + screen + L.T("% of total energy", "% dell'energia totale"))
                    + (ignored > 0 ? L.T(" · rows ignored: ", " · righe ignorate: ") + ignored : "");
            }
            catch (System.ComponentModel.Win32Exception) { text = () => L.T("Administrator permission not granted", "Permesso di amministratore non concesso"); }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Func<string> reason = why;
                text = () => L.T("Export failed: ", "Esportazione non riuscita: ") + (reason != null ? reason() : msg);
            }
            // Contains the list of programs used: if it stays, it must be said.
            bool kept = false;
            try { System.IO.File.Delete(file); }
            catch { kept = true; }
            Func<string> done = text;
            status = () => done() + (kept ? L.T(" · temporary file not deleted", " · file temporaneo non eliminato") : "");
            return rows;
        }

        static List<string[]> Rows(List<Entry> list)
        {
            decimal total = Total(list), rest = 0;
            var rows = new List<string[]>();
            for (int i = 0; i < list.Count; i++)
            {
                // & is a prefix for TextRenderer: a name like A&B would lose the character
                if (i < 10) rows.Add(new[] { Percent(list[i].Energy, total) + "%", list[i].Name.Replace("&", "&&") });
                else rest += list[i].Energy;
            }
            if (rest > 0) rows.Add(new[] { Percent(rest, total) + "%", null });   // null = "Others" row, written on screen in the active language
            return rows;
        }

        // ---------------------------------------------------------------- command line

        public static int Cli(string[] a)
        {
            try
            {
                if (a[0] == "--app-usage") return CliUsage(a);
                if (a[0] == "--app-usage-export" && a.Length == 2) return Export(a[1]);
            }
            catch { }   // stdin or stdout unusable
            return 1;
        }

        // Stderr does not change the outcome: if closed only the exit code counts.
        static void Err(string s)
        {
            try { PowerSettings.Send(PowerSettings.StdErr, s + "\n"); }
            catch (System.IO.IOException) { }
        }

        static int CliUsage(string[] a)
        {
            DateTime instant;
            if (a.Length != 2 || !DateTime.TryParseExact(a[1], "yyyy-MM-ddTHH:mm:ss", Inv, System.Globalization.DateTimeStyles.None, out instant))
            {
                Err("istante non valido");
                return 1;
            }
            Report rep;
            using (var input = new System.IO.StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                rep = Parse(input, instant);
            if (rep == null)
            {
                Err("intestazione non valida");
                return 1;
            }
            decimal total = Total(rep.Entries);
            var sb = new StringBuilder();
            foreach (var en in rep.Entries) sb.Append(Percent(en.Energy, total)).Append(' ').Append(en.Name).Append('\n');
            if (rep.Totals > 0) sb.Append("schermo ").Append(Percent(rep.Screen, rep.Totals)).Append('\n');
            if (sb.Length > 0) PowerSettings.Send(PowerSettings.StdOut, sb.ToString());
            if (rep.Ignored > 0) Err("righe ignorate: " + rep.Ignored);
            return 0;
        }

        // The old file is deleted first: if powercfg fails it must not pass for a successful export.
        static int Export(string file)
        {
            // the target is in a folder the user controls: this elevated process only ever creates a new file there
            ChargeLimit.SecureDataDir();
            string work = System.IO.Path.Combine(ChargeLimit.DataDir, "work");
            System.IO.Directory.CreateDirectory(work);
            string tmp = System.IO.Path.Combine(work, Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/srumutil /output \"" + tmp + "\" /csv") { UseShellExecute = false, CreateNoWindow = true };
                using (var p = System.Diagnostics.Process.Start(psi)) p.WaitForExit();
                if (!System.IO.File.Exists(tmp)) return 1;
                System.IO.File.Copy(tmp, file, false);
                return 0;
            }
            finally { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
        }
    }

    sealed class MainForm : Form
    {
        static string[] Pages { get { return new[] { L.T("SYSTEM", "SISTEMA"), L.T("BATTERY", "BATTERIA"), L.T("DRIVERS", "DRIVER"), "SNAPDRAGON", L.T("CONTROLS", "CONTROLLI") }; } }
        static string[] Titles { get { return new[] { L.T("System", "Sistema"), L.T("Battery", "Batteria"), L.T("Drivers", "Driver"), "Snapdragon", L.T("Controls", "Controlli") }; } }

        int themeIdx = Theme.Load();
        int page = 1;
        List<string[]> sysRows = new List<string[]>();
        BatteryInfo bat = new BatteryInfo();
        readonly Autonomy autonomy = new Autonomy();
        Guid mode;
        bool trayMode = Settings.Tray, noStandby = NoStandby.Enabled, startHidden, inited, quitting;
        NotifyIcon tray;
        IntPtr trayIconHandle;
        string trayIconKey;
        List<DriverRow> drivers;
        Func<string> drvStatus = () => "";                     // functions: the text follows the language without redoing searches or elevations
        int drvScroll;
        int drvSort = -1;                                      // -1 = default order (BIOS, then vendor)
        bool drvDesc;
        bool wuBusy, installing;
        List<UpdateItem> updates;                              // null = search not done yet
        readonly HashSet<string> selected = new HashSet<string>();
        readonly HashSet<string> flagged = new HashSet<string>(Settings.Flagged);
        bool biosBusy, chargeBusy;
        UpdateItem biosLatest;                                 // null = not asked yet
        SocSample soc;
        List<string[]> qcRows;
        bool socBusy;
        readonly System.Windows.Forms.Timer liveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        uint[] cpuVals;                                        // max CPU battery/AC, boost battery/AC
        int bright = -1, hz;
        List<int> rates;
        bool ctlPower;                                         // Controls page: power view
        uint[] powerVals;                                      // AC and DC for each PowerSettings row; MaxValue = unreadable
        bool hibernate;
        bool ctlApps;                                          // Controls page: per-app usage view
        bool appBusy;
        List<string[]> appRows;
        Func<string> appStatus = () => L.T("Press REFRESH to read the data: administrator permission is required", "Premi AGGIORNA per leggere i dati: serve il permesso di amministratore");

        readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();
        Rectangle hover = Rectangle.Empty;
        Rectangle listArea = Rectangle.Empty;
        int listVisible;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 5000 };
        Font fMono, fMonoBold, fMonoSmall, fTitle, fBody;

        Theme T { get { return Theme.All[themeIdx]; } }
        float S { get { return DeviceDpi / 96f; } }
        int Px(float v) { return (int)Math.Round(v * S); }

        public MainForm() : this(false) { }

        public MainForm(bool hidden)
        {
            startHidden = hidden && trayMode;
            Text = "SnapControl";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }   // app.ico embedded by build.cmd
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            MakeFonts();
            ClientSize = new Size(Px(1000), Px(820));
            // do not exceed the screen work area (e.g. 1920x1200 at 150% = 1280x752 usable)
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (Height > wa.Height) Height = wa.Height;
            if (Width > wa.Width) Width = wa.Width;
            MinimumSize = new Size(Px(680), Px(480));
            // The battery is always read (even with the window hidden) to keep the power average ready.
            timer.Tick += delegate
            {
                RefreshBattery();
                UpdateTray();
                if (page == 1 && Visible && WindowState != FormWindowState.Minimized) Invalidate();
                if (page == 4 && ctlPower && Visible && WindowState != FormWindowState.Minimized) { RefreshPower(); Invalidate(); }
            };
            liveTimer.Tick += delegate { if (page == 3 && Visible && WindowState != FormWindowState.Minimized) RefreshSoc(); };
            Shown += delegate { Init(); Invalidate(); };
            if (trayMode) CreateTray();
        }

        void Init()
        {
            if (inited) return;
            inited = true;
            RefreshSystem();
            RefreshBattery();
            UpdateTray();
            timer.Start();
        }

        // Started with --tray: the window does not appear, only the icon near the clock remains.
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                value = false;
                if (!IsHandleCreated) CreateHandle();
                Init();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (trayMode && !quitting && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None))
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DisposeTray();
            base.OnFormClosed(e);
        }

        // ---------------------------------------------------------------- tray

        public void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void CreateTray()
        {
            if (tray != null) return;
            var menu = new ContextMenuStrip();
            menu.Items.Add("", null, delegate { ShowWindow(); });
            menu.Items.Add(new ToolStripSeparator());
            var modes = new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance };
            foreach (var m in modes)
            {
                Guid g = m;
                var item = new ToolStripMenuItem(PowerMode.Name(g), null, delegate { SetMode(g); UpdateTray(); }) { Tag = g };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("", null, delegate { quitting = true; Close(); });
            menu.Opening += delegate
            {
                RefreshBattery();
                foreach (ToolStripItem it in menu.Items)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null && mi.Tag is Guid) mi.Checked = (Guid)mi.Tag == mode;
                }
            };
            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = false };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            trayIconKey = null;
            TrayTexts();
            UpdateTray();
            tray.Visible = true;
        }

        void TrayTexts()
        {
            if (tray == null) return;
            var items = tray.ContextMenuStrip.Items;
            items[0].Text = L.T("Open SnapControl", "Apri SnapControl");
            items[items.Count - 1].Text = L.T("Exit", "Esci");
            foreach (ToolStripItem it in items) if (it.Tag is Guid) it.Text = PowerMode.Name((Guid)it.Tag);
        }

        // Drawn text follows L by itself; here only text held in fields. Local rereads: no network, no elevation.
        void ToggleLang()
        {
            L.It = !L.It;
            Settings.Italian = L.It;
            RefreshSystem();
            TrayTexts();
            UpdateTray();
        }

        void DisposeTray()
        {
            if (tray == null) return;
            tray.Visible = false;
            tray.Dispose();
            tray = null;
            if (trayIconHandle != IntPtr.Zero) { Native.DestroyIcon(trayIconHandle); trayIconHandle = IntPtr.Zero; }
        }

        void ToggleTray()
        {
            trayMode = !trayMode;
            Settings.Tray = trayMode;
            if (trayMode) CreateTray(); else DisposeTray();
        }

        void ToggleNoStandby()
        {
            try { NoStandby.Set(!NoStandby.Enabled); }
            catch (Exception ex) { MessageBox.Show(this, L.T("Could not change standby: ", "Impossibile cambiare lo standby: ") + ex.Message, "SnapControl"); }
            RefreshPower();
            Invalidate();
        }

        static bool TaskbarLight()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { return false; }
        }

        // Icon with the percentage, redrawn only on change; tooltip with the estimated runtime.
        void UpdateTray()
        {
            if (tray == null) return;
            string tip = "SnapControl · " + (bat.Percent >= 0 ? bat.Percent + "%" : "--%");
            string est = autonomy.Short(bat);
            if (est.Length > 0) tip += " · " + est;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;   // NotifyIcon limit in .NET Framework

            bool light = TaskbarLight();
            string key = bat.Percent + "|" + bat.Charging + "|" + light + "|" + DeviceDpi;
            if (key == trayIconKey) return;
            trayIconKey = key;
            int sz = Px(16);
            using (var bmp = new Bitmap(sz, sz))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    string s = bat.Percent >= 0 ? Math.Min(bat.Percent, 100).ToString() : "--";
                    float size = sz * (s.Length >= 3 ? 0.52f : 0.72f);
                    using (var f = new Font("Segoe UI Variable Display", size, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(light ? Color.FromArgb(20, 20, 19) : Color.White))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(s, f, b, new RectangleF(0, -sz * 0.06f, sz, sz), sf);
                    // charging: bar in the LED color under the digit
                    if (bat.Charging) using (var a = new SolidBrush(T.Accent)) g.FillRectangle(a, sz / 5, sz - Math.Max(2, sz / 8), sz * 3 / 5, Math.Max(2, sz / 8));
                }
                IntPtr h = bmp.GetHicon();
                var old = trayIconHandle;
                tray.Icon = Icon.FromHandle(h);
                trayIconHandle = h;
                if (old != IntPtr.Zero) Native.DestroyIcon(old);
            }
        }

        void MakeFonts()
        {
            fMono = new Font("Cascadia Mono SemiBold", Px(13.5f), GraphicsUnit.Pixel);
            fMonoBold = new Font("Cascadia Mono", Px(13.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            fMonoSmall = new Font("Cascadia Mono SemiBold", Px(12.5f), GraphicsUnit.Pixel);
            fTitle = new Font("Segoe UI Variable Display", Px(42), FontStyle.Bold, GraphicsUnit.Pixel);
            fBody = new Font("Segoe UI Variable Text Semibold", Px(16), GraphicsUnit.Pixel);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.TitleBar(Handle, T);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            MakeFonts();
            Invalidate();
        }

        // ---------------------------------------------------------------- data

        void RefreshSystem() { sysRows = SystemInfo.Rows(); }

        void RefreshBattery()
        {
            bat = BatteryInfo.Read();
            mode = PowerMode.Current();
            autonomy.Add(bat, mode);
        }

        void LoadDrivers()
        {
            drivers = Drivers.ReadOem();
            if (drvSort >= 0) Drivers.Sort(drivers, drvSort, drvDesc);
            drvScroll = 0;
            if (updates == null) drvStatus = () => L.T("Press \"Check for updates\": Windows Update, Lenovo (BIOS, drivers), Microsoft catalog", "Premi «Cerca aggiornamenti» per controllare Windows Update, Lenovo (BIOS e driver) e il catalogo Microsoft");
        }

        // Same column: reverses. New column: date starts newest first, the others ascending.
        void SortDrivers(int col)
        {
            drvDesc = col == drvSort ? !drvDesc : col == 3;
            drvSort = col;
            Drivers.Sort(drivers, drvSort, drvDesc);
            drvScroll = 0;
        }

        void Background(ThreadStart work)
        {
            var th = new Thread(work) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void SearchUpdates()
        {
            if (wuBusy) return;
            wuBusy = true;
            drvStatus = () => L.T("Searching Windows Update, Lenovo and the Microsoft catalog…", "Ricerca su Windows Update, Lenovo e catalogo Microsoft in corso…");
            Invalidate();
            Background(delegate
            {
                var notes = new List<Func<string>>();
                var res = Drivers.SearchUpdates(notes);
                BeginInvoke((MethodInvoker)delegate
                {
                    wuBusy = false;
                    updates = res;
                    biosLatest = Lenovo.LatestBios;
                    selected.Clear();
                    // catalog drivers (from other OEMs) are not preselected
                    foreach (var u in res) if (u.Source == "WINDOWS UPDATE" && !flagged.Contains(u.Id)) selected.Add(u.Id);
                    drvStatus = () => string.Join(" · ", notes.ConvertAll(n => n()));
                    Invalidate();
                });
            });
        }

        // Install requires administrator: SnapControl restarts itself elevated for this step only.
        void InstallUpdates(List<string> ids)
        {
            if (installing || ids.Count == 0) return;
            // the Windows prompt is not always there: without it this would be the only confirmation
            if (MessageBox.Show(this, L.T("Install " + ids.Count + " driver update(s)?", "Installare " + ids.Count + " aggiornamenti driver?"),
                    "SnapControl", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            installing = true;
            drvStatus = () => L.T("Installing " + ids.Count + (ids.Count == 1 ? " driver" : " drivers") + "…", "Installazione di " + ids.Count + " driver in corso…");
            Invalidate();
            Background(delegate
            {
                string msg;
                string job = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snapcontrol_" + Guid.NewGuid().ToString("N"));
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--wu-install \"" + job + "\" " + string.Join(" ", ids)) { Verb = "runas", UseShellExecute = true };
                    using (var p = System.Diagnostics.Process.Start(psi)) p.WaitForExit();
                    msg = System.IO.File.Exists(job + ".out") ? System.IO.File.ReadAllText(job + ".out") : L.T("Installation interrupted.", "Installazione interrotta.");
                }
                catch (System.ComponentModel.Win32Exception) { msg = L.T("Installation cancelled: administrator permission not granted.", "Installazione annullata: permesso di amministratore non concesso."); }
                catch (Exception ex) { msg = L.T("Installation failed: ", "Installazione non riuscita: ") + ex.Message; }
                finally
                {
                    try { System.IO.File.Delete(job + ".out"); } catch { }
                }
                BeginInvoke((MethodInvoker)delegate
                {
                    installing = false;
                    string first = msg.Split('\n')[0];
                    drvStatus = () => first;
                    drivers = null;
                    updates = null;
                    Invalidate();
                    if (msg.Contains("\n")) MessageBox.Show(this, msg, L.T("SnapControl · drivers", "SnapControl · driver"));
                });
            });
        }

        void CheckBios()
        {
            if (biosBusy) return;
            biosBusy = true;
            drvStatus = () => L.T("Asking Lenovo for the latest BIOS…", "Richiesta a Lenovo dell'ultimo BIOS…");
            Invalidate();
            Background(delegate
            {
                string err = null;
                try { Lenovo.Search(); }
                catch (Exception ex) { err = ex.Message.Trim(); }
                BeginInvoke((MethodInvoker)delegate
                {
                    biosBusy = false;
                    biosLatest = Lenovo.LatestBios;
                    if (err != null) drvStatus = () => L.T("Lenovo unreachable (", "Lenovo non raggiungibile (") + err + ")";
                    else if (biosLatest == null) drvStatus = () => L.T("Lenovo lists no BIOS for this model.", "Lenovo non elenca un BIOS per questo modello.");
                    else drvStatus = () => L.T("Lenovo latest BIOS: ", "Ultimo BIOS Lenovo: ") + biosLatest.By;
                    Invalidate();
                });
            });
        }

        // Installer offered by Lenovo, shown to the user: no silent flash, and the file is checked again right before it starts.
        void BiosAction(UpdateItem u, bool install)
        {
            if (biosBusy) return;
            biosBusy = true;
            drvStatus = () => L.T("Downloading and checking the BIOS package…", "Download e verifica del pacchetto BIOS…");
            Invalidate();
            Background(delegate
            {
                string err;
                try { err = Lenovo.EnsureBios(u); }
                catch (Exception ex) { err = ex.Message; }
                BeginInvoke((MethodInvoker)delegate
                {
                    biosBusy = false;
                    string path = Lenovo.BiosPath(u);
                    if (err != null) drvStatus = () => L.T("BIOS package refused: ", "Pacchetto BIOS rifiutato: ") + err;
                    else if (install) StartBiosInstaller(u, path);
                    else drvStatus = () => L.T("BIOS package checked and saved: ", "Pacchetto BIOS verificato e salvato: ") + path;
                    Invalidate();
                });
            });
        }

        void StartBiosInstaller(UpdateItem u, string path)
        {
            if (!u.BiosFlash) return;
            if (MessageBox.Show(this, L.T(
                    "Start the Lenovo installer for " + u.Title + "?\n\n• Do not turn off or restart the PC during the update: it can damage the laptop.\n• BIOS settings return to their defaults.\n• Lenovo says not to go back to an earlier BIOS.\n• The PC restarts by itself: save your work.\n• If BitLocker or device encryption is on, have the recovery key at hand.",
                    "Avviare l'installer Lenovo per " + u.Title + "?\n\n• Non spegnere né riavviare il PC durante l'aggiornamento: può danneggiare il laptop.\n• Le impostazioni del BIOS tornano ai valori predefiniti.\n• Lenovo dice di non tornare a un BIOS precedente.\n• Il PC si riavvia da solo: salva il lavoro.\n• Se BitLocker o la crittografia del dispositivo sono attivi, tieni pronta la chiave di ripristino."),
                    "SnapControl · BIOS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            // after the question: the cable can be pulled while it is open
            var b = BatteryInfo.Read();
            // shortcut: 30% is our margin, the Lenovo readme gives no threshold; raise it if Lenovo publishes one
            if (!b.OnAc || b.Percent < 30)
            {
                MessageBox.Show(this, L.T("Connect the charger and charge the battery to at least 30% before updating the BIOS.", "Collega l'alimentatore e carica la batteria almeno al 30% prima di aggiornare il BIOS."), "SnapControl · BIOS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                drvStatus = () => L.T("BIOS update not started: charger or battery level.", "Aggiornamento BIOS non avviato: alimentatore o livello batteria.");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--bios-run \"" + path + "\"") { Verb = "runas", UseShellExecute = true }).Dispose();
                drvStatus = () => L.T("Lenovo installer requested: follow its window.", "Installer Lenovo richiesto: segui la sua finestra.");
            }
            catch (System.ComponentModel.Win32Exception) { drvStatus = () => L.T("BIOS update cancelled: administrator permission not granted.", "Aggiornamento BIOS annullato: permesso di amministratore non concesso."); }
        }

        static void Open(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public string Report()
        {
            RefreshSystem();
            RefreshBattery();
            LoadDrivers();
            var sb = new StringBuilder();
            sb.AppendLine(L.T("== System ==", "== Sistema =="));
            foreach (var r in sysRows) sb.AppendLine(r[0].PadRight(16) + r[1]);
            sb.AppendLine(L.T("== Battery ==", "== Batteria =="));
            sb.AppendLine(L.T("Power mode: ", "Modalità energia: ") + PowerMode.Name(mode));
            sb.AppendLine(bat.Percent + "% · " + bat.State);
            foreach (var r in bat.Rows(autonomy.Text(bat))) sb.AppendLine(r[0].PadRight(16) + r[1]);
            sb.AppendLine(bat.Advice());
            sb.AppendLine(L.T("== Drivers ==", "== Driver ==")).AppendLine(drvStatus());
            var s = SocSample.Read();
            sb.AppendLine("== Snapdragon ==");
            sb.AppendLine(string.Format(L.T("CPU {0}% · {1} MHz · performance {2}% · cores [{3}]", "CPU {0}% · {1} MHz · prestazioni {2}% · core [{3}]"), s.Cpu, s.MHz, s.Perf, string.Join(" ", s.Cores)));
            sb.AppendLine(string.Format(L.T("GPU {0}% 3D · {1}% compute · {2}% video · {3} MB · NPU {4}%", "GPU {0}% 3D · {1}% calcolo · {2}% video · {3} MB · NPU {4}%"), s.Gpu3d, s.GpuCompute, s.GpuVideo, s.GpuSharedMb, s.Npu));
            foreach (var r in Soc.QualcommDrivers()) sb.AppendLine(L.T(r[2], r[0]).PadRight(16) + r[1]);
            RefreshControls();
            sb.AppendLine(L.T("== Controls ==", "== Controlli =="));
            sb.AppendLine(L.T("Max CPU battery/AC: ", "Max CPU batteria/rete: ") + cpuVals[0] + "/" + cpuVals[1] + L.T(" · boost battery/AC: ", " · boost batteria/rete: ") + cpuVals[2] + "/" + cpuVals[3]);
            sb.AppendLine(L.T("Brightness ", "Luminosità ") + bright + "% · " + hz + L.T(" Hz · available: ", " Hz · disponibili: ") + string.Join(", ", rates));
            return sb.ToString();
        }

        // ---------------------------------------------------------------- input

        void Hit(Rectangle r, Action a) { hits.Add(new KeyValuePair<Rectangle, Action>(r, a)); }

        // pill with checkbox in the top bar, right-aligned on "right"
        Rectangle CheckPill(Graphics g, string label, bool on, int right, Rectangle row, Action click)
        {
            int ckW = TxtW(label, fMono) + Px(40);
            var ck = new Rectangle(right - ckW, row.Y, ckW, row.Height);
            using (var path = Pill(ck))
            {
                if (ck == hover) using (var b = new SolidBrush(T.Band)) g.FillPath(b, path);
                using (var p = new Pen(T.LineStrong, 1)) g.DrawPath(p, path);
            }
            var box = new Rectangle(ck.X + Px(12), ck.Y + (ck.Height - Px(13)) / 2, Px(13), Px(13));
            using (var p = new Pen(on ? T.Accent : T.LineStrong, Px(1.5f))) g.DrawRectangle(p, box);
            if (on) using (var b = new SolidBrush(T.Accent)) g.FillRectangle(b, Rectangle.Inflate(box, -Px(3), -Px(3)));
            Txt(g, label, fMono, T.Text, new Rectangle(box.Right + Px(8), ck.Y, ckW - Px(33), ck.Height));
            Hit(ck, click);
            return ck;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle h = Rectangle.Empty;
            foreach (var kv in hits) if (kv.Key.Contains(e.Location)) h = kv.Key;
            Cursor = h.IsEmpty ? Cursors.Default : Cursors.Hand;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!hover.IsEmpty) { hover = Rectangle.Empty; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var kv in hits.ToArray()) if (kv.Key.Contains(e.Location)) { kv.Value(); Invalidate(); return; }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (page != 2 || drivers == null) return;
            int max = Math.Max(0, drivers.Count - listVisible);
            drvScroll = Math.Max(0, Math.Min(max, drvScroll - Math.Sign(e.Delta) * 3));
            Invalidate();
        }

        void Go(int p)
        {
            page = p;
            if (p == 1) RefreshBattery();
            if (p == 2 && drivers == null) LoadDrivers();
            if (p == 3) { if (qcRows == null) qcRows = Soc.QualcommDrivers(); RefreshSoc(); liveTimer.Start(); }
            else liveTimer.Stop();
            if (p == 4) RefreshControls();
        }

        // WMI counters can take a few hundred ms: read off the window thread.
        void RefreshSoc()
        {
            if (socBusy) return;
            socBusy = true;
            Background(delegate
            {
                var s = SocSample.Read();
                BeginInvoke((MethodInvoker)delegate { soc = s; socBusy = false; Invalidate(); });
            });
        }

        void RefreshControls()
        {
            cpuVals = new[] { Soc.Read(Soc.MaxState, false), Soc.Read(Soc.MaxState, true), Soc.Read(Soc.Boost, false), Soc.Read(Soc.Boost, true) };
            bright = Soc.Brightness();
            hz = Soc.RefreshRate();
            rates = Soc.RefreshRates();
            RefreshPower();
        }

        void RefreshPower()
        {
            var vals = new uint[PowerSettings.Rows.Length * 2];
            for (int i = 0; i < vals.Length; i++)
                try { vals[i] = PowerSettings.Read(PowerSettings.Rows[i / 2], i % 2 == 0); }
                catch { vals[i] = uint.MaxValue; }
            powerVals = vals;
            hibernate = PowerSettings.HibernateAvailable();
            noStandby = NoStandby.Enabled;
        }

        void SetPower(PowerSettings.Row r, bool ac, uint v)
        {
            Apply(r.Title.ToLowerInvariant(), delegate
            {
                uint got;
                try { got = PowerSettings.Write(r, ac, v); }
                catch (Exception ex) { throw new Exception(ex.Message + L.T(" · actual value: ", " · valore reale: ") + PowerReal(r, ac)); }
                if (got != v) throw new Exception(L.T("the system reports ", "il sistema riporta ") + PowerName(r, got));
            });
        }

        static string PowerReal(PowerSettings.Row r, bool ac)
        {
            try { return PowerName(r, PowerSettings.Read(r, ac)); }
            catch { return L.T("unreadable", "non leggibile"); }
        }

        // shortcut: an out-of-type value shows as a number, without unit; enough until Windows produces one
        static string PowerName(PowerSettings.Row r, uint v)
        {
            return PowerSettings.Label(r.Kind, v) ?? v.ToString();
        }

        void Apply(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { MessageBox.Show(this, L.T("Could not change ", "Impossibile cambiare ") + what + ": " + ex.Message, "SnapControl"); }
            RefreshControls();
        }

        // level 0 = off
        void SetChargeLimit(int level)
        {
            // off stays clickable when already off: it is the way to free a charge left stopped
            if (chargeBusy || (level != 0 && ChargeLimit.Enabled && level == ChargeLimit.Stop)) return;
            chargeBusy = true;
            Background(delegate
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--charge-limit " + (level == 0 ? "off" : level.ToString())) { Verb = "runas", UseShellExecute = true };
                    using (var p = System.Diagnostics.Process.Start(psi)) p.WaitForExit();
                }
                catch (System.ComponentModel.Win32Exception) { }   // administrator permission not granted
                BeginInvoke((MethodInvoker)delegate { chargeBusy = false; Invalidate(); });
            });
        }

        void RefreshApps()
        {
            if (appBusy) return;
            appBusy = true;
            appRows = null;   // previous data must not stay on screen as current
            appStatus = () => L.T("Exporting…", "Esportazione in corso…");
            // Not in background: if the window closes the process waits, so the temp file is deleted anyway.
            var th = new Thread((ThreadStart)delegate
            {
                Func<string> status;
                var rows = AppUsage.Refresh(out status);
                try { BeginInvoke((MethodInvoker)delegate { appRows = rows; appStatus = status; appBusy = false; Invalidate(); }); }
                catch (InvalidOperationException) { }   // window already closed
            });
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void CycleTheme()
        {
            themeIdx = (themeIdx + 1) % Theme.All.Length;
            Theme.Save(themeIdx);
            Dwm.TitleBar(Handle, T);
        }

        // ---------------------------------------------------------------- drawing

        const TextFormatFlags TxtLeft = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

        void Txt(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags fl = TxtLeft)
        {
            TextRenderer.DrawText(g, s, f, r, c, fl);
        }

        int TxtW(string s, Font f) { return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding).Width; }

        // With the variable font Segoe UI Variable in bold MeasureText overestimates by ~15%:
        // measures the real ink by drawing once off-screen (cached result).
        readonly Dictionary<string, int> inkCache = new Dictionary<string, int>();
        int InkW(string s, Font f)
        {
            string key = s + "|" + f.Size;
            int w;
            if (inkCache.TryGetValue(key, out w)) return w;
            int bw = TxtW(s, f) + 8, bh = f.Height + 8;
            using (var bmp = new Bitmap(bw, bh))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                TextRenderer.DrawText(g, s, f, Point.Empty, Color.Black, TextFormatFlags.NoPadding);
                for (int x = bw - 1; x >= 0 && w == 0; x--)
                    for (int y = 0; y < bh; y++)
                        if (bmp.GetPixel(x, y).R < 140) { w = x + 1; break; }
            }
            inkCache[key] = w;
            return w;
        }

        void HLine(Graphics g, int x1, int x2, int y, Color c) { using (var p = new Pen(c, 1)) g.DrawLine(p, x1, y, x2, y); }
        void VLine(Graphics g, int x, int y1, int y2, Color c) { using (var p = new Pen(c, 1)) g.DrawLine(p, x, y1, x, y2); }

        static GraphicsPath Pill(Rectangle r)
        {
            int d = r.Height;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        // "Pill" button: filled in the LED color if active, thin outline otherwise.
        Rectangle PillButton(Graphics g, int x, int y, string label, bool active, Action a, int pad = 36)
        {
            int h = Px(34), w = TxtW(label, fMono) + Px(pad);
            var r = new Rectangle(x, y, w, h);
            bool hov = r == hover;
            using (var path = Pill(r))
            {
                if (active) using (var b = new SolidBrush(T.Accent)) g.FillPath(b, path);
                else
                {
                    if (hov) using (var b = new SolidBrush(T.Band)) g.FillPath(b, path);
                    using (var p = new Pen(hov ? T.Text : T.LineStrong, 1)) g.DrawPath(p, path);
                }
            }
            Txt(g, label, fMono, active ? T.OnAccent : T.Text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (a != null) Hit(r, a);
            return r;
        }

        void Led(Graphics g, int cx, int cy, bool on)
        {
            int r = Px(4);
            if (on)
            {
                using (var glow = new SolidBrush(Color.FromArgb(T.Dark ? 70 : 40, T.Accent))) g.FillEllipse(glow, cx - r * 2, cy - r * 2, r * 4, r * 4);
                using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            }
            else using (var p = new Pen(T.LineStrong, 1)) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
        }

        void DotGrid(Graphics g, Rectangle area)
        {
            int step = Px(18);
            float r = Math.Max(1f, S * 1.1f);
            using (var b = new SolidBrush(T.Dot))
                for (int y = area.Top + step / 2; y < area.Bottom; y += step)
                    for (int x = area.Left + step / 2; x < area.Right; x += step)
                        g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
        }

        // Mono index label + title with the final dot lit in the LED color (like "theme." in the web theme).
        int Header(Graphics g, int x, int y, int w)
        {
            Txt(g, string.Format("{0:00} // {1}", page + 1, Pages[page]), fMono, T.Muted, new Rectangle(x, y, w, Px(16)));
            y += Px(22);
            string t = Titles[page];
            int tw = InkW(t, fTitle) + Px(2);
            var tr = new Rectangle(x, y, TxtW(t, fTitle) + Px(4), Px(54));
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x - Px(4), y, tw + Px(32), Px(54)));
            Txt(g, t, fTitle, T.Text, tr);
            Txt(g, ".", fTitle, T.Accent, new Rectangle(x + tw, y, Px(24), Px(54)));
            return y + Px(72);
        }

        int Rows(Graphics g, int x, int y, int w, List<string[]> rows)
        {
            int h = Px(37), labelW = Px(170);
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, rows.Count * h));
            HLine(g, x, x + w, y, T.Line);
            foreach (var r in rows)
            {
                // third element = English label: rows held in a field without rereading WMI on language change
                Txt(g, (r.Length > 2 ? L.T(r[2], r[0]) : r[0]).ToUpperInvariant(), fMonoSmall, T.Muted, new Rectangle(x, y, labelW, h));
                Txt(g, r[1], fBody, T.Text, new Rectangle(x + labelW, y, w - labelW, h));
                y += h;
                HLine(g, x, x + w, y, T.Line);
            }
            return y;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            hits.Clear();
            g.Clear(T.Bg);
            int w = ClientSize.Width, h = ClientSize.Height;
            int barH = Px(52), navW = Px(210), pad = Px(36);

            DotGrid(g, new Rectangle(navW, barH, w - navW, h - barH));

            // top bar
            string tl = T.Name;
            int pillW = TxtW(tl, fMono) + Px(48);
            var pr = new Rectangle(w - Px(20) - pillW, (barH - Px(30)) / 2, pillW, Px(30));

            // checkboxes: stays active near the clock and starts with Windows; never sleep, Wi-Fi always on
            // language: the button shows the active one, like the theme button
            var lr = PillButton(g, pr.X - Px(10) - TxtW("EN", fMono) - Px(24), (barH - Px(34)) / 2, L.It ? "IT" : "EN", false, ToggleLang, 24);
            var ck = CheckPill(g, L.T("KEEP IN TRAY", "VICINO ALL'OROLOGIO"), trayMode, lr.X - Px(10), pr, ToggleTray);
            ck = CheckPill(g, L.T("NEVER SLEEP", "MAI IN STANDBY"), noStandby, ck.X - Px(10), pr, ToggleNoStandby);

            int subX = Px(20) + TxtW("SNAPCONTROL", fMonoBold) + Px(12);
            Txt(g, "SNAPCONTROL", fMonoBold, T.Text, new Rectangle(Px(20), 0, Px(120), barH));
            Txt(g, "//  IDEAPAD SLIM 5 14Q8X9  ·  SNAPDRAGON X PLUS", fMono, T.Muted, new Rectangle(subX, 0, Math.Max(0, ck.X - Px(12) - subX), barH));
            using (var path = Pill(pr))
            {
                if (pr == hover) using (var b = new SolidBrush(T.Band)) g.FillPath(b, path);
                using (var p = new Pen(T.LineStrong, 1)) g.DrawPath(p, path);
            }
            Led(g, pr.X + Px(16), pr.Y + pr.Height / 2, true);
            Txt(g, tl, fMono, T.Text, new Rectangle(pr.X + Px(28), pr.Y, pillW - Px(28), pr.Height));
            Hit(pr, CycleTheme);
            HLine(g, 0, w, barH, T.Line);

            // navigation
            VLine(g, navW, barH, h, T.Line);
            int rowH = Px(60);
            for (int i = 0; i < Pages.Length; i++)
            {
                var r = new Rectangle(0, barH + 1 + i * rowH, navW, rowH);
                bool act = i == page;
                if (r == hover && !act) using (var b = new SolidBrush(T.Band)) g.FillRectangle(b, r);
                Txt(g, string.Format("{0:00}", i + 1), fMono, act ? T.Accent : T.Muted, new Rectangle(Px(20), r.Y, Px(30), rowH));
                Txt(g, Pages[i], act ? fMonoBold : fMono, act ? T.Text : T.Muted, new Rectangle(Px(56), r.Y, navW - Px(90), rowH));
                if (act) Led(g, navW - Px(24), r.Y + rowH / 2, true);
                HLine(g, 0, navW, r.Bottom, T.Line);
                int idx = i;
                Hit(r, delegate { Go(idx); });
            }
            Txt(g, L.T("ARM64 NATIVE", "ARM64 NATIVO"), fMonoSmall, T.Muted, new Rectangle(Px(20), h - Px(58), navW - Px(30), Px(18)));
            Txt(g, L.T("0 TELEMETRY", "0 TELEMETRIA"), fMonoSmall, T.Muted, new Rectangle(Px(20), h - Px(38), navW - Px(30), Px(18)));

            // content
            int cx = navW + pad, cy = barH + pad, cw = w - navW - pad * 2;
            cy = Header(g, cx, cy, cw);
            if (page == 0)
            {
                int ry = Rows(g, cx, cy, cw, sysRows);
                PillButton(g, cx, ry + Px(16), L.T("AUTHOR: MICH-DE · GITHUB ↗", "AUTORE: MICH-DE · GITHUB ↗"), false, delegate { Open("https://github.com/mich-de/snapcontrol"); });
                PaintUac(g, cx, ry + Px(62), cw);
            }
            else if (page == 1) PaintBattery(g, cx, cy, cw);
            else if (page == 2) PaintDrivers(g, cx, cy, cw, h - pad);
            else if (page == 3) PaintSoc(g, cx, cy, cw);
            else PaintControls(g, cx, cy, cw);
        }

        void PaintBattery(Graphics g, int x, int y, int w)
        {
            // dot-matrix percentage
            string pct = bat.Percent >= 0 ? bat.Percent + "%" : "--%";
            float pitch = Px(8);
            var sz = DotMatrix.Measure(pct, pitch);
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, x - Px(6), y - Px(6), sz.Width + Px(12), sz.Height + Px(12));
            // off dots dimmed like ".sp-mx .off { opacity: .6 }" in the web theme
            DotMatrix.Draw(g, pct, x, y, pitch, T.Accent, Color.FromArgb(T.Dark ? 255 : 150, T.Dot));

            // status to the right of the digit
            int sx = x + (int)sz.Width + Px(36);
            Led(g, sx + Px(5), y + Px(14), bat.Charging);
            Txt(g, bat.State.ToUpperInvariant(), fMonoBold, T.Text, new Rectangle(sx + Px(18), y + Px(4), w - (sx - x), Px(20)));
            Txt(g, bat.RateW == 0 ? "0 W" : string.Format("{0:+0.0;-0.0} W", bat.RateW), fMono, T.Muted, new Rectangle(sx + Px(18), y + Px(28), Px(200), Px(18)));
            Txt(g, L.T("MODE ", "MODALITÀ ") + PowerMode.Name(mode).ToUpperInvariant(), fMono, T.Muted, new Rectangle(sx + Px(18), y + Px(48), w - (sx - x), Px(18)));
            y += (int)sz.Height + Px(20);

            // row of dot LEDs: one LED every 5%
            int leds = 20, d = Px(11), step = Math.Min(Px(28), Math.Min(w, Px(600)) / leds);
            int lit = (int)Math.Round(Math.Max(0, bat.Percent) / 5.0);
            using (var on = new SolidBrush(T.Accent))
            using (var glow = new SolidBrush(Color.FromArgb(T.Dark ? 60 : 30, T.Accent)))
            using (var off = new SolidBrush(T.Dot))
                for (int i = 0; i < leds; i++)
                {
                    int lx = x + i * step;
                    if (i < lit) { g.FillEllipse(glow, lx - Px(3), y - Px(3), d + Px(6), d + Px(6)); g.FillEllipse(on, lx, y, d, d); }
                    else g.FillEllipse(off, lx, y, d, d);
                }
            y += Px(38);

            // power mode
            Txt(g, L.T("POWER MODE", "MODALITÀ ENERGIA"), fMonoSmall, T.Muted, new Rectangle(x, y, w, Px(16)));
            y += Px(22);
            int bx = x;
            bx = PillButton(g, bx, y, L.T("EFFICIENCY", "EFFICIENZA"), mode == PowerMode.Efficiency, delegate { SetMode(PowerMode.Efficiency); }).Right + Px(8);
            bx = PillButton(g, bx, y, L.T("BALANCED", "BILANCIATA"), mode == PowerMode.Balanced, delegate { SetMode(PowerMode.Balanced); }).Right + Px(8);
            PillButton(g, bx, y, L.T("PERFORMANCE", "PRESTAZIONI"), mode == PowerMode.Performance, delegate { SetMode(PowerMode.Performance); });
            y += Px(50);

            Txt(g, L.T("CHARGE LIMIT", "LIMITE DI CARICA"), fMonoSmall, T.Muted, new Rectangle(x, y, w, Px(16)));
            y += Px(22);
            bool limit = ChargeLimit.Enabled;
            int stop = limit ? ChargeLimit.Stop : 0;
            bx = PillButton(g, x, y, L.T("OFF", "NO"), !limit, delegate { SetChargeLimit(0); }).Right + Px(8);
            foreach (int lv in ChargeLimit.Levels)
            {
                int level = lv;
                bx = PillButton(g, bx, y, level + "%", level == stop, delegate { SetChargeLimit(level); }).Right + Px(8);
            }
            y += Px(40);
            Txt(g, limit
                ? L.T("Stops at " + stop + "%, resumes at " + (stop - ChargeLimit.Margin) + "%. Does not discharge when plugged in.", "Si ferma al " + stop + "%, riprende al " + (stop - ChargeLimit.Margin) + "%. Non scarica con l'alimentatore collegato.")
                : L.T("Choose where charging stops: it resumes " + ChargeLimit.Margin + "% below. Does not discharge when plugged in.", "Scegli dove fermare la carica: riprende " + ChargeLimit.Margin + "% sotto. Non scarica con l'alimentatore collegato."), fMono, T.Muted, new Rectangle(x, y, w, Px(20)));
            y += Px(34);

            y = Rows(g, x, y, w, bat.Rows(autonomy.Text(bat))) + Px(18);

            // note
            string adv = bat.Advice();
            int ah = TextRenderer.MeasureText(adv, fBody, new Size(w - Px(32), 0), TextFormatFlags.WordBreak).Height + Px(40);
            var ar = new Rectangle(x, y, w, ah);
            using (var bg = new SolidBrush(T.Surface)) g.FillRectangle(bg, ar);
            using (var p = new Pen(T.Line, 1)) g.DrawRectangle(p, ar);
            using (var a = new SolidBrush(T.Accent)) g.FillRectangle(a, ar.X, ar.Y, Px(3), ar.Height);
            Txt(g, L.T("NOTE", "NOTA"), fMonoSmall, T.Accent, new Rectangle(x + Px(16), y + Px(10), Px(100), Px(14)));
            TextRenderer.DrawText(g, adv, fBody, new Rectangle(x + Px(16), y + Px(28), w - Px(32), ah - Px(30)), T.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        // Thin horizontal bar: track of off dots, fill in the LED color.
        void Bar(Graphics g, Rectangle r, int pct)
        {
            using (var off = new SolidBrush(T.Dot)) g.FillRectangle(off, r);
            int fill = r.Height * Math.Max(0, Math.Min(100, pct)) / 100;
            using (var on = new SolidBrush(T.Accent)) g.FillRectangle(on, r.X, r.Bottom - fill, r.Width, fill);
        }

        void PaintSoc(Graphics g, int x, int y, int w)
        {
            var s = soc ?? new SocSample();
            y = Rows(g, x, y, w, new List<string[]>
            {
                new[] { "CPU Oryon", s.Cpu + "%  ·  " + (s.MHz / 1000.0).ToString("0.00") + " GHz  ·  " + L.T("performance ", "prestazioni ") + s.Perf + "%" },
                new[] { "GPU Adreno", s.Gpu3d + "% 3D  ·  " + s.GpuCompute + L.T("% compute  ·  ", "% calcolo  ·  ") + s.GpuVideo + "% video  ·  " + s.GpuSharedMb + " MB" },
                new[] { "NPU Hexagon", s.Npu + "%" },
            });

            // per-core load, one column each
            int rh = Px(76), labelW = Px(170);
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, rh));
            Txt(g, L.T("CORES · ", "CORE · ") + s.Cores.Count, fMonoSmall, T.Muted, new Rectangle(x, y, labelW, rh));
            if (s.Cores.Count > 0)
            {
                int step = Math.Min(Px(40), (w - labelW) / s.Cores.Count), bw = step - Px(8);
                for (int i = 0; i < s.Cores.Count; i++)
                {
                    int bx = x + labelW + i * step;
                    Bar(g, new Rectangle(bx, y + Px(10), bw, rh - Px(34)), s.Cores[i]);
                    Txt(g, s.Cores[i].ToString(), fMonoSmall, T.Muted, new Rectangle(bx - Px(4), y + rh - Px(22), bw + Px(8), Px(18)), TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                }
            }
            y += rh;
            HLine(g, x, x + w, y, T.Line);
            y += Px(26);

            Txt(g, L.T("QUALCOMM DRIVERS", "DRIVER QUALCOMM"), fMonoSmall, T.Accent, new Rectangle(x, y, w, Px(18)));
            y += Px(24);
            y = Rows(g, x, y, w, qcRows ?? new List<string[]>()) + Px(12);
            string note = L.T("Updates every 2 s · Qualcomm does not expose temperatures or GPU/NPU clocks", "Aggiornamento ogni 2 s · temperature e clock GPU/NPU non sono esposti da Qualcomm");
            if (updates != null)
            {
                int n = updates.FindAll(u => (u.Provider ?? "").Contains("Qualcomm") || (u.Title ?? "").Contains("Qualcomm")).Count;
                note = n > 0 ? n + L.T(" new Qualcomm drivers on Windows Update: Drivers page", " driver Qualcomm nuovi su Windows Update: pagina Driver") : L.T("No new Qualcomm drivers in the last search", "Nessun driver Qualcomm nuovo nell'ultima ricerca");
            }
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, Px(20)));
            Txt(g, note, fMono, T.Muted, new Rectangle(x, y, w, Px(20)));
        }

        // Control row: label on the left, buttons with the active value lit. set null = read-only.
        int ControlRow(Graphics g, int x, int y, int w, string label, string[] names, uint[] values, uint current, Action<uint> set, int labelPx = 200, int pad = 36, int step = 44, uint blocked = uint.MaxValue)
        {
            int labelW = Px(labelPx);
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, labelW, Px(34)));
            Txt(g, label, fMonoSmall, T.Muted, new Rectangle(x, y, labelW, Px(34)));
            int bx = x + labelW;
            for (int i = 0; i < names.Length; i++)
            {
                uint v = values[i];
                if (bx > x + labelW && bx + TxtW(names[i], fMono) + Px(pad) > x + w) { bx = x + labelW; y += Px(step); }
                bx = PillButton(g, bx, y, names[i], current == v, set == null || v == blocked ? null : (Action)delegate { set(v); }, pad).Right + Px(8);
            }
            return y + Px(step);
        }

        void PaintControls(Graphics g, int x, int y, int w)
        {
            if (cpuVals == null) RefreshControls();
            // the two views do not fit together in the usable height at 1920x1200 at 150%
            int sx = PillButton(g, x, y, L.T("CPU AND SCREEN", "CPU E SCHERMO"), !ctlPower && !ctlApps, delegate { ctlPower = ctlApps = false; }).Right + Px(8);
            sx = PillButton(g, sx, y, L.T("POWER", "ALIMENTAZIONE"), ctlPower, delegate { ctlPower = true; ctlApps = false; RefreshPower(); }).Right + Px(8);
            PillButton(g, sx, y, L.T("APP USAGE", "CONSUMO APP"), ctlApps, delegate { ctlApps = true; ctlPower = false; });
            y += Px(46);
            if (ctlPower) { PaintPower(g, x, y, w); return; }
            if (ctlApps) { PaintApps(g, x, y, w); return; }
            string[] maxN = { "50%", "70%", "85%", "100%" };
            uint[] maxV = { 50, 70, 85, 100 };
            string[] boostN = { L.T("OFF", "NO"), L.T("ON", "SÌ"), L.T("AGGRESSIVE", "AGGRESSIVO") };
            uint[] boostV = { 0, 1, 2 };

            Txt(g, L.T("CPU PROFILE · ACTIVE POWER PLAN", "PROFILO CPU · PIANO ENERGIA ATTIVO"), fMonoSmall, T.Accent, new Rectangle(x, y, w, Px(18)));
            y += Px(26);
            y = ControlRow(g, x, y, w, L.T("MAX CPU · BATTERY", "MAX CPU · BATTERIA"), maxN, maxV, cpuVals[0], v => Apply(L.T("the CPU limit", "il limite CPU"), delegate { Soc.Write(Soc.MaxState, false, v); }));
            y = ControlRow(g, x, y, w, L.T("MAX CPU · AC", "MAX CPU · RETE"), maxN, maxV, cpuVals[1], v => Apply(L.T("the CPU limit", "il limite CPU"), delegate { Soc.Write(Soc.MaxState, true, v); }));
            y = ControlRow(g, x, y, w, L.T("BOOST · BATTERY", "BOOST · BATTERIA"), boostN, boostV, cpuVals[2], v => Apply(L.T("boost", "il boost"), delegate { Soc.Write(Soc.Boost, false, v); }));
            y = ControlRow(g, x, y, w, L.T("BOOST · AC", "BOOST · RETE"), boostN, boostV, cpuVals[3], v => Apply(L.T("boost", "il boost"), delegate { Soc.Write(Soc.Boost, true, v); }));
            y += Px(18);

            Txt(g, L.T("SCREEN", "SCHERMO"), fMonoSmall, T.Accent, new Rectangle(x, y, w, Px(18)));
            y += Px(26);
            if (bright >= 0)
            {
                string[] bN = { "25%", "50%", "75%", "100%" };
                uint[] bV = { 25, 50, 75, 100 };
                // the active value is the step closest to the real brightness
                uint near = bV[0];
                foreach (uint v in bV) if (Math.Abs((int)v - bright) < Math.Abs((int)near - bright)) near = v;
                y = ControlRow(g, x, y, w, L.T("BRIGHTNESS · ", "LUMINOSITÀ · ") + bright + "%", bN, bV, near, v => Apply(L.T("brightness", "la luminosità"), delegate { Soc.SetBrightness((int)v); }));
            }
            if (rates != null && rates.Count > 0)
            {
                y = ControlRow(g, x, y, w, L.T("REFRESH RATE", "AGGIORNAMENTO"), rates.ConvertAll(r => r + " HZ").ToArray(), rates.ConvertAll(r => (uint)r).ToArray(), (uint)hz,
                    v => Apply(L.T("the refresh rate", "la frequenza"), delegate { int r = Soc.SetRefreshRate((int)v); if (r != 0) throw new Exception(L.T("code ", "codice ") + r); }));
                if (rates.Count == 1)
                {
                    using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, Px(20)));
                    Txt(g, L.T("The panel only offers ", "Il pannello espone solo ") + rates[0] + " Hz", fMono, T.Muted, new Rectangle(x, y, w, Px(20)));
                    y += Px(28);
                }
            }
            y += Px(10);
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, Px(20)));
            Txt(g, L.T("On Snapdragon X the main control is still the power mode (Battery page)", "Su Snapdragon X il controllo principale resta la modalità energia (pagina Batteria)"), fMono, T.Muted, new Rectangle(x, y, w, Px(20)));
        }

        void PaintApps(Graphics g, int x, int y, int w)
        {
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, Px(18)));
            Txt(g, L.T("APP USAGE · LAST 24 HOURS ON BATTERY · SCREEN EXCLUDED · WINDOWS ESTIMATE", "CONSUMO PER APP · ULTIME 24 ORE A BATTERIA · SCHERMO ESCLUSO · STIMA DI WINDOWS"), fMonoSmall, T.Accent, new Rectangle(x, y, w, Px(18)));
            y += Px(26);
            int bx = PillButton(g, x, y, L.T("REFRESH", "AGGIORNA"), false, appBusy ? null : (Action)RefreshApps).Right + Px(16);
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(bx, y, Math.Max(0, x + w - bx), Px(34)));
            Txt(g, appStatus(), fMono, T.Muted, new Rectangle(bx, y, Math.Max(0, x + w - bx), Px(34)));
            y += Px(46);
            if (appRows != null && appRows.Count > 0) Rows(g, x, y, w, appRows.ConvertAll(r => r[1] == null ? new[] { r[0], L.T("Others", "Altre") } : r));
        }

        void PaintPower(Graphics g, int x, int y, int w)
        {
            bool mixed = false;
            for (int i = 0; i < PowerSettings.Rows.Length; i++)
            {
                var r = PowerSettings.Rows[i];
                if (r.Name == "ibernazione" && !hibernate) continue;
                bool locked = NoStandby.Controls(r.Set) && noStandby;
                // on battery the threshold is the only one that counts
                if (r.Name != "risparmio") y = PowerRow(g, x, y, w, r, true, powerVals[i * 2], powerVals[i * 2 + 1], locked);
                y = PowerRow(g, x, y, w, r, false, powerVals[i * 2 + 1], powerVals[i * 2], locked);
                if (r.Name == "sospensione") mixed = powerVals[i * 2] == 0 || powerVals[i * 2 + 1] == 0;
            }
            string note = noStandby ? L.T("Sleep and hibernate are read-only: to change them clear \"Never sleep\"", "Sospensione e ibernazione in sola lettura: per cambiarle togli «Mai in standby»")
                : mixed ? L.T("Sleep: \"Never\" on both AC and battery is set with the \"Never sleep\" check", "Sospensione: «Mai» su rete e batteria si imposta con la spunta «Mai in standby»") : null;
            if (note != null)
            {
                using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, Px(20)));
                Txt(g, note, fMono, T.Muted, new Rectangle(x, y, w, Px(20)));
            }
        }

        int PowerRow(Graphics g, int x, int y, int w, PowerSettings.Row r, bool ac, uint current, uint other, bool locked)
        {
            string label = r.Title + (ac ? L.T(" · AC", " · RETE") : L.T(" · BATTERY", " · BATTERIA"));
            if (current == uint.MaxValue)
            {
                using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, new Rectangle(x, y, w, Px(34)));
                Txt(g, label + L.T(": unreadable", ": non leggibile"), fMonoSmall, T.Muted, new Rectangle(x, y, w, Px(34)));
                return y + Px(40);
            }
            var values = PowerSettings.Choices(r, current, hibernate);
            string[] names = values.ConvertAll(v => PowerName(r, v).ToUpperInvariant()).ToArray();
            Action<uint> set = null;
            if (!locked) set = v => SetPower(r, ac, v);
            return ControlRow(g, x, y, w, label, names, values.ToArray(), current, set, 160, 20, 40, r.Name == "sospensione" && other == 0 ? 0 : uint.MaxValue);
        }

        void SetMode(Guid m)
        {
            uint r = Native.PowerSetActiveOverlayScheme(ref m);
            if (r != 0) MessageBox.Show(this, L.T("Could not change mode (error " + r + ").", "Impossibile cambiare modalità (errore " + r + ")."), "SnapControl");
            RefreshBattery();
        }

        void ToggleFlag(string id)
        {
            if (!flagged.Remove(id)) { flagged.Add(id); selected.Remove(id); }
            Settings.Flagged = new List<string>(flagged).ToArray();
        }

        void PaintUac(Graphics g, int x, int y, int w)
        {
            Txt(g, Uac.Prompts
                ? L.T("UAC · on: Windows asks at each admin action", "UAC · attivo: Windows chiede a ogni azione da amministratore")
                : L.T("UAC · no prompts on this PC: any program can get admin rights without asking", "UAC · nessuna richiesta su questo PC: qualsiasi programma può avere i diritti di amministratore senza chiedere"), fMono, Uac.Prompts ? T.Muted : T.Accent, new Rectangle(x, y, w, Px(34)));
        }

        void PaintDrivers(Graphics g, int x, int y, int w, int bottom)
        {
            if (drivers == null) LoadDrivers();
            bool busy = wuBusy || installing;
            int bx = x;
            // buttons wrap if there is no room
            Action<string, bool, Action> pill = (label, active, act) =>
            {
                if (bx > x && bx + TxtW(label, fMono) + Px(36) > x + w) { bx = x; y += Px(42); }
                bx = PillButton(g, bx, y, label, active, act).Right + Px(8);
            };
            pill(wuBusy ? L.T("SEARCHING…", "RICERCA…") : L.T("CHECK FOR UPDATES", "CERCA AGGIORNAMENTI"), false, busy ? (Action)null : SearchUpdates);
            var installable = updates == null ? new List<UpdateItem>() : updates.FindAll(u => u.Installable && !flagged.Contains(u.Id));
            var chosen = installable.FindAll(u => selected.Contains(u.Id));
            if (installable.Count > 0)
            {
                pill(installing ? L.T("INSTALLING…", "INSTALLAZIONE…") : L.T("INSTALL SELECTED (", "INSTALLA SELEZIONATI (") + chosen.Count + ")", chosen.Count > 0 && !busy,
                    chosen.Count > 0 && !busy ? (Action)delegate { InstallUpdates(chosen.ConvertAll(u => u.Id)); } : null);
                pill(L.T("INSTALL ALL", "INSTALLA TUTTI"), false, busy ? (Action)null : delegate { InstallUpdates(installable.ConvertAll(u => u.Id)); });
            }
            pill(L.T("LENOVO 83HL PAGE ↗", "PAGINA LENOVO 83HL ↗"), false, delegate { Open(Lenovo.SupportPage); });
            y += Px(46);
            using (var bg = new SolidBrush(T.Bg)) g.FillRectangle(bg, x, y, w, bottom - y);
            Txt(g, drvStatus(), fMono, T.Muted, new Rectangle(x, y, w, Px(20)));
            y += Px(32);

            // BIOS row, always visible
            {
                var inst = drivers.Find(d => d.Device == "BIOS");
                int bh = Px(46), ax = x + w, ay = y + (bh - Px(34)) / 2;
                var b = biosLatest;
                Action<string, Action> act = (label, a) => { ax -= TxtW(label, fMono) + Px(20); PillButton(g, ax, ay, label, false, biosBusy ? null : a, 20); ax -= Px(6); };
                act(biosBusy ? L.T("WORKING…", "ATTENDI…") : L.T("CHECK BIOS", "CONTROLLA BIOS"), CheckBios);
                if (b != null && b.BiosUrl != null) act(L.T("DOWNLOAD", "SCARICA"), delegate { BiosAction(b, false); });
                if (b != null && b.BiosUrl != null && b.BiosFlash) act(L.T("INSTALL", "INSTALLA"), delegate { BiosAction(b, true); });
                string text = "BIOS · " + L.T("installed ", "installato ") + (inst == null ? "?" : inst.Version);
                if (b != null) text += b.BiosFlash ? " · " + L.T("NEW ", "NUOVO ") + b.By : " · " + L.T("Lenovo latest ", "ultimo Lenovo ") + b.By + (inst != null && inst.Version.Equals(b.By, StringComparison.OrdinalIgnoreCase) ? L.T(": up to date", ": aggiornato") : "");
                Txt(g, text, fMono, b != null && b.BiosFlash ? T.Accent : T.Text, new Rectangle(x, y, ax - x - Px(6), bh));
                y += bh;
                HLine(g, x, x + w, y, T.Line);
                y += Px(14);
            }

            // updates found: checkbox for single install, or all together
            if (updates != null && updates.Count > 0)
            {
                Txt(g, L.T("AVAILABLE UPDATES · ", "AGGIORNAMENTI DISPONIBILI · ") + updates.Count, fMonoSmall, T.Accent, new Rectangle(x, y, w, Px(18)));
                y += Px(24);
                HLine(g, x, x + w, y, T.LineStrong);
                int uh = Px(52);
                foreach (var u in updates)
                {
                    var row = new Rectangle(x, y, w, uh);
                    if (row == hover) using (var b = new SolidBrush(T.Band)) g.FillRectangle(b, row);
                    var box = new Rectangle(x + Px(4), y + (uh - Px(16)) / 2, Px(16), Px(16));
                    bool isFlagged = flagged.Contains(u.Id);
                    // buttons before the row: the first hit area wins
                    int ax = x + w, ay = y + (uh - Px(34)) / 2;
                    if (isFlagged)
                    {
                        ax -= TxtW("ISSUE ↗", fMono) + Px(20);
                        PillButton(g, ax, ay, "ISSUE ↗", false, delegate { Open(u.IssueUrl(Lenovo.MachineType(), Environment.OSVersion.Version.ToString(), Application.ProductVersion)); }, 20);
                        ax -= Px(6);
                    }
                    string flagLabel = isFlagged ? L.T("UNFLAG", "RIMUOVI SEGNO") : L.T("FLAG", "SEGNALA");
                    ax -= TxtW(flagLabel, fMono) + Px(20);
                    PillButton(g, ax, ay, flagLabel, isFlagged, delegate { ToggleFlag(u.Id); }, 20);
                    string pkg = L.T("PACKAGE ↗", "PACCHETTO ↗");
                    ax -= Px(6) + TxtW(pkg, fMono) + Px(20);
                    PillButton(g, ax, ay, pkg, false, delegate { Open(u.Url); }, 20);
                    if (isFlagged)
                        Txt(g, "!", fMonoBold, T.Accent, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    else if (u.Installable)
                    {
                        bool on = selected.Contains(u.Id);
                        using (var p = new Pen(on ? T.Accent : T.LineStrong, Px(1.5f))) g.DrawRectangle(p, box);
                        if (on) using (var b = new SolidBrush(T.Accent)) g.FillRectangle(b, Rectangle.Inflate(box, -Px(4), -Px(4)));
                        string id = u.Id;
                        Hit(row, delegate { if (!selected.Remove(id)) selected.Add(id); });
                    }
                    else
                    {
                        Txt(g, "↗", fMonoBold, T.Accent, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        string url = u.Url;
                        Hit(row, delegate { Open(url); });
                    }
                    int tw = ax - x - Px(42);
                    Txt(g, u.TitleText, fBody, isFlagged ? T.Muted : T.Text, new Rectangle(x + Px(32), y + Px(4), tw, uh / 2));
                    Txt(g, (isFlagged ? L.T("MAY BE INCOMPATIBLE · ", "FORSE NON COMPATIBILE · ") : "") + u.SourceText + " · " + u.Date, fMonoSmall, isFlagged ? T.Accent : T.Muted, new Rectangle(x + Px(32), y + uh / 2, tw, uh / 2 - Px(4)));
                    y += uh;
                    HLine(g, x, x + w, y, T.Line);
                }
                y += Px(22);
            }

            Txt(g, L.T("INSTALLED · ", "INSTALLATI · ") + drivers.Count, fMonoSmall, T.Muted, new Rectangle(x, y, w, Px(18)));
            y += Px(22);
            string[] heads = { L.T("DEVICE", "DISPOSITIVO"), L.T("PROVIDER", "FORNITORE"), L.T("VERSION", "VERSIONE"), L.T("DATE", "DATA"), L.T("AGE", "ETÀ") };
            float[] frac = { .35f, .17f, .21f, .16f, .11f };
            int[] cols = new int[5];
            int acc = x;
            for (int i = 0; i < 5; i++) { cols[i] = acc; acc += (int)(w * frac[i]); }
            int rh = Px(34);
            for (int i = 0; i < 5; i++)
            {
                var head = new Rectangle(cols[i], y, (int)(w * frac[i]) - Px(10), rh);
                int col = i;
                Txt(g, heads[i] + (drvSort == i ? (drvDesc ? " ↓" : " ↑") : ""), fMonoSmall, drvSort == i ? T.Accent : head == hover ? T.Text : T.Muted, head);
                Hit(head, delegate { SortDrivers(col); });
            }
            y += rh;
            HLine(g, x, x + w, y, T.LineStrong);

            listArea = new Rectangle(x, y, w, bottom - y);
            listVisible = Math.Max(1, (bottom - y) / rh);
            for (int i = drvScroll; i < drivers.Count && i < drvScroll + listVisible; i++)
            {
                var d = drivers[i];
                int months = d.Date == DateTime.MinValue ? -1 : (int)((DateTime.Now - d.Date).TotalDays / 30.4);
                string[] cells = { d.Device, d.Provider, d.Version, d.Date == DateTime.MinValue ? "" : d.Date.ToString("yyyy-MM-dd"), months < 0 ? "" : months + L.T(" mo", " m") };
                for (int c = 0; c < 5; c++)
                    Txt(g, cells[c], c == 0 ? fBody : fMono, c == 0 ? T.Text : T.Muted, new Rectangle(cols[c], y, (int)(w * frac[c]) - Px(10), rh));
                y += rh;
                HLine(g, x, x + w, y, T.Line);
            }
            // thin scroll indicator
            if (drivers.Count > listVisible)
            {
                int trackH = listArea.Height - Px(4);
                int thumbH = Math.Max(Px(24), trackH * listVisible / drivers.Count);
                int thumbY = listArea.Y + (trackH - thumbH) * drvScroll / Math.Max(1, drivers.Count - listVisible);
                using (var b = new SolidBrush(T.Accent)) g.FillRectangle(b, x + w - Px(2), thumbY, Px(2), thumbH);
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Command line and charge limit always in Italian; the preference applies only to the window, --report and --wu-install.
            L.It = true;
            if (args.Length > 0 && args[0].StartsWith("--power-"))
            {
                Environment.ExitCode = PowerSettings.Cli(args);
                return;
            }
            if (args.Length > 0 && args[0].StartsWith("--app-usage"))
            {
                Environment.ExitCode = AppUsage.Cli(args);
                return;
            }
            if (args.Length == 2 && args[0] == "--report")
            {
                // Writes to a file the same data shown in the window (useful for diagnosis without UI).
                L.It = Settings.Italian;
                var f = new MainForm();
                System.IO.File.WriteAllText(args[1], f.Report(), Encoding.UTF8);
                return;
            }
            if (args.Length >= 3 && args[0] == "--wu-install")
            {
                // Elevated process started by the window: installs the selected updates and writes the outcome.
                L.It = Settings.Italian;
                string result;
                try
                {
                    var wu = new HashSet<string>();
                    var cat = new List<string>();
                    for (int i = 2; i < args.Length; i++)
                    {
                        string id = args[i];
                        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^(cat:)?[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$")) throw new Exception("id: " + id);
                        if (id.StartsWith("cat:")) cat.Add(id); else wu.Add(id);
                    }
                    var parts = new List<string>();
                    if (wu.Count > 0) parts.Add(Drivers.InstallWindowsUpdate(wu));
                    if (cat.Count > 0) parts.Add(MsCatalog.Install(cat));
                    // first line = short outcome for the status bar
                    result = string.Join(" · ", parts.ConvertAll(p => p.Split('\n')[0])) + "\n" + string.Join("\n", parts.ConvertAll(p => p.Substring(p.IndexOf('\n') + 1)));
                }
                catch (Exception ex) { result = L.T("Installation failed: ", "Installazione non riuscita: ") + ex.Message; }
                // CreateNew: a link prepared at this path must not redirect an elevated write
                using (var o = new System.IO.StreamWriter(new System.IO.FileStream(args[1] + ".out", System.IO.FileMode.CreateNew), Encoding.UTF8)) o.Write(result);
                return;
            }
            if (args.Length == 2 && args[0] == "--bios-run")
            {
                // Elevated process started by the window: the package is checked again in a folder only administrators can write to.
                L.It = Settings.Italian;
                try { Environment.ExitCode = Lenovo.RunBios(args[1]); }
                catch (Exception ex) { Environment.ExitCode = 1; MessageBox.Show(L.T("BIOS package refused: ", "Pacchetto BIOS rifiutato: ") + ex.Message, "SnapControl"); }
                return;
            }
            if (args.Length == 2 && args[0] == "--charge-limit")
            {
                if (args[1] == "run") { ChargeLimit.Run(); return; }
                // Elevated process started by the window or the uninstaller.
                int level;
                try
                {
                    if (args[1] == "off") ChargeLimit.Disable();
                    else if (int.TryParse(args[1], out level)) ChargeLimit.Enable(level);
                    else throw new Exception("argomento non valido: " + args[1]);
                }
                catch (Exception ex) { Environment.ExitCode = 1; MessageBox.Show("Limite di carica: " + ex.Message, "SnapControl"); }
                return;
            }
            // Single instance: with the icon near the clock a second start would create a duplicate.
            // The second start only asks the first instance to show the window.
            bool fresh;
            using (var single = new Mutex(true, @"Local\SnapControl.Single", out fresh))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SnapControl.Show"))
            {
                if (!fresh) { if (!(args.Length == 1 && args[0] == "--tray")) show.Set(); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                L.It = Settings.Italian;
                var form = new MainForm(args.Length == 1 && args[0] == "--tray");
                var waiter = new Thread((ThreadStart)delegate
                {
                    try
                    {
                        while (show.WaitOne())
                            try { form.BeginInvoke((MethodInvoker)form.ShowWindow); } catch (InvalidOperationException) { }
                    }
                    catch (ObjectDisposedException) { }   // closing: the event has already been released
                }) { IsBackground = true };
                waiter.Start();
                Application.Run(form);
            }
        }
    }
}
