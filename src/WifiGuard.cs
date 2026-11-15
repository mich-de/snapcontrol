// Wi-Fi security checks, all local: how the connection is protected, look-alike networks (evil twin),
// router identity and DNS remembered per network (ARP spoofing / DNS changes), network category,
// saved networks that join on their own, and on request whether HTTPS is intercepted.
// Nothing leaves the PC except the two HTTPS connections of the on-demand check.
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SnapControl
{
    static class WifiGuard
    {
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")] static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanGetProfile(IntPtr handle, ref Guid iface, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr mem);
        [DllImport("iphlpapi.dll")] static extern int GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

        public sealed class Item
        {
            public int Level;                       // 0 ok, 1 warning, 2 danger
            public Func<string> Text;
            public Item(int level, Func<string> text) { Level = level; Text = text; }
        }

        public sealed class Saved
        {
            public string Name = "", Ssid = "", Auth = "", Encryption = "", Random = "";
            public bool Auto, Open;
        }

        public sealed class Result
        {
            public bool Connected;
            public string Ssid = "", Profile = "", Bssid = "", Security = "", Gateway = "", GatewayMac = "", Adapter = "";
            public int Category = -1;               // 0 public, 1 private, 2 domain
            public List<string> Dns = new List<string>();
            public List<Item> Items = new List<Item>();
            public List<Saved> Profiles = new List<Saved>();
            public bool Remembered, Changed;        // router/DNS: known network, and different from what was remembered
            public int Worst { get { int w = 0; foreach (var i in Items) w = Math.Max(w, i.Level); return w; } }
        }

        // 0 open, 1 WEP, 2 WPA, 3 WPA2, 4 WPA3 / OWE
        static int Rank(int auth, int cipher)
        {
            if (cipher == 1 || cipher == 5 || cipher == 0x101) return 1;    // WEP
            switch (auth)
            {
                case 1: return 0;
                case 2: return 1;
                case 3: case 4: return 2;
                case 6: case 7: return cipher == 2 ? 2 : 3;                   // WPA2 with TKIP counts as WPA
                case 8: case 9: case 10: case 11: return 4;
                default: return 3;
            }
        }

        static string AuthName(int a)
        {
            switch (a)
            {
                case 1: return "Open";
                case 2: return "WEP";
                case 3: return "WPA-Enterprise";
                case 4: return "WPA-Personal";
                case 6: return "WPA2-Enterprise";
                case 7: return "WPA2-Personal";
                case 8: case 11: return "WPA3-Enterprise";
                case 9: return "WPA3-Personal";
                case 10: return "OWE";
                default: return "auth " + a;
            }
        }

        static string CipherName(int c)
        {
            switch (c) { case 0: return ""; case 1: case 5: case 0x101: return "WEP"; case 2: return "TKIP"; case 4: return "AES"; case 8: return "GCMP"; default: return "cipher " + c; }
        }

        // profile XML authentication -> rank
        static int RankXml(string auth, string enc)
        {
            auth = auth.ToUpperInvariant(); enc = enc.ToUpperInvariant();
            if (enc == "WEP") return 1;
            if (auth == "OPEN" || auth == "") return enc == "NONE" || enc == "" ? 0 : 1;
            if (auth.StartsWith("WPA3") || auth == "OWE") return 4;
            if (auth.StartsWith("WPA2")) return enc == "TKIP" ? 2 : 3;
            if (auth.StartsWith("WPA")) return 2;
            return 3;
        }

        static string Tag(string xml, string tag)
        {
            var m = Regex.Match(xml, "<" + tag + "(?:\\s[^>]*)?>([^<]*)</" + tag + ">", RegexOptions.IgnoreCase);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : "";
        }

        static string Ssid(IntPtr p, int off)
        {
            int len = Math.Min(32, Math.Max(0, Marshal.ReadInt32(p, off)));
            var b = new byte[len];
            Marshal.Copy(new IntPtr(p.ToInt64() + off + 4), b, 0, len);
            return Encoding.UTF8.GetString(b);
        }

        static string Mac(byte[] b, int off, int len)
        {
            var s = new string[len];
            for (int i = 0; i < len; i++) s[i] = b[off + i].ToString("X2");
            return string.Join(":", s);
        }

        // ------------------------------------------------ memory per network (HKCU, this PC only)

        const string MemKey = Settings.Key + @"\WifiGuard";

        static string MemName(string ssid) { return "n_" + BitConverter.ToString(Encoding.UTF8.GetBytes(ssid)).Replace("-", ""); }

        static string Recall(string ssid)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(MemKey)) return k == null ? null : k.GetValue(MemName(ssid)) as string; }
            catch { return null; }
        }

        static void Remember(string ssid, string value)
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(MemKey)) k.SetValue(MemName(ssid), value); } catch { }
        }

        // "This is my router": the current router and DNS become the reference for this network.
        public static void Trust(Result r)
        {
            if (r.Connected && r.Ssid.Length > 0) Remember(r.Ssid, r.Gateway + "|" + r.GatewayMac + "|" + string.Join(",", r.Dns));
        }

        // ------------------------------------------------ main check (no network traffic)

        public static Result Check(bool withCategory)
        {
            var r = new Result();
            IntPtr h, list;
            uint ver;
            if (WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0) return r;
            try
            {
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return r;
                Guid id = Guid.Empty;
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = new IntPtr(list.ToInt64() + 8 + i * 532);
                        if (i == 0 || Marshal.ReadInt32(item, 16 + 512) == 1)
                        {
                            id = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                            r.Adapter = Marshal.PtrToStringUni(new IntPtr(item.ToInt64() + 16));
                            r.Connected = Marshal.ReadInt32(item, 16 + 512) == 1;
                            if (r.Connected) break;
                        }
                    }
                }
                finally { WlanFreeMemory(list); }
                if (id == Guid.Empty) return r;

                int curRank = -1;
                if (r.Connected) curRank = ReadConnection(h, id, r);
                var visible = Visible(h, id);
                ReadProfiles(h, id, r);

                if (r.Connected)
                {
                    // 1. protection of the current connection
                    string sec = r.Security;
                    if (curRank == 0) r.Items.Add(new Item(2, () => L.T("Open network: traffic is not encrypted, anyone nearby can read it. Use a VPN or avoid logins.", "Rete aperta: il traffico non è cifrato, chiunque vicino può leggerlo. Usa una VPN o evita gli accessi.")));
                    else if (curRank == 1) r.Items.Add(new Item(2, () => L.T("WEP protection can be broken in minutes: treat it as an open network.", "La protezione WEP si rompe in pochi minuti: considerala una rete aperta.")));
                    else if (curRank == 2) r.Items.Add(new Item(1, () => L.T("Old protection (" + sec + "): change the router to WPA2-AES or WPA3.", "Protezione vecchia (" + sec + "): imposta sul router WPA2-AES o WPA3.")));
                    else r.Items.Add(new Item(0, () => L.T("Encrypted connection: ", "Connessione cifrata: ") + sec));

                    // 3-4. router identity and DNS against what was remembered for this network
                    ReadIp(r);
                    string mem = Recall(r.Ssid), now = r.Gateway + "|" + r.GatewayMac + "|" + string.Join(",", r.Dns);
                    if (r.Gateway.Length > 0 && r.GatewayMac.Length > 0)
                    {
                        if (mem == null) { Remember(r.Ssid, now); r.Items.Add(new Item(0, () => L.T("New network: router and DNS remembered to spot future changes", "Rete nuova: router e DNS memorizzati per accorgersi di cambi futuri"))); }
                        else
                        {
                            r.Remembered = true;
                            string[] m = mem.Split('|');
                            string oldGw = m[0], oldMac = m.Length > 1 ? m[1] : "", oldDns = m.Length > 2 ? m[2] : "";
                            if (oldGw == r.Gateway && oldMac.Length > 0 && oldMac != r.GatewayMac)
                            {
                                r.Changed = true;
                                r.Items.Add(new Item(2, () => L.T("The router changed identity (" + oldMac + " → " + r.GatewayMac + "): someone may be intercepting traffic (ARP spoofing). If you replaced the router, press TRUST.", "Il router ha cambiato identità (" + oldMac + " → " + r.GatewayMac + "): qualcuno potrebbe intercettare il traffico (ARP spoofing). Se hai cambiato router, premi FIDATI.")));
                            }
                            else if (oldGw != r.Gateway)
                            {
                                r.Changed = true;
                                r.Items.Add(new Item(1, () => L.T("Different router address than usual (" + oldGw + " → " + r.Gateway + ")", "Indirizzo del router diverso dal solito (" + oldGw + " → " + r.Gateway + ")")));
                            }
                            else r.Items.Add(new Item(0, () => L.T("Same router as usual", "Stesso router di sempre") + " (" + r.GatewayMac + ")"));
                            string dnsNow = string.Join(",", r.Dns);
                            if (oldDns != dnsNow)
                            {
                                r.Changed = true;
                                r.Items.Add(new Item(1, () => L.T("DNS servers changed (" + oldDns + " → " + dnsNow + "): pages could be redirected. If you changed them, press TRUST.", "Server DNS cambiati (" + oldDns + " → " + dnsNow + "): le pagine potrebbero essere dirottate. Se li hai cambiati tu, premi FIDATI.")));
                            }
                        }
                    }
                    // another address answering with the router's hardware address
                    foreach (string other in SameMac(r.GatewayMac, r.Gateway))
                    {
                        string o = other;
                        r.Items.Add(new Item(1, () => L.T("The router's hardware address also answers for " + o + ": possible ARP spoofing", "L'indirizzo fisico del router risponde anche per " + o + ": possibile ARP spoofing")));
                    }

                    // 2. look-alike of the current network with weaker protection
                    foreach (var v in visible)
                        if (v.Key == r.Ssid && v.Value < curRank)
                        {
                            r.Items.Add(new Item(2, () => L.T("A network with the same name «" + r.Ssid + "» is broadcast with weaker protection: possible fake access point. Do not join it.", "Una rete con lo stesso nome «" + r.Ssid + "» trasmette con protezione più debole: possibile access point falso. Non collegarti.")));
                            break;
                        }
                }

                // 2. look-alikes of saved secure networks
                foreach (var p in r.Profiles)
                {
                    int saved = RankXml(p.Auth, p.Encryption);
                    if (saved < 3 || p.Ssid == r.Ssid) continue;
                    foreach (var v in visible)
                        if (v.Key == p.Ssid && v.Value < saved)
                        {
                            string n = p.Ssid;
                            r.Items.Add(new Item(2, () => L.T("«" + n + "» is saved as protected but a copy without the same protection is nearby: possible fake access point.", "«" + n + "» è salvata come protetta ma qui vicino ce n'è una copia senza la stessa protezione: possibile access point falso.")));
                            break;
                        }
                }

                // 7. open saved networks that join on their own
                int risky = 0;
                foreach (var p in r.Profiles) if (p.Open && p.Auto) risky++;
                if (risky > 0) { int n = risky; r.Items.Add(new Item(1, () => n + L.T(" saved open networks join automatically: a fake network with that name would be joined without asking", " reti aperte salvate si collegano da sole: una rete falsa con quel nome verrebbe usata senza chiedere"))); }
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }

            // 6. network category: an untrusted network should be Public
            if (withCategory && r.Connected)
            {
                r.Category = Category(r.Profile.Length > 0 ? r.Profile : r.Ssid);
                if (r.Category == 1)
                {
                    bool weak = r.Items.Exists(i => i.Level == 2);
                    r.Items.Add(new Item(weak ? 2 : 1, () => L.T("Network set as Private: this PC is visible to the others and file sharing is allowed. Use Public outside home.", "Rete impostata come Privata: questo PC è visibile agli altri e la condivisione file è permessa. Fuori casa usa Pubblica.")));
                }
                else if (r.Category == 0) r.Items.Add(new Item(0, () => L.T("Public network: this PC is hidden from the other devices", "Rete pubblica: questo PC è nascosto agli altri dispositivi")));
            }
            r.Items.Sort((a, b) => b.Level.CompareTo(a.Level));
            return r;
        }

        static int ReadConnection(IntPtr h, Guid id, Result r)
        {
            int size, rank = 3;
            IntPtr p;
            if (WlanQueryInterface(h, ref id, 7, IntPtr.Zero, out size, out p, IntPtr.Zero) != 0) return rank;
            try
            {
                r.Profile = Marshal.PtrToStringUni(new IntPtr(p.ToInt64() + 8)) ?? "";
                const int a = 520;
                r.Ssid = Ssid(p, a);
                var b = new byte[6];
                Marshal.Copy(new IntPtr(p.ToInt64() + a + 40), b, 0, 6);
                r.Bssid = Mac(b, 0, 6);
                bool secure = Marshal.ReadInt32(p, a + 68) != 0;
                int auth = Marshal.ReadInt32(p, a + 76), cipher = Marshal.ReadInt32(p, a + 80);
                if (!secure) { auth = 1; cipher = 0; }
                rank = Rank(auth, cipher);
                string c = CipherName(cipher);
                r.Security = AuthName(auth) + (c.Length > 0 ? " · " + c : "");
            }
            finally { WlanFreeMemory(p); }
            return rank;
        }

        // visible networks: SSID -> protection rank (one entry per SSID/security combination)
        static List<KeyValuePair<string, int>> Visible(IntPtr h, Guid id)
        {
            var res = new List<KeyValuePair<string, int>>();
            IntPtr list;
            if (WlanGetAvailableNetworkList(h, ref id, 0, IntPtr.Zero, out list) != 0) return res;
            try
            {
                int n = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = new IntPtr(list.ToInt64() + 8 + (long)i * 628);   // WLAN_AVAILABLE_NETWORK
                    string ssid = Ssid(e, 512);
                    if (ssid.Length == 0) continue;
                    bool secure = Marshal.ReadInt32(e, 608) != 0;
                    int auth = Marshal.ReadInt32(e, 612), cipher = Marshal.ReadInt32(e, 616);
                    res.Add(new KeyValuePair<string, int>(ssid, secure ? Rank(auth, cipher) : 0));
                }
            }
            finally { WlanFreeMemory(list); }
            return res;
        }

        static void ReadProfiles(IntPtr h, Guid id, Result r)
        {
            IntPtr list;
            if (WlanGetProfileList(h, ref id, IntPtr.Zero, out list) != 0) return;
            try
            {
                int n = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < n; i++)
                {
                    string name = Marshal.PtrToStringUni(new IntPtr(list.ToInt64() + 8 + i * 516)) ?? "";
                    IntPtr xml;
                    uint flags = 0, access;
                    if (WlanGetProfile(h, ref id, name, IntPtr.Zero, out xml, ref flags, out access) != 0) continue;
                    try
                    {
                        string x = Marshal.PtrToStringUni(xml) ?? "";
                        var p = new Saved { Name = name, Ssid = Tag(x, "name"), Auth = Tag(x, "authentication"), Encryption = Tag(x, "encryption") };
                        // <SSIDConfig><SSID><name> comes after the profile <name>: take the second one if present
                        var names = Regex.Matches(x, "<name>([^<]*)</name>", RegexOptions.IgnoreCase);
                        if (names.Count > 1) p.Ssid = System.Net.WebUtility.HtmlDecode(names[1].Groups[1].Value);
                        p.Auto = !Tag(x, "connectionMode").Equals("manual", StringComparison.OrdinalIgnoreCase);
                        p.Open = RankXml(p.Auth, p.Encryption) == 0;
                        string rnd = Tag(x, "enableRandomization");
                        p.Random = rnd.Length == 0 ? "" : rnd.Equals("true", StringComparison.OrdinalIgnoreCase) ? L.T("random MAC", "MAC casuale") : L.T("real MAC", "MAC reale");
                        r.Profiles.Add(p);
                    }
                    finally { WlanFreeMemory(xml); }
                }
            }
            finally { WlanFreeMemory(list); }
        }

        static void ReadIp(Result r)
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.Description != r.Adapter || ni.OperationalStatus != OperationalStatus.Up) continue;
                var ip = ni.GetIPProperties();
                foreach (var g in ip.GatewayAddresses)
                    if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { r.Gateway = g.Address.ToString(); break; }
                foreach (var d in ip.DnsAddresses) r.Dns.Add(d.ToString());
                break;
            }
            if (r.Gateway.Length > 0)
                foreach (var row in Arp()) if (row.Key == r.Gateway) { r.GatewayMac = row.Value; break; }
        }

        // IPv4 neighbor table: ip -> MAC (dynamic and static entries only)
        static List<KeyValuePair<string, string>> Arp()
        {
            var res = new List<KeyValuePair<string, string>>();
            int size = 0;
            GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return res;
            IntPtr t = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(t, ref size, false) != 0) return res;
                int n = Marshal.ReadInt32(t, 0);
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 24;                         // MIB_IPNETROW
                    int len = Math.Min(8, Marshal.ReadInt32(t, o + 4)), type = Marshal.ReadInt32(t, o + 20);
                    if (len != 6 || (type != 3 && type != 4)) continue;
                    var b = new byte[6];
                    Marshal.Copy(new IntPtr(t.ToInt64() + o + 8), b, 0, 6);
                    var addr = new System.Net.IPAddress((long)(uint)Marshal.ReadInt32(t, o + 16));
                    res.Add(new KeyValuePair<string, string>(addr.ToString(), Mac(b, 0, 6)));
                }
            }
            finally { Marshal.FreeHGlobal(t); }
            return res;
        }

        static List<string> SameMac(string mac, string gateway)
        {
            var res = new List<string>();
            if (mac.Length == 0) return res;
            foreach (var row in Arp())
                if (row.Value == mac && row.Key != gateway && !row.Key.EndsWith(".255") && !row.Key.StartsWith("224.") && !row.Key.StartsWith("239."))
                    res.Add(row.Key);
            return res;
        }

        // ------------------------------------------------ network category (Network List Manager)

        static int Category(string name)
        {
            try
            {
                dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
                foreach (dynamic net in nlm.GetNetworks(1))   // NLM_ENUM_NETWORK_CONNECTED
                {
                    string n = net.GetName();
                    if (n == name || n.StartsWith(name + " ")) return (int)net.GetCategory();
                }
                foreach (dynamic net in nlm.GetNetworks(1)) return (int)net.GetCategory();
            }
            catch { }
            return -1;
        }

        // Elevated (--net-public): sets the connected network with that name to Public.
        public static int SetPublic(string name)
        {
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
            foreach (dynamic net in nlm.GetNetworks(1))
            {
                string n = net.GetName();
                if (n == name || n.StartsWith(name + " ")) { net.SetCategory(0); return 0; }
            }
            return 2;
        }

        // Saved network -> join only when chosen (netsh changes the all-user profile: administrator).
        public static void SetManual(string profile)
        {
            if (profile.IndexOfAny(new[] { '"', '\r', '\n', '%', '^', '&', '|', '<', '>' }) >= 0) throw new ArgumentException(profile);
            var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", "wlan set profileparameter name=\"" + profile + "\" connectionmode=manual")
            { Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using (var p = System.Diagnostics.Process.Start(psi)) p.WaitForExit();
        }

        // ------------------------------------------------ 5. HTTPS interception (on request, network)

        // A proxy that decrypts HTTPS presents certificates from its own root, added to this PC's Root store.
        // Genuine sites chain to a root distributed by Microsoft (AuthRoot store) and validate without errors.
        public static List<Item> Https()
        {
            var res = new List<Item>();
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288;
            var auth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.AuthRoot, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine))
            {
                store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                foreach (var c in store.Certificates) auth.Add(c.Thumbprint);
            }
            foreach (string host in new[] { "www.microsoft.com", "www.google.com" })
            {
                string h = host, root = null, issuer = null;
                System.Net.Security.SslPolicyErrors errors = System.Net.Security.SslPolicyErrors.None;
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://" + host + "/");
                    req.Method = "HEAD";
                    req.Timeout = 8000;
                    req.AllowAutoRedirect = false;
                    req.ServerCertificateValidationCallback = (s, cert, chain, err) =>
                    {
                        errors = err;
                        if (chain != null && chain.ChainElements.Count > 0)
                        {
                            var top = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                            root = top.Thumbprint;
                            issuer = top.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                        }
                        return true;   // only inspected here: the response is not used
                    };
                    using (req.GetResponse()) { }
                }
                catch (System.Net.WebException ex) { if (root == null) { string m = ex.Message; res.Add(new Item(1, () => h + L.T(": not reachable (", ": non raggiungibile (") + m + ")")); continue; } }
                catch (Exception ex) { string m = ex.Message; res.Add(new Item(1, () => h + ": " + m)); continue; }
                string iss = issuer ?? "?";
                if (errors != System.Net.Security.SslPolicyErrors.None)
                    res.Add(new Item(2, () => h + L.T(": certificate NOT valid (", ": certificato NON valido (") + errors + L.T(") – connection probably intercepted", ") – connessione probabilmente intercettata")));
                else if (root != null && !auth.Contains(root))
                    res.Add(new Item(2, () => h + L.T(": certificate from a root added to this PC («", ": certificato emesso da un'autorità aggiunta a questo PC («") + iss + L.T("»): HTTPS is decrypted by a proxy or software", "»): l'HTTPS viene decifrato da un proxy o da un programma")));
                else
                    res.Add(new Item(0, () => h + L.T(": genuine certificate (", ": certificato autentico (") + iss + ")"));
            }
            return res;
        }

        // ------------------------------------------------ command line (tests without the window)

        public static int Cli(string[] a)
        {
            if (a.Length == 2 && a[0] == "--net-public")
            {
                try { return SetPublic(a[1]); }
                catch (Exception ex) { System.Windows.Forms.MessageBox.Show("Rete pubblica: " + ex.Message, "SnapControl"); return 1; }
            }
            var sb = new StringBuilder();
            try
            {
                var r = Check(true);
                sb.Append("connesso " + r.Connected + " · " + r.Ssid + " · profilo " + r.Profile + " · " + r.Security + " · categoria " + r.Category + "\n");
                sb.Append("router " + r.Gateway + " " + r.GatewayMac + " · DNS " + string.Join(", ", r.Dns) + "\n");
                foreach (var i in r.Items) sb.Append("[" + i.Level + "] " + i.Text() + "\n");
                foreach (var p in r.Profiles) sb.Append("salvata: " + p.Name + " · ssid " + p.Ssid + " · " + p.Auth + "/" + p.Encryption + " · " + (p.Auto ? "auto" : "manuale") + " · " + p.Random + "\n");
                if (a.Length == 2 && a[1] == "https") foreach (var i in Https()) sb.Append("[" + i.Level + "] " + i.Text() + "\n");
            }
            catch (Exception ex) { sb.Append("errore: " + ex + "\n"); }
            try { PowerSettings.Send(PowerSettings.StdOut, sb.ToString()); } catch { return 1; }
            return 0;
        }
    }
}
