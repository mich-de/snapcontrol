// SnapControl-Setup - dependency-free installer, built like the app with the .NET Framework 4.8.1 csc.
// Holds SnapControl.exe and the .config as resources; installs to Program Files, creates shortcuts
// on desktop and Start and registers in "Installed apps". Copied as uninstall.exe it acts as the uninstaller.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SnapControl Setup")]
[assembly: AssemblyCompany("mich-de")]
[assembly: AssemblyCopyright("mich-de - github.com/mich-de/snapcontrol")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8.1", FrameworkDisplayName = ".NET Framework 4.8.1")]

namespace SnapControl.Setup
{
    static class Program
    {
        const string App = "SnapControl";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SnapControl";
        static readonly string[] Payload = { "SnapControl.exe", "SnapControl.exe.config" };

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), App); } }
        static string Exe { get { return Path.Combine(Dir, "SnapControl.exe"); } }
        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), App + ".lnk"); } }
        static string StartLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), App + ".lnk"); } }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool quiet = Array.IndexOf(args, "/quiet") >= 0;
            try
            {
                if (Array.IndexOf(args, "/uninstall") >= 0) return Uninstall(quiet);
                return Install(quiet);
            }
            catch (Exception ex)
            {
                if (!quiet) MessageBox.Show("Operation failed:\n" + ex.Message, "SnapControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static int Install(bool quiet)
        {
            bool update = File.Exists(Exe);
            if (!quiet && MessageBox.Show((update ? "Update" : "Install") + " SnapControl " + Version() + " in\n" + Dir + "?\n\nShortcuts will be created on the desktop and in the Start menu.",
                    "SnapControl · setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return 2;

            Schtasks("/end");
            StopRunning();
            string uninstaller = Path.Combine(Dir, "uninstall.exe");
            try
            {
                Directory.CreateDirectory(Dir);
                var asm = Assembly.GetExecutingAssembly();
                foreach (string name in Payload)
                    using (var src = asm.GetManifestResourceStream(name))
                    using (var dst = File.Create(Path.Combine(Dir, name)))
                        src.CopyTo(dst);
                // the uninstaller is this same program
                if (!string.Equals(Application.ExecutablePath, uninstaller, StringComparison.OrdinalIgnoreCase))
                    File.Copy(Application.ExecutablePath, uninstaller, true);

                Shortcut(DesktopLink);
                Shortcut(StartLink);
            }
            // if the charge limit was active it restarts with the new executable, also after a failed copy
            finally { Schtasks("/run"); }

            long kb = 0;
            foreach (var f in new DirectoryInfo(Dir).GetFiles()) kb += f.Length / 1024;
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hklm.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", App);
                k.SetValue("DisplayVersion", Version());
                k.SetValue("Publisher", "mich-de");
                k.SetValue("URLInfoAbout", "https://github.com/mich-de/snapcontrol");
                k.SetValue("HelpLink", "https://github.com/mich-de/snapcontrol");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
            }

            if (!quiet && MessageBox.Show("SnapControl " + Version() + " has been " + (update ? "updated" : "installed") + ".\n\nStart it now?",
                    "SnapControl", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                // via Explorer so the app starts as a normal user and does not inherit the installer's privileges
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + Exe + "\"");
            return 0;
        }

        static int Uninstall(bool quiet)
        {
            if (!quiet && MessageBox.Show("Uninstall SnapControl?", "SnapControl", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return 2;

            // removes the charge limit task and resumes charging, otherwise it would stay stopped
            if (File.Exists(Exe))
                using (var off = Process.Start(new ProcessStartInfo(Exe, "--charge-limit off") { UseShellExecute = false, CreateNoWindow = true }))
                    // removing the app now would leave charging stopped with nothing to resume it
                    if (!off.WaitForExit(30000) || off.ExitCode != 0)
                    {
                        if (quiet || MessageBox.Show("Charging could not be resumed: without SnapControl it may stay stopped.\n\nUninstall anyway?", "SnapControl", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                            return 1;
                    }
            // also when the executable is missing or could not turn it off
            Schtasks("/end");
            Schtasks("/delete /f");
            string flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"SnapControl\charge-limit.on");
            if (File.Exists(flag)) File.Delete(flag);
            StopRunning();
            foreach (string l in new[] { DesktopLink, StartLink }) if (File.Exists(l)) File.Delete(l);
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (run != null) run.DeleteValue(App, false);
            // the automatic start is gone: a later install must not say it is on
            using (var set = Registry.CurrentUser.OpenSubKey(@"Software\SnapControl", true))
                if (set != null) set.DeleteValue("Tray", false);
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                hklm.DeleteSubKeyTree(UninstallKey, false);
            foreach (string name in Payload)
            {
                string p = Path.Combine(Dir, name);
                if (File.Exists(p)) File.Delete(p);
            }
            // uninstall.exe is in use: the folder is removed shortly after exit
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath()
            });

            if (!quiet) MessageBox.Show("SnapControl has been removed.", "SnapControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static string Version()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        // The app may be open or in the tray: it must be closed before overwriting the executable.
        static void StopRunning()
        {
            foreach (var p in Process.GetProcessesByName(App))
                try { p.Kill(); p.WaitForExit(5000); } catch { }
        }

        static void Schtasks(string verb)
        {
            using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), verb + " /tn \"SnapControl Limite carica\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
            {
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
        }

        static void Shortcut(string path)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = Exe;
            lnk.WorkingDirectory = Dir;
            lnk.IconLocation = Exe + ",0";
            lnk.Description = "Battery, power and drivers for IdeaPad Slim 5 14Q8X9";
            lnk.Save();
        }
    }
}
