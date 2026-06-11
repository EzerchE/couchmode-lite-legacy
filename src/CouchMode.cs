// CouchMode - automatically toggles the Windows 11 Xbox full screen
// experience (Xbox mode) based on Xbox controller connection state.
//
// Targets .NET Framework 4.8 (ships with Windows 11). Compile with build.ps1.
// License: MIT.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("CouchMode Lite")]
[assembly: AssemblyProduct("CouchMode Lite")]
[assembly: AssemblyDescription("Automatically switches the Windows 11 Xbox full screen experience based on your controller.")]
[assembly: AssemblyCompany("EzerchE")]
[assembly: AssemblyCopyright("Copyright (c) 2026 EzerchE. MIT License.")]
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]
[assembly: AssemblyInformationalVersion("1.5.0-beta")]

namespace CouchMode
{
    static class Program
    {
        public const string AppName = "CouchMode Lite";
        public const string Version = "1.5.0-beta";
        public const string RepoUrl = "https://github.com/EzerchE/couchmode-lite";

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "CouchMode_SingleInstance_{8F3A1C20-1E4B-4C2A-9D6E-7A1B2C3D4E5F}", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("CouchMode is already running (check the system tray).",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Pro gate. Resource control, game tweaks, and display switching are Pro
    //  features. In the free build they are shown but locked; in the Store build
    //  this reflects the purchase/trial license. There is a single check point so
    //  the free build can ship without any Pro logic at all.
    // ---------------------------------------------------------------------
    static class Pro
    {
        // Free build: false. These tabs/controls are shown but disabled to preview
        // what Pro offers. (Pro itself is a separate build.)
        public static bool IsUnlocked = false;

        // Short label shown on locked controls.
        public const string Badge = "Pro - coming soon";

        // Store listing link, set once the Microsoft Store page exists. Empty for
        // now, so the upsell just says "coming soon" without opening anything.
        public const string StoreUrl = "https://couchmode.app/";

        public static void ShowUpsell(IWin32Window owner)
        {
            string msg =
                "CouchMode Pro is coming soon!\r\n\r\n" +
                "Pro will add: close apps to free RAM, game tweaks (Do Not Disturb, " +
                "Game Bar, power plan, visual effects), display switching, and Steam " +
                "Big Picture / custom launcher modes.";
            if (!string.IsNullOrEmpty(StoreUrl))
            {
                msg += "\r\n\r\nOpen the website to learn more?";
                if (MessageBox.Show(owner, msg, "CouchMode Pro",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { Process.Start(StoreUrl); } catch { }
                }
            }
            else
            {
                MessageBox.Show(owner, msg, "CouchMode Pro",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Native interop: XInput, foreground/monitor queries, key injection
    // ---------------------------------------------------------------------
    static class Native
    {
        // --- FSE state detection ---
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);

        struct RECT { public int Left, Top, Right, Bottom; }
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        // True when a visible window whose title contains "Xbox" fully covers
        // the monitor it sits on (borderless full screen) -> Xbox mode is on.
        // Monitor-relative, so it works on any resolution / multi-monitor setup.
        public static bool IsXboxModeOn()
        {
            bool found = false;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(h, sb, 512);
                string t = sb.ToString();
                if (string.IsNullOrEmpty(t) || t.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                IntPtr mon = MonitorFromWindow(h, 2); // MONITOR_DEFAULTTONEAREST
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(mi);
                if (!GetMonitorInfo(mon, ref mi)) return true;
                RECT m = mi.rcMonitor;

                const int tol = 2;
                if (r.Left <= m.Left + tol && r.Top <= m.Top + tol &&
                    r.Right >= m.Right - tol && r.Bottom >= m.Bottom - tol)
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // --- Win+F11 (official toggle for the full screen experience) ---
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        const byte VK_LWIN = 0x5B;
        const byte VK_F11 = 0x7A;
        const uint KEYUP = 0x0002;

        public static void SendWinF11()
        {
            keybd_event(VK_LWIN, 0, 0, IntPtr.Zero);
            keybd_event(VK_F11, 0, 0, IntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(VK_F11, 0, KEYUP, IntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYUP, IntPtr.Zero);
        }


        // Minimizes all windows (clean desktop), like the shell's "Minimize all".
        // Not a toggle, so it's safe to call on disconnect.
        public static void MinimizeAll()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                try { t.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); }
                finally { Marshal.ReleaseComObject(shell); }
                Log.Write("Minimized all windows (returned to desktop).");
            }
            catch (Exception ex) { Log.Debug("MinimizeAll failed: " + ex.Message); }
        }

        // --- XInput controller detection ---
        struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern int XInputGetState14(int idx, out XINPUT_STATE s);
        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        static extern int XInputGetState13(int idx, out XINPUT_STATE s);

        static bool useLegacy = false;

        static int GetState(int idx, out XINPUT_STATE s)
        {
            if (!useLegacy)
            {
                try { return XInputGetState14(idx, out s); }
                catch { useLegacy = true; }
            }
            try { return XInputGetState13(idx, out s); }
            catch { s = new XINPUT_STATE(); return 1167; } // ERROR_DEVICE_NOT_CONNECTED
        }

        public static int ControllerCount()
        {
            int n = 0;
            for (int i = 0; i < 4; i++)
            {
                XINPUT_STATE s;
                if (GetState(i, out s) == 0) n++;
            }
            return n;
        }

        // --- XInput capabilities (for diagnostics) ---
        struct XINPUT_GAMEPAD_C { public ushort wButtons; public byte bLT, bRT; public short sLX, sLY, sRX, sRY; }
        struct XINPUT_VIBRATION_C { public ushort wLeft, wRight; }
        struct XINPUT_CAPABILITIES
        {
            public byte Type;
            public byte SubType;
            public ushort Flags;
            public XINPUT_GAMEPAD_C Gamepad;
            public XINPUT_VIBRATION_C Vibration;
        }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
        static extern int XInputGetCaps14(int idx, int flags, out XINPUT_CAPABILITIES c);
        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetCapabilities")]
        static extern int XInputGetCaps13(int idx, int flags, out XINPUT_CAPABILITIES c);

        static int GetCaps(int idx, out XINPUT_CAPABILITIES c)
        {
            if (!useLegacy) { try { return XInputGetCaps14(idx, 0, out c); } catch { } }
            try { return XInputGetCaps13(idx, 0, out c); } catch { c = new XINPUT_CAPABILITIES(); return 1167; }
        }

        static string SubTypeName(byte st)
        {
            switch (st)
            {
                case 0: return "unknown";
                case 1: return "gamepad";
                case 2: return "wheel";
                case 3: return "arcade-stick";
                case 4: return "flight-stick";
                case 5: return "dance-pad";
                case 6: return "guitar";
                case 7: return "guitar-alt";
                case 8: return "drum-kit";
                case 11: return "guitar-bass";
                case 19: return "arcade-pad";
                default: return "subtype-" + st;
            }
        }

        // Per-slot XInput report for the diagnostics log.
        public static string DescribeControllers()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("  XInput dll: " + (useLegacy ? "xinput1_3" : "xinput1_4"));
            for (int i = 0; i < 4; i++)
            {
                XINPUT_STATE s;
                int rc = GetState(i, out s);
                if (rc == 0)
                {
                    XINPUT_CAPABILITIES c;
                    string caps = (GetCaps(i, out c) == 0)
                        ? string.Format("subtype={0} {1}", SubTypeName(c.SubType),
                            ((c.Flags & 0x0004) != 0) ? "wireless" : "wired")
                        : "caps=n/a";
                    sb.AppendLine(string.Format("  slot {0}: CONNECTED  {1}  packet={2}", i, caps, s.dwPacketNumber));
                }
                else
                {
                    sb.AppendLine(string.Format("  slot {0}: empty (rc={1})", i, rc));
                }
            }
            return sb.ToString();
        }

        // Privacy: window titles can contain personal content (browser tabs,
        // document names, account names). The Xbox full screen shell window is
        // titled exactly "Xbox", so we only ever log that; any other window that
        // merely contains "Xbox" as a substring has its title redacted. We keep
        // the rect/monitor/verdict, which is all we need to diagnose detection.
        static string SafeTitle(string t)
        {
            if (t != null && string.Equals(t.Trim(), "Xbox", StringComparison.OrdinalIgnoreCase))
                return "Xbox";
            return "[redacted title]";
        }

        // Lists every visible window whose title contains "Xbox" with its rect,
        // monitor bounds, and whether it counts as full screen (Xbox mode on).
        public static string DescribeXboxMode()
        {
            List<string> lines = new List<string>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(h, sb, 512);
                string t = sb.ToString();
                if (string.IsNullOrEmpty(t) || t.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                IntPtr mon = MonitorFromWindow(h, 2);
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(mi);
                bool gotMon = GetMonitorInfo(mon, ref mi);
                RECT m = mi.rcMonitor;
                const int tol = 2;
                bool full = gotMon && r.Left <= m.Left + tol && r.Top <= m.Top + tol &&
                            r.Right >= m.Right - tol && r.Bottom >= m.Bottom - tol;
                lines.Add(string.Format("  '{0}' win[{1},{2},{3},{4}] mon[{5},{6},{7},{8}] -> {9}",
                    SafeTitle(t), r.Left, r.Top, r.Right, r.Bottom, m.Left, m.Top, m.Right, m.Bottom,
                    full ? "FULL SCREEN (Xbox mode)" : "not full screen"));
                return true;
            }, IntPtr.Zero);
            if (lines.Count == 0) return "  (no visible window with 'Xbox' in its title)" + Environment.NewLine;
            return string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
        }
    }

    // ---------------------------------------------------------------------
    //  Diagnostics snapshot for the activity log (debug logging only).
    //  Designed so users on any device/controller can send a log that is
    //  enough to diagnose issues remotely.
    // ---------------------------------------------------------------------
    static class Diagnostics
    {
        public static string Snapshot(int baseline)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==== DIAGNOSTICS ====");
            sb.AppendLine("App: " + Program.AppName + " v" + Program.Version);
            sb.AppendLine("OS: " + OsInfo());
            sb.AppendLine("Process: " + (Environment.Is64BitProcess ? "x64" : "x86")
                + " | OS 64-bit: " + Environment.Is64BitOperatingSystem
                + " | .NET: " + Environment.Version);
            sb.AppendLine("Monitors:");
            try
            {
                foreach (Screen sc in Screen.AllScreens)
                    sb.AppendLine(string.Format("  {0} bounds=[{1},{2},{3},{4}] primary={5}",
                        sc.DeviceName, sc.Bounds.Left, sc.Bounds.Top, sc.Bounds.Right, sc.Bounds.Bottom, sc.Primary));
            }
            catch (Exception ex) { sb.AppendLine("  (error: " + ex.Message + ")"); }
            sb.AppendLine("Baseline controller count: " + baseline);
            sb.AppendLine("Controllers (XInput):");
            sb.Append(Native.DescribeControllers());
            sb.AppendLine("HID game devices (WMI):");
            sb.Append(HidGameDevices());
            sb.AppendLine("Xbox mode now: " + (Native.IsXboxModeOn() ? "ON" : "OFF"));
            sb.AppendLine("Windows matching 'Xbox':");
            sb.Append(Native.DescribeXboxMode());
            sb.Append("=====================");
            return sb.ToString();
        }

        static string OsInfo()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string product = k.GetValue("ProductName") as string;
                        string display = k.GetValue("DisplayVersion") as string;
                        string build = k.GetValue("CurrentBuild") as string;
                        object ubr = k.GetValue("UBR");
                        return string.Format("{0} {1} (build {2}.{3})", product, display, build, ubr);
                    }
                }
            }
            catch { }
            return Environment.OSVersion.ToString();
        }

        static string HidGameDevices()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass='HIDClass'"))
                {
                    foreach (ManagementBaseObject mo in s.Get())
                    {
                        string name = mo["Name"] as string;
                        if (name == null) continue;
                        string low = name.ToLowerInvariant();
                        if (low.Contains("game") || low.Contains("controller") ||
                            low.Contains("gamepad") || low.Contains("xbox") || low.Contains("xinput"))
                        {
                            sb.AppendLine("  " + name + " [" + (mo["Status"] as string) + "]");
                        }
                    }
                }
            }
            catch (Exception ex) { return "  (unavailable: " + ex.Message + ")" + Environment.NewLine; }
            if (sb.Length == 0) sb.AppendLine("  (no HID game controllers matched)");
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------------
    //  Device watcher: a message-only window that receives device interface
    //  arrival/removal notifications. Fully event-driven - no polling, so
    //  idle CPU usage is zero.
    // ---------------------------------------------------------------------
    class DeviceWatcher : NativeWindow, IDisposable
    {
        public event Action DeviceChanged;

        const int WM_DEVICECHANGE = 0x0219;
        const int DBT_DEVICEARRIVAL = 0x8000;
        const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x0000;
        const int DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 0x0004;
        static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 255)]
            public string dbcc_name;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr RegisterDeviceNotification(IntPtr h, IntPtr filter, int flags);
        [DllImport("user32.dll")]
        static extern bool UnregisterDeviceNotification(IntPtr h);

        IntPtr notify = IntPtr.Zero;

        public DeviceWatcher()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "CouchMode.DeviceWatcher";
            cp.Parent = HWND_MESSAGE; // message-only window
            CreateHandle(cp);

            DEV_BROADCAST_DEVICEINTERFACE dbi = new DEV_BROADCAST_DEVICEINTERFACE();
            dbi.dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE));
            dbi.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE;
            IntPtr buf = Marshal.AllocHGlobal(dbi.dbcc_size);
            try
            {
                Marshal.StructureToPtr(dbi, buf, false);
                notify = RegisterDeviceNotification(this.Handle, buf,
                    DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
            {
                int e = m.WParam.ToInt32();
                if (e == DBT_DEVICEARRIVAL || e == DBT_DEVICEREMOVECOMPLETE)
                {
                    Action h = DeviceChanged;
                    if (h != null) h();
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (notify != IntPtr.Zero) { UnregisterDeviceNotification(notify); notify = IntPtr.Zero; }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // ---------------------------------------------------------------------
    //  Settings (simple key=value file in %AppData%\CouchMode\config.ini)
    // ---------------------------------------------------------------------
    class Settings
    {
        public bool EnableOnConnect = true;
        public bool DisableOnDisconnect = true;
        public bool StartWithWindows = false;
        public bool DebugLogging = false;

        // Reliability: how long to wait before acting on a controller connect /
        // disconnect. The off-delay is a grace period that absorbs brief
        // Bluetooth drops (a reconnect within the window cancels the turn-off).
        public int OnDelaySeconds = 0;
        public int OffDelaySeconds = 5;

        // What to open when a controller connects:
        //   "xbox"    = Windows Xbox mode / full screen experience (Win+F11)
        //   "steambp" = Steam Big Picture (steam://open|close/bigpicture)
        //   "custom"  = a launcher the user picks (CustomLauncherPath)
        public string Mode = "xbox";
        public string CustomLauncherPath = "";   // .exe/.lnk for "custom" mode
        // In Steam Big Picture mode, also turn on the Windows full screen experience
        // for its performance/background trimming. Off = open Steam BP only (faster).
        public bool SteamWithFse = true;

        // Resource control (apps only, no admin). Two simple lists for when
        // CouchMode turns ON, and two switches for when it turns OFF. Lists hold
        // full paths to .exe or .lnk files, separated by '|'.
        public bool ForceClose = false;            // false = graceful only; true = kill survivors
        public string CloseList = "";              // ON: apps to close (free memory)
        public string LaunchOnEnterList = "";      // ON: apps to launch (e.g. a game)
        public bool ReopenClosedOnExit = true;     // OFF: reopen the apps closed on connect
        public bool CloseLaunchedOnExit = true;    // OFF: close the apps launched on connect

        // Optional gaming tweaks applied on entering Xbox mode and reverted on exit.
        // All per-user (HKCU) or the active power scheme; originals are restored.
        public bool TweakDnd = false;            // silence notifications (toasts off)
        public bool TweakGameDvr = false;        // disable Game Bar background recording
        public bool TweakPowerPlan = false;      // switch power plan while in Xbox mode
        public string PowerSchemeGuid = "";      // target plan GUID ("" = High performance)
        public bool TweakVisualEffects = false;  // turn off transparency and UI animations
        public bool PowerPlanPluggedInOnly = true; // skip the power-plan switch on battery
        public string GameMode = "";             // Windows Game Mode: "" leave, "on", "off"
        // Display: switch screens when entering / leaving Xbox mode (uses the
        // built-in DisplaySwitch.exe). Values: "" (leave as-is), "internal" (PC
        // screen only), "clone" (duplicate), "extend", "external" (second screen only).
        public string DisplayOnXbox = "";
        public string DisplayOnExit = "";

        // Resource control timing. CloseTimeoutSeconds is the graceful wait before
        // a forced close; LaunchStaggerSeconds is the gap between launching/reopening
        // apps so they do not all start at once and spike the CPU.
        public int CloseTimeoutSeconds = 10;
        public int LaunchStaggerSeconds = 0;

        static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Program.AppName);
            }
        }
        public static string ConfigPath { get { return Path.Combine(Dir, "config.ini"); } }
        public static string LogPath { get { return Path.Combine(Dir, "app.log"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (string raw in File.ReadAllLines(ConfigPath))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "EnableOnConnect") s.EnableOnConnect = (v == "1");
                        else if (k == "DisableOnDisconnect") s.DisableOnDisconnect = (v == "1");
                        else if (k == "StartWithWindows") s.StartWithWindows = (v == "1");
                        else if (k == "DebugLogging") s.DebugLogging = (v == "1");
                        else if (k == "OnDelaySeconds") s.OnDelaySeconds = ParseInt(v, 0, 0, 30);
                        else if (k == "OffDelaySeconds") s.OffDelaySeconds = ParseInt(v, 5, 0, 60);
                        else if (k == "Mode") s.Mode = v;
                        else if (k == "CustomLauncherPath") s.CustomLauncherPath = v;
                        else if (k == "SteamWithFse") s.SteamWithFse = (v == "1");
                        else if (k == "ForceClose") s.ForceClose = (v == "1");
                        else if (k == "CloseList") s.CloseList = v;
                        else if (k == "LaunchOnEnterList") s.LaunchOnEnterList = v;
                        else if (k == "ReopenClosedOnExit") s.ReopenClosedOnExit = (v == "1");
                        else if (k == "CloseLaunchedOnExit") s.CloseLaunchedOnExit = (v == "1");
                        else if (k == "TweakDnd") s.TweakDnd = (v == "1");
                        else if (k == "TweakGameDvr") s.TweakGameDvr = (v == "1");
                        else if (k == "TweakPowerPlan") s.TweakPowerPlan = (v == "1");
                        else if (k == "PowerSchemeGuid") s.PowerSchemeGuid = v;
                        else if (k == "TweakVisualEffects") s.TweakVisualEffects = (v == "1");
                        else if (k == "PowerPlanPluggedInOnly") s.PowerPlanPluggedInOnly = (v == "1");
                        else if (k == "GameMode") s.GameMode = v;
                        else if (k == "CloseTimeoutSeconds") s.CloseTimeoutSeconds = ParseInt(v, 10, 1, 60);
                        else if (k == "LaunchStaggerSeconds") s.LaunchStaggerSeconds = ParseInt(v, 0, 0, 10);
                        else if (k == "DisplayOnXbox") s.DisplayOnXbox = v;
                        else if (k == "DisplayOnExit") s.DisplayOnExit = v;
                    }
                }
            }
            catch { }
            return s;
        }

        static int ParseInt(string v, int def, int min, int max)
        {
            int n;
            if (!int.TryParse(v, out n)) return def;
            if (n < min) n = min;
            if (n > max) n = max;
            return n;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("EnableOnConnect=" + (EnableOnConnect ? "1" : "0"));
                sb.AppendLine("DisableOnDisconnect=" + (DisableOnDisconnect ? "1" : "0"));
                sb.AppendLine("StartWithWindows=" + (StartWithWindows ? "1" : "0"));
                sb.AppendLine("DebugLogging=" + (DebugLogging ? "1" : "0"));
                sb.AppendLine("OnDelaySeconds=" + OnDelaySeconds);
                sb.AppendLine("OffDelaySeconds=" + OffDelaySeconds);
                sb.AppendLine("Mode=" + Mode);
                sb.AppendLine("CustomLauncherPath=" + CustomLauncherPath);
                sb.AppendLine("SteamWithFse=" + (SteamWithFse ? "1" : "0"));
                sb.AppendLine("ForceClose=" + (ForceClose ? "1" : "0"));
                sb.AppendLine("CloseList=" + CloseList);
                sb.AppendLine("LaunchOnEnterList=" + LaunchOnEnterList);
                sb.AppendLine("ReopenClosedOnExit=" + (ReopenClosedOnExit ? "1" : "0"));
                sb.AppendLine("CloseLaunchedOnExit=" + (CloseLaunchedOnExit ? "1" : "0"));
                sb.AppendLine("TweakDnd=" + (TweakDnd ? "1" : "0"));
                sb.AppendLine("TweakGameDvr=" + (TweakGameDvr ? "1" : "0"));
                sb.AppendLine("TweakPowerPlan=" + (TweakPowerPlan ? "1" : "0"));
                sb.AppendLine("PowerSchemeGuid=" + PowerSchemeGuid);
                sb.AppendLine("TweakVisualEffects=" + (TweakVisualEffects ? "1" : "0"));
                sb.AppendLine("PowerPlanPluggedInOnly=" + (PowerPlanPluggedInOnly ? "1" : "0"));
                sb.AppendLine("GameMode=" + GameMode);
                sb.AppendLine("CloseTimeoutSeconds=" + CloseTimeoutSeconds);
                sb.AppendLine("LaunchStaggerSeconds=" + LaunchStaggerSeconds);
                sb.AppendLine("DisplayOnXbox=" + DisplayOnXbox);
                sb.AppendLine("DisplayOnExit=" + DisplayOnExit);
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch { }
        }
    }

    static class Log
    {
        static readonly object gate = new object();

        // When false, only essential lines are written (start, mode changes, exit,
        // warnings). When true, verbose diagnostic detail is added too.
        public static bool Verbose = false;

        public static void Debug(string msg)
        {
            if (Verbose) Write(msg);
        }

        // Hard caps so the log can never grow without bound. When it exceeds
        // MaxBytes we keep only the most recent KeepBytes (trim to tail) instead
        // of wiping it, so the startup diagnostics and recent events survive.
        const long MaxBytes = 512 * 1024;
        const int KeepChars = 128 * 1024;

        public static void Write(string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Settings.LogPath));
                    string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}",
                        DateTime.Now, msg, Environment.NewLine);

                    if (File.Exists(Settings.LogPath) && new FileInfo(Settings.LogPath).Length > MaxBytes)
                    {
                        string all = File.ReadAllText(Settings.LogPath);
                        string keep = all.Length > KeepChars ? all.Substring(all.Length - KeepChars) : all;
                        File.WriteAllText(Settings.LogPath,
                            "[log trimmed to last " + (KeepChars / 1024) + " KB]" + Environment.NewLine + keep);
                    }
                    File.AppendAllText(Settings.LogPath, line);
                }
            }
            catch { }
        }
    }


    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void Apply(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enable)
                        key.SetValue(Program.AppName, "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue(Program.AppName) != null)
                        key.DeleteValue(Program.AppName, false);
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------
    //  Lightweight shared state for the settings dialog's live status panel.
    //  Privacy: holds only counts/flags and a short action label, never window
    //  titles or file paths.
    // ---------------------------------------------------------------------
    static class AppStatus
    {
        public static volatile string LastAction = "(none yet)";
        public static int Baseline;
        public static volatile bool AutomationOn = true;
    }

    // ---------------------------------------------------------------------
    //  Tray application
    // ---------------------------------------------------------------------
    class TrayContext : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly DeviceWatcher watcher;
        readonly System.Windows.Forms.Timer debounce;
        readonly System.Windows.Forms.Timer pending; // delayed/grace mode switch
        readonly Control sink; // marshals work back onto the UI thread
        readonly Icon iconActive;
        readonly Icon iconIdle;
        readonly ToolStripMenuItem miActive;
        ToolStripMenuItem miStartup;
        bool pendingTarget;
        bool pendingActive;

        Settings settings;
        int prevCount;
        // Number of controllers always present at startup (e.g. a handheld's
        // built-in gamepad). Xbox mode is wanted whenever the live count exceeds
        // this baseline, so plugging/unplugging extra controllers toggles it
        // correctly on both desktops (baseline 0) and handhelds (baseline >= 1).
        int baseline;
        bool automationOn = true;
        volatile bool busy = false;

        public TrayContext()
        {
            settings = Settings.Load();
            Log.Verbose = settings.DebugLogging;
            Startup.Apply(settings.StartWithWindows); // keep registry in sync

            sink = new Control();
            IntPtr force = sink.Handle; // force handle creation on the UI thread

            iconActive = MakeIcon(true);
            iconIdle = MakeIcon(false);

            miActive = new ToolStripMenuItem("Active", null, OnToggleActive);
            miActive.Checked = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem header = new ToolStripMenuItem(Program.AppName + " v" + Program.Version);
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miActive);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Turn Xbox mode on now", null, delegate { RunSetMode(true); }));
            menu.Items.Add(new ToolStripMenuItem("Turn Xbox mode off now", null, delegate { RunSetMode(false); }));
            miStartup = new ToolStripMenuItem("Start with Windows", null, OnToggleStartup);
            miStartup.Checked = settings.StartWithWindows;
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Settings…", null, OnSettings));
            menu.Items.Add(new ToolStripMenuItem("Open Log File", null, OnOpenLog));
            menu.Items.Add(new ToolStripMenuItem("About…", null, OnAbout));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

            tray = new NotifyIcon();
            tray.Icon = iconIdle;
            tray.Text = Program.AppName;
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += OnSettings;

            // Coalesces bursts of device messages and gives XInput a moment to
            // recognise a freshly powered controller. One-shot, so it only runs
            // briefly after a device event - idle CPU stays at zero.
            debounce = new System.Windows.Forms.Timer();
            debounce.Interval = 1000;
            debounce.Tick += OnDebounceTick;

            // One-shot timer that performs the actual switch after the user's
            // configured on/off delay (the off delay doubles as a grace period).
            pending = new System.Windows.Forms.Timer();
            pending.Tick += OnPendingTick;

            baseline = prevCount = Native.ControllerCount();
            AppStatus.Baseline = baseline;
            Log.Write(string.Format("Started (event-driven). Controllers={0} (baseline), XboxMode={1}, Debug={2}",
                prevCount, Native.IsXboxModeOn() ? "ON" : "OFF", Log.Verbose));
            if (Log.Verbose) Log.Write(Diagnostics.Snapshot(baseline));
            UpdateIcon();

            watcher = new DeviceWatcher();
            watcher.DeviceChanged += OnDeviceChanged;
        }

        // Fired on any device arrival/removal. Re-arm the debounce so the actual
        // evaluation happens shortly after the last message in a burst.
        void OnDeviceChanged()
        {
            Log.Debug("Device change notification received.");
            if (!automationOn) return;
            debounce.Stop();
            debounce.Start();
        }

        void OnDebounceTick(object sender, EventArgs e)
        {
            debounce.Stop();
            if (!automationOn || busy) return;

            int count;
            try { count = Native.ControllerCount(); }
            catch (Exception ex) { Log.Debug("ControllerCount failed: " + ex.Message); return; }

            if (Log.Verbose)
                Log.Write(string.Format("Evaluate: count={0} prevCount={1} baseline={2} xboxMode={3}{4}{5}",
                    count, prevCount, baseline, Native.IsXboxModeOn() ? "ON" : "OFF",
                    Environment.NewLine, Native.DescribeControllers()));

            // Xbox mode is wanted whenever the live count is above the baseline of
            // always-present controllers. Acting on baseline crossings (rather than
            // crossings of zero) works on handhelds, which expose a built-in gamepad
            // so the count never reaches zero, and keeps multi-controller setups
            // correct: removing one of several extra controllers does not exit Xbox
            // mode, only returning all the way to the baseline does.
            bool wasAbove = prevCount > baseline;
            bool isAbove = count > baseline;

            if (isAbove && !wasAbove && settings.EnableOnConnect)
            {
                Log.Write(string.Format("Controller connected ({0} -> {1}, baseline {2}).", prevCount, count, baseline));
                SchedulePending(true, settings.OnDelaySeconds);
            }
            else if (!isAbove && wasAbove && settings.DisableOnDisconnect)
            {
                Log.Write(string.Format("Controller disconnected ({0} -> {1}, baseline {2}).", prevCount, count, baseline));
                SchedulePending(false, settings.OffDelaySeconds);
            }

            prevCount = count;
            UpdateIcon();
        }

        // Schedules the actual mode switch after the configured delay. A later
        // opposite edge simply reschedules, so a brief disconnect+reconnect (or
        // vice versa) cancels the earlier intent before it fires.
        void SchedulePending(bool target, int seconds)
        {
            pendingTarget = target;
            pendingActive = true;
            pending.Stop();
            pending.Interval = Math.Max(1, seconds * 1000);
            pending.Start();
            if (seconds > 0)
                Log.Debug(string.Format("Pending {0} in {1}s.", target ? "ON" : "OFF", seconds));
        }

        void OnPendingTick(object sender, EventArgs e)
        {
            pending.Stop();
            if (!pendingActive) return;
            pendingActive = false;

            // Re-verify the controller state still matches the intent. This one
            // check absorbs brief disconnects (the grace period) without any
            // extra bookkeeping: if the state reverted during the wait, skip.
            bool stillAbove;
            try { stillAbove = Native.ControllerCount() > baseline; }
            catch { stillAbove = pendingTarget; }

            if (pendingTarget == stillAbove)
            {
                Log.Write(pendingTarget ? "Entering Xbox mode." : "Exiting Xbox mode.");
                RunSetMode(pendingTarget);
            }
            else
            {
                Log.Debug("Pending action cancelled (controller state reverted during delay).");
            }
        }

        // Toggle work runs on a background thread so the UI/tray never freezes.
        // When it finishes, refresh the tray icon on the UI thread so it reflects
        // the final Xbox mode state (the switch takes ~1s to settle).
        void RunSetMode(bool want)
        {
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Free build: just toggle the Windows Xbox full screen experience.
                // (Resource control, game tweaks, Steam Big Picture and custom
                // launcher modes are Pro features, not included in this build.)
                try { SetMode(want); }
                finally
                {
                    busy = false;
                    try
                    {
                        if (sink.IsHandleCreated)
                            sink.BeginInvoke((Action)UpdateIcon);
                    }
                    catch { }
                }
            });
        }

        static void SetMode(bool want)
        {
            Log.Debug(string.Format("SetMode(want={0}); current XboxMode={1}",
                want, Native.IsXboxModeOn() ? "ON" : "OFF"));
            if (Native.IsXboxModeOn() == want) return;

            Log.Debug("Sending Win+F11.");
            Native.SendWinF11();

            // Win+F11 is the official toggle. On a desktop it switches instantly,
            // so the wait below returns early. On handhelds Windows shows a
            // "Restart for better performance" prompt and waits for the user to
            // choose, so allow generous time when turning Xbox mode ON. We never
            // send a second keystroke or relaunch anything: doing so could toggle
            // the mode back off right after the user accepts, or override a
            // deliberate "Stay on desktop" choice.
            int timeout = want ? 20000 : 4000;
            if (WaitFor(want, timeout))
            {
                Log.Write(want ? "Xbox mode ON." : "Xbox mode OFF.");
                AppStatus.LastAction = string.Format("{0:HH:mm:ss}  {1}",
                    DateTime.Now, want ? "Entered Xbox mode" : "Returned to desktop");
                return;
            }

            Log.Write(want
                ? "Xbox mode did not turn on (prompt dismissed or still open)."
                : "Xbox mode did not turn off.");
            AppStatus.LastAction = string.Format("{0:HH:mm:ss}  {1}",
                DateTime.Now, want ? "Xbox mode did not turn on" : "Xbox mode did not turn off");
            if (Log.Verbose) Log.Write("Xbox windows at this point:" + Environment.NewLine + Native.DescribeXboxMode());
        }

        static bool WaitFor(bool want, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                Thread.Sleep(300);
                waited += 300;
                if (Native.IsXboxModeOn() == want) return true;
            }
            return false;
        }

        void UpdateIcon()
        {
            bool on = false;
            try { on = Native.IsXboxModeOn(); }
            catch { }
            // Green whenever automation is active (the "Active" menu item is checked),
            // grey when paused - independent of whether Xbox mode is currently on.
            tray.Icon = automationOn ? iconActive : iconIdle;
            tray.Text = string.Format("{0}: {1}{2}",
                Program.AppName,
                automationOn ? "Active" : "Paused",
                on ? " (Xbox mode ON)" : "");
        }

        void OnToggleActive(object sender, EventArgs e)
        {
            automationOn = !automationOn;
            miActive.Checked = automationOn;
            AppStatus.AutomationOn = automationOn;
            Log.Debug("Automation " + (automationOn ? "resumed." : "paused."));
            if (automationOn) prevCount = Native.ControllerCount();
            else { pending.Stop(); pendingActive = false; }
            UpdateIcon();
        }

        void OnToggleStartup(object sender, EventArgs e)
        {
            settings.StartWithWindows = !settings.StartWithWindows;
            miStartup.Checked = settings.StartWithWindows;
            Startup.Apply(settings.StartWithWindows);
            settings.Save();
            Log.Debug("Start with Windows " + (settings.StartWithWindows ? "enabled." : "disabled."));
        }

        bool settingsOpen;
        void OnSettings(object sender, EventArgs e)
        {
            if (settingsOpen) return; // tray clicks while the dialog is open are ignored
            settingsOpen = true;
            try
            {
                using (SettingsForm f = new SettingsForm(settings))
                {
                    if (f.ShowDialog() == DialogResult.OK)
                    {
                        settings = f.Result;
                        settings.Save();
                        Log.Verbose = settings.DebugLogging;
                        Startup.Apply(settings.StartWithWindows);
                        miStartup.Checked = settings.StartWithWindows;
                        Log.Debug("Settings saved.");
                    }
                }
            }
            finally { settingsOpen = false; }
        }

        void OnOpenLog(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Settings.LogPath));
                if (!File.Exists(Settings.LogPath))
                    File.WriteAllText(Settings.LogPath, "(log is empty - enable Debug logging in Settings for detail)" + Environment.NewLine);
                // Open with Notepad: a .log file has no default association, so
                // launching it directly fails with "path not found".
                Process.Start("notepad.exe", "\"" + Settings.LogPath + "\"");
            }
            catch { }
        }

        void OnAbout(object sender, EventArgs e)
        {
            using (AboutForm f = new AboutForm())
                f.ShowDialog();
        }

        void OnExit(object sender, EventArgs e)
        {
            debounce.Stop();
            if (pending != null) pending.Stop();
            if (watcher != null) watcher.Dispose();
            if (sink != null) sink.Dispose();
            tray.Visible = false;
            Log.Write("Exit.");
            ExitThread();
        }

        static Bitmap couchSrc;
        static bool couchSrcTried;

        // Loads the couch artwork embedded in the exe (the same source the build
        // uses for the app icon), so the tray icon matches the app icon exactly.
        static Bitmap CouchSource()
        {
            if (couchSrcTried) return couchSrc;
            couchSrcTried = true;
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("couch-src.png");
                if (s != null) using (s) couchSrc = new Bitmap(s);
            }
            catch { couchSrc = null; }
            return couchSrc;
        }

        // Tray icon: the couch recoloured to the brand colour when active, grey when
        // paused (alpha preserved so anti-aliased edges stay smooth). Falls back to a
        // simple drawn couch if the embedded artwork is missing.
        static Icon MakeIcon(bool active)
        {
            Color fill = active ? Color.FromArgb(16, 124, 16) : Color.FromArgb(110, 110, 110);
            const int S = 32;
            Bitmap src = CouchSource();
            Bitmap bmp = new Bitmap(S, S);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    if (src != null)
                    {
                        g.DrawImage(src, 1, 1, S - 2, S - 2);
                    }
                    else
                    {
                        // Fallback: the previous hand-drawn couch silhouette.
                        using (SolidBrush b = new SolidBrush(fill))
                        {
                            using (var back = RoundRect(6f, 8f, 20f, 10f, 4f)) g.FillPath(b, back);
                            using (var armL = RoundRect(2f, 12f, 7f, 12f, 3.5f)) g.FillPath(b, armL);
                            using (var armR = RoundRect(23f, 12f, 7f, 12f, 3.5f)) g.FillPath(b, armR);
                            using (var seat = RoundRect(4f, 16f, 24f, 8f, 3f)) g.FillPath(b, seat);
                            g.FillRectangle(b, 5f, 23f, 3f, 3.5f);
                            g.FillRectangle(b, 24f, 23f, 3f, 3.5f);
                        }
                        return Icon.FromHandle(bmp.GetHicon());
                    }
                }
                // Recolour every opaque pixel to the brand/grey colour.
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        Color p = bmp.GetPixel(x, y);
                        if (p.A > 0) bmp.SetPixel(x, y, Color.FromArgb(p.A, fill.R, fill.G, fill.B));
                    }
                return Icon.FromHandle(bmp.GetHicon());
            }
            finally { bmp.Dispose(); }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            float d = r * 2f;
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------------------------------------------------------------------
    //  Minimal settings dialog
    // ---------------------------------------------------------------------
    // Holds a full path but shows only the file name in list boxes.
    class AppItem
    {
        public readonly string Path;
        public AppItem(string path) { Path = path; }
        public override string ToString() { return System.IO.Path.GetFileName(Path); }
    }

    class SettingsForm : Form
    {
        readonly CheckBox cbConnect, cbDisconnect, cbStartup, cbDebug;
        readonly CheckBox cbForce, cbReopen, cbCloseLaunched;
        readonly CheckBox cbDnd, cbGameDvr, cbPower, cbVisual, cbPowerPlugged;
        readonly ListBox lstClose, lstLaunchEnter;
        readonly ComboBox cmbPower, cmbDispXbox, cmbDispExit, cmbMode, cmbGameMode;
        readonly TextBox txtCustomLauncher;
        readonly Button btnBrowseLauncher;
        readonly CheckBox cbSteamFse;
        readonly NumericUpDown numOnDelay, numOffDelay, numCloseTimeout, numLaunchStagger;
        readonly Label lblStat1, lblStat2;
        readonly System.Windows.Forms.Timer statusTimer;
        readonly List<string> schemeGuids = new List<string>();
        public Settings Result;

        // Mode dropdown options: (label, stored value).
        static readonly string[][] ModeOptions = new string[][]
        {
            new string[] { "Xbox mode (full screen experience)", "xbox" },
            new string[] { "Steam Big Picture", "steambp" },
            new string[] { "Custom launcher", "custom" },
        };

        // Windows Game Mode dropdown options: (label, stored value).
        static readonly string[][] GameModeOptions = new string[][]
        {
            new string[] { "Leave as-is", "" },
            new string[] { "Turn on", "on" },
            new string[] { "Turn off", "off" },
        };

        // Display dropdown options: (label, stored value).
        static readonly string[][] DispOptions = new string[][]
        {
            new string[] { "Leave as-is", "" },
            new string[] { "PC screen only", "internal" },
            new string[] { "Second screen only", "external" },
            new string[] { "Duplicate", "clone" },
            new string[] { "Extend", "extend" },
        };

        public SettingsForm(Settings current)
        {
            Result = current;
            Text = Program.AppName + " Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(440, 530);

            TabControl tabs = new TabControl();
            tabs.SetBounds(8, 8, 424, 470);

            // ---- General tab ----
            TabPage tabGeneral = new TabPage("General");

            // Live status block, refreshed by statusTimer while the dialog is open.
            GroupBox grpStatus = new GroupBox();
            grpStatus.Text = "Status";
            grpStatus.SetBounds(12, 6, 398, 58);
            lblStat1 = new Label(); lblStat1.SetBounds(10, 18, 380, 16);
            lblStat2 = new Label(); lblStat2.SetBounds(10, 36, 380, 16);
            lblStat2.ForeColor = SystemColors.GrayText;
            grpStatus.Controls.Add(lblStat1);
            grpStatus.Controls.Add(lblStat2);

            cbConnect = Check("Turn on when a controller connects", current.EnableOnConnect, 16, 76);
            cbDisconnect = Check("Turn off when all controllers disconnect", current.DisableOnDisconnect, 16, 104);

            Label mlbl = Lbl("When CouchMode turns on, open:", 16, 140);
            cmbMode = new ComboBox();
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.SetBounds(16, 160, 300, 24);
            int msel = 0;
            for (int i = 0; i < ModeOptions.Length; i++)
            {
                cmbMode.Items.Add(ModeOptions[i][0]);
                if (ModeOptions[i][1] == (current.Mode ?? "xbox")) msel = i;
            }
            cmbMode.SelectedIndex = msel;

            txtCustomLauncher = new TextBox();
            txtCustomLauncher.SetBounds(16, 192, 300, 22);
            txtCustomLauncher.Text = current.CustomLauncherPath;
            btnBrowseLauncher = Btn("Browse…", 322, 191);
            btnBrowseLauncher.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "Choose a launcher (.exe or .lnk)";
                    d.Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK) txtCustomLauncher.Text = d.FileName;
                }
            };
            cmbMode.SelectedIndexChanged += delegate { UpdateModeEnabled(); };

            // Shown only for Steam Big Picture mode (shares the row with the custom path).
            cbSteamFse = Check("Also enable Xbox full screen experience (better performance)",
                current.SteamWithFse, 16, 194);

            // Steam Big Picture / custom launcher are Pro: lock the chooser to Xbox
            // mode in the free build and offer an unlock link.
            LinkLabel modeUpsell = null;
            if (!Pro.IsUnlocked)
            {
                cmbMode.SelectedIndex = 0; // Xbox mode
                cmbMode.Enabled = false;
                modeUpsell = new LinkLabel();
                modeUpsell.Text = "Steam Big Picture & custom launcher (Pro - coming soon)";
                modeUpsell.LinkColor = Color.FromArgb(16, 124, 16);
                modeUpsell.SetBounds(16, 192, 380, 20);
                modeUpsell.LinkClicked += delegate { Pro.ShowUpsell(this); };
            }

            cbStartup = Check("Start automatically with Windows", current.StartWithWindows, 16, 228);

            // Advanced (collapsed by default): timing knobs and debug logging.
            LinkLabel advLink = new LinkLabel();
            advLink.Text = "Advanced settings";
            advLink.SetBounds(16, 258, 200, 18);
            GroupBox grpAdv = new GroupBox();
            grpAdv.Text = "Advanced";
            grpAdv.SetBounds(12, 280, 398, 100);
            grpAdv.Visible = false;
            Label la1 = new Label(); la1.Text = "Wait before turning on (seconds):"; la1.SetBounds(10, 24, 220, 18);
            numOnDelay = new NumericUpDown(); numOnDelay.SetBounds(238, 22, 56, 22);
            numOnDelay.Minimum = 0; numOnDelay.Maximum = 30; numOnDelay.Value = Clamp(current.OnDelaySeconds, 0, 30);
            Label la2 = new Label(); la2.Text = "Wait before turning off (seconds):"; la2.SetBounds(10, 50, 220, 18);
            numOffDelay = new NumericUpDown(); numOffDelay.SetBounds(238, 48, 56, 22);
            numOffDelay.Minimum = 0; numOffDelay.Maximum = 60; numOffDelay.Value = Clamp(current.OffDelaySeconds, 0, 60);
            cbDebug = Check("Debug logging (verbose activity log)", current.DebugLogging, 10, 74);
            cbDebug.SetBounds(10, 74, 370, 22);
            grpAdv.Controls.Add(la1); grpAdv.Controls.Add(numOnDelay);
            grpAdv.Controls.Add(la2); grpAdv.Controls.Add(numOffDelay);
            grpAdv.Controls.Add(cbDebug);
            advLink.LinkClicked += delegate { grpAdv.Visible = !grpAdv.Visible; };

            tabGeneral.Controls.Add(grpStatus);
            tabGeneral.Controls.Add(cbConnect);
            tabGeneral.Controls.Add(cbDisconnect);
            tabGeneral.Controls.Add(mlbl);
            tabGeneral.Controls.Add(cmbMode);
            tabGeneral.Controls.Add(txtCustomLauncher);
            tabGeneral.Controls.Add(btnBrowseLauncher);
            tabGeneral.Controls.Add(cbSteamFse);
            if (modeUpsell != null) tabGeneral.Controls.Add(modeUpsell);
            tabGeneral.Controls.Add(cbStartup);
            tabGeneral.Controls.Add(advLink);
            tabGeneral.Controls.Add(grpAdv);
            UpdateModeEnabled();

            // Live status panel: poll lightweight state once a second while open.
            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 1000;
            statusTimer.Tick += delegate { RefreshStatus(); };
            Load += delegate { RefreshStatus(); statusTimer.Start(); };
            FormClosed += delegate { statusTimer.Stop(); statusTimer.Dispose(); };

            // ---- Resource control tab ----
            // Two simple sections: what happens when CouchMode turns ON (close /
            // launch lists) and when it turns OFF (reverse those, plus options).
            TabPage tabRes = new TabPage("Resource control");

            Label onHdr = Lbl("When CouchMode turns ON (controller connected)", 12, 10);
            onHdr.Font = new Font(onHdr.Font, FontStyle.Bold);

            lstClose = new ListBox();
            lstClose.SetBounds(12, 52, 286, 70);
            lstClose.HorizontalScrollbar = true;
            Button addClose = Btn("File...", 306, 52);
            addClose.Click += delegate { AddTo(lstClose); };
            Button addCloseRun = Btn("Running...", 306, 82);
            addCloseRun.Click += delegate { AddRunningTo(lstClose); };
            Button remClose = Btn("Remove", 306, 112);
            remClose.Click += delegate { RemoveFrom(lstClose); };

            lstLaunchEnter = new ListBox();
            lstLaunchEnter.SetBounds(12, 168, 286, 70);
            lstLaunchEnter.HorizontalScrollbar = true;
            Button addEnter = Btn("File...", 306, 168);
            addEnter.Click += delegate { AddTo(lstLaunchEnter); };
            Button addEnterRun = Btn("Running...", 306, 198);
            addEnterRun.Click += delegate { AddRunningTo(lstLaunchEnter); };
            Button remEnter = Btn("Remove", 306, 228);
            remEnter.Click += delegate { RemoveFrom(lstLaunchEnter); };

            Label staggerLbl = Lbl("Stagger launches (seconds):", 12, 246);
            numLaunchStagger = new NumericUpDown();
            numLaunchStagger.SetBounds(206, 244, 56, 22);
            numLaunchStagger.Minimum = 0; numLaunchStagger.Maximum = 10;
            numLaunchStagger.Value = Clamp(current.LaunchStaggerSeconds, 0, 10);

            Label offHdr = Lbl("When CouchMode turns OFF (controllers disconnected)", 12, 278);
            offHdr.Font = new Font(offHdr.Font, FontStyle.Bold);
            cbReopen = Check("Reopen the apps that were closed", current.ReopenClosedOnExit, 16, 304);
            cbCloseLaunched = Check("Close the apps that were launched", current.CloseLaunchedOnExit, 16, 328);

            cbForce = Check("Force close apps after timeout", current.ForceClose, 16, 354);
            cbForce.CheckedChanged += delegate
            {
                if (cbForce.Checked)
                {
                    DialogResult r = MessageBox.Show(this,
                        "Force close ends apps that don't close on their own. Any unsaved work in them can be lost.\r\n\r\nEnable force close anyway?",
                        "Force close", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) cbForce.Checked = false;
                }
                numCloseTimeout.Enabled = cbForce.Checked;
            };
            Label toLbl = Lbl("Force close timeout (seconds):", 36, 382);
            numCloseTimeout = new NumericUpDown();
            numCloseTimeout.SetBounds(266, 380, 56, 22);
            numCloseTimeout.Minimum = 1; numCloseTimeout.Maximum = 60;
            numCloseTimeout.Value = Clamp(current.CloseTimeoutSeconds, 1, 60);
            numCloseTimeout.Enabled = cbForce.Checked;
            Label toNote = Lbl("Ends an app only if it ignores the close request.", 36, 404);
            toNote.ForeColor = SystemColors.GrayText;

            tabRes.Controls.Add(onHdr);
            tabRes.Controls.Add(Lbl("Apps to close (free memory):", 12, 32));
            tabRes.Controls.Add(lstClose);
            tabRes.Controls.Add(addClose);
            tabRes.Controls.Add(addCloseRun);
            tabRes.Controls.Add(remClose);
            tabRes.Controls.Add(Lbl("Apps to launch (e.g. a game):", 12, 148));
            tabRes.Controls.Add(lstLaunchEnter);
            tabRes.Controls.Add(addEnter);
            tabRes.Controls.Add(addEnterRun);
            tabRes.Controls.Add(remEnter);
            tabRes.Controls.Add(staggerLbl);
            tabRes.Controls.Add(numLaunchStagger);
            tabRes.Controls.Add(offHdr);
            tabRes.Controls.Add(cbReopen);
            tabRes.Controls.Add(cbCloseLaunched);
            tabRes.Controls.Add(cbForce);
            tabRes.Controls.Add(toLbl);
            tabRes.Controls.Add(numCloseTimeout);
            tabRes.Controls.Add(toNote);

            // ---- Session tweaks tab ----
            // Temporary, reversible adjustments while CouchMode is on.
            TabPage tabTweaks = new TabPage("Session tweaks");
            cbDnd = Check("Silence notifications (Do Not Disturb)", current.TweakDnd, 12, 12);
            cbGameDvr = Check("Disable Game Bar background recording", current.TweakGameDvr, 12, 38);
            cbVisual = Check("Turn off transparency and animations", current.TweakVisualEffects, 12, 64);

            Label gmLbl = Lbl("Windows Game Mode:", 12, 92);
            cmbGameMode = new ComboBox();
            cmbGameMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbGameMode.SetBounds(150, 90, 160, 24);
            int gmSel = 0;
            for (int i = 0; i < GameModeOptions.Length; i++)
            {
                cmbGameMode.Items.Add(GameModeOptions[i][0]);
                if (GameModeOptions[i][1] == (current.GameMode ?? "")) gmSel = i;
            }
            cmbGameMode.SelectedIndex = gmSel;

            cbPower = Check("Switch power plan to:", current.TweakPowerPlan, 12, 124);
            cbPower.SetBounds(12, 124, 150, 22);
            cmbPower = new ComboBox();
            cmbPower.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPower.SetBounds(166, 122, 230, 24);
            // Power plan list is a Pro feature; the tab is locked in the free build,
            // so the combo is left empty here.
            cbPowerPlugged = Check("Only when plugged in (skip on battery)", current.PowerPlanPluggedInOnly, 30, 150);

            Label dlbl1 = Lbl("Display when CouchMode turns on:", 12, 180);
            cmbDispXbox = DisplayCombo(current.DisplayOnXbox, 12, 200);
            Label dlbl2 = Lbl("Display when it turns off:", 12, 234);
            cmbDispExit = DisplayCombo(current.DisplayOnExit, 12, 254);

            Label tnote = Lbl("These changes are restored when CouchMode turns off.", 12, 290);
            tnote.ForeColor = SystemColors.GrayText;

            tabTweaks.Controls.Add(cbDnd);
            tabTweaks.Controls.Add(cbGameDvr);
            tabTweaks.Controls.Add(cbVisual);
            tabTweaks.Controls.Add(gmLbl);
            tabTweaks.Controls.Add(cmbGameMode);
            tabTweaks.Controls.Add(cbPower);
            tabTweaks.Controls.Add(cmbPower);
            tabTweaks.Controls.Add(cbPowerPlugged);
            tabTweaks.Controls.Add(dlbl1);
            tabTweaks.Controls.Add(cmbDispXbox);
            tabTweaks.Controls.Add(dlbl2);
            tabTweaks.Controls.Add(cmbDispExit);
            tabTweaks.Controls.Add(tnote);

            tabs.TabPages.Add(tabGeneral);
            tabs.TabPages.Add(tabRes);
            tabs.TabPages.Add(tabTweaks);

            // Pro gate: in the free build these tabs are visible but locked.
            if (!Pro.IsUnlocked)
            {
                LockTab(tabRes);
                LockTab(tabTweaks);
            }

            Populate(lstClose, current.CloseList);
            Populate(lstLaunchEnter, current.LaunchOnEnterList);

            // Right-aligned with a margin from the window edge (client width 440).
            Button ok = Btn("Save", 244, 486);
            ok.DialogResult = DialogResult.OK;
            ok.Click += OnSave;
            Button cancel = Btn("Cancel", 340, 486);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(tabs);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        static CheckBox Check(string text, bool chk, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text; c.Checked = chk; c.SetBounds(x, y, 384, 22);
            return c;
        }
        static Label Lbl(string text, int x, int y)
        {
            // AutoSize so the label hugs its text and never overlaps (and hides) a
            // control placed to its right on the same row.
            Label l = new Label(); l.AutoSize = true; l.Text = text; l.Location = new Point(x, y);
            return l;
        }
        static Button Btn(string text, int x, int y)
        {
            Button b = new Button(); b.Text = text; b.SetBounds(x, y, 88, 28);
            return b;
        }
        static ComboBox DisplayCombo(string value, int x, int y)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.SetBounds(x, y, 230, 24);
            int sel = 0;
            for (int i = 0; i < DispOptions.Length; i++)
            {
                c.Items.Add(DispOptions[i][0]);
                if (DispOptions[i][1] == (value ?? "")) sel = i;
            }
            c.SelectedIndex = sel;
            return c;
        }
        static string DisplayValue(ComboBox c)
        {
            int i = c.SelectedIndex;
            return (i >= 0 && i < DispOptions.Length) ? DispOptions[i][1] : "";
        }

        static decimal Clamp(int v, int min, int max)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        // Updates the General-tab status block. Privacy: only counts/flags and a
        // short action label, never window titles or file paths.
        void RefreshStatus()
        {
            int count = 0; bool on = false;
            try { count = Native.ControllerCount(); } catch { }
            try { on = Native.IsXboxModeOn(); } catch { }
            int baseline = AppStatus.Baseline;
            string ctrl = (count > baseline)
                ? string.Format("connected ({0})", count)
                : string.Format("disconnected (baseline {0})", baseline);
            lblStat1.Text = string.Format("Controller: {0}     CouchMode: {1}{2}",
                ctrl, on ? "On" : "Off", AppStatus.AutomationOn ? "" : "   (paused)");
            lblStat2.Text = "Last action: " + AppStatus.LastAction;
        }

        // Disables every control on a Pro tab and adds a clickable unlock banner.
        void LockTab(TabPage page)
        {
            List<Control> existing = new List<Control>();
            foreach (Control c in page.Controls) existing.Add(c);
            foreach (Control c in existing) { c.Enabled = false; c.Top += 28; }

            LinkLabel banner = new LinkLabel();
            banner.Text = "Pro feature - coming soon (click for details)";
            banner.SetBounds(10, 6, 420, 20);
            banner.LinkColor = Color.FromArgb(16, 124, 16);
            banner.LinkClicked += delegate { Pro.ShowUpsell(this); };
            page.Controls.Add(banner);
            banner.BringToFront();
        }

        void UpdateModeEnabled()
        {
            bool custom = cmbMode.SelectedIndex == 2; // "custom"
            bool steam = cmbMode.SelectedIndex == 1;  // "steambp"
            txtCustomLauncher.Visible = custom;
            btnBrowseLauncher.Visible = custom;
            cbSteamFse.Visible = steam;
        }

        void AddTo(ListBox lb)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choose a program or shortcut";
                d.Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*";
                d.Multiselect = true;
                if (d.ShowDialog(this) == DialogResult.OK)
                    AddPaths(lb, d.FileNames);
            }
        }

        void AddRunningTo(ListBox lb)
        {
            using (RunningAppsForm f = new RunningAppsForm())
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                    AddPaths(lb, f.SelectedPaths.ToArray());
            }
        }

        // Adds paths to a list, skipping ones already there or already in the OTHER
        // list (an app cannot be both closed and launched). Warns once if any were
        // skipped for being in the other list.
        void AddPaths(ListBox lb, string[] paths)
        {
            ListBox other = (lb == lstClose) ? lstLaunchEnter : lstClose;
            bool clash = false;
            foreach (string p in paths)
            {
                if (Contains(other, p)) { clash = true; continue; }
                if (!Contains(lb, p)) lb.Items.Add(new AppItem(p));
            }
            if (clash)
                MessageBox.Show(this,
                    "Some apps were skipped because they are already in the other list. "
                    + "An app cannot be both closed and launched.",
                    "Already listed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void RemoveFrom(ListBox lb)
        {
            if (lb.SelectedItem != null) lb.Items.Remove(lb.SelectedItem);
        }

        static bool Contains(ListBox lb, string path)
        {
            foreach (object o in lb.Items)
            {
                AppItem a = o as AppItem;
                if (a != null && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static void Populate(ListBox lb, string list)
        {
            if (string.IsNullOrEmpty(list)) return;
            foreach (string raw in list.Split('|'))
            {
                string p = raw.Trim();
                if (p.Length > 0) lb.Items.Add(new AppItem(p));
            }
        }

        static string Join(ListBox lb)
        {
            List<string> paths = new List<string>();
            foreach (object o in lb.Items)
            {
                AppItem a = o as AppItem;
                if (a != null) paths.Add(a.Path);
            }
            return string.Join("|", paths.ToArray());
        }

        void OnSave(object sender, EventArgs e)
        {
            Settings s = new Settings();
            s.EnableOnConnect = cbConnect.Checked;
            s.DisableOnDisconnect = cbDisconnect.Checked;
            s.StartWithWindows = cbStartup.Checked;
            s.DebugLogging = cbDebug.Checked;
            s.OnDelaySeconds = (int)numOnDelay.Value;
            s.OffDelaySeconds = (int)numOffDelay.Value;
            int mi = cmbMode.SelectedIndex;
            s.Mode = (mi >= 0 && mi < ModeOptions.Length) ? ModeOptions[mi][1] : "xbox";
            s.CustomLauncherPath = txtCustomLauncher.Text.Trim();
            s.SteamWithFse = cbSteamFse.Checked;
            s.ForceClose = cbForce.Checked;
            s.CloseTimeoutSeconds = (int)numCloseTimeout.Value;
            s.LaunchStaggerSeconds = (int)numLaunchStagger.Value;
            s.CloseList = Join(lstClose);
            s.LaunchOnEnterList = Join(lstLaunchEnter);
            s.ReopenClosedOnExit = cbReopen.Checked;
            s.CloseLaunchedOnExit = cbCloseLaunched.Checked;
            s.TweakDnd = cbDnd.Checked;
            s.TweakGameDvr = cbGameDvr.Checked;
            s.TweakPowerPlan = cbPower.Checked;
            s.PowerPlanPluggedInOnly = cbPowerPlugged.Checked;
            int pi = cmbPower.SelectedIndex;
            s.PowerSchemeGuid = (pi >= 0 && pi < schemeGuids.Count) ? schemeGuids[pi] : "";
            int gm = cmbGameMode.SelectedIndex;
            s.GameMode = (gm >= 0 && gm < GameModeOptions.Length) ? GameModeOptions[gm][1] : "";
            s.TweakVisualEffects = cbVisual.Checked;
            s.DisplayOnXbox = DisplayValue(cmbDispXbox);
            s.DisplayOnExit = DisplayValue(cmbDispExit);
            Result = s;
        }
    }

    // ---------------------------------------------------------------------
    //  Picker that lists currently running windowed apps (by RAM) so the user
    //  can add them without hunting for the executable on disk.
    // ---------------------------------------------------------------------
    class RunningAppsForm : Form
    {
        readonly ListView list;
        public readonly List<string> SelectedPaths = new List<string>();

        class Row
        {
            public string Name;
            public string Path;
            public bool HasWindow;
            public long RamBytes;
            public double CpuPct;
            public double DiskKBs;
            public readonly List<Process> Procs = new List<Process>();
            // Snapshot accumulators (first sample).
            public double Cpu0Ms;
            public ulong Io0Bytes;

            public long RamMB { get { return RamBytes / (1024 * 1024); } }
            public double Score { get { return CpuPct * 30.0 + DiskKBs / 50.0 + RamMB / 20.0; } }

            public override string ToString()
            {
                return string.Format("{0}    {1} MB · {2}% CPU · {3} MB/s",
                    Name, RamMB, CpuPct.ToString("0"), (DiskKBs / 1024.0).ToString("0.0"));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // More robust than Process.MainModule.FileName: also resolves the path for
        // UWP/Store, packaged, and cross-bitness processes that MainModule cannot read.
        static string GetProcessPath(Process p)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h != IntPtr.Zero)
            {
                try
                {
                    int cap = 1024;
                    StringBuilder sb = new StringBuilder(cap);
                    if (QueryFullProcessImageName(h, 0, sb, ref cap)) return sb.ToString();
                }
                finally { CloseHandle(h); }
            }
            try { return p.MainModule.FileName; } catch { return null; }
        }

        public RunningAppsForm()
        {
            Text = "Add a running app";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(470, 440);

            Label hint = new Label();
            hint.Text = "Apps using the most CPU, disk and RAM right now. Tick the ones to add.";
            hint.SetBounds(12, 8, 446, 18);

            list = new ListView();
            list.SetBounds(12, 34, 446, 354);
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add("App", 158);
            list.Columns.Add("Process", 95);
            ColumnHeader cRam = list.Columns.Add("RAM", 68); cRam.TextAlign = HorizontalAlignment.Right;
            ColumnHeader cCpu = list.Columns.Add("CPU", 50); cCpu.TextAlign = HorizontalAlignment.Right;
            ColumnHeader cDisk = list.Columns.Add("Disk", 70); cDisk.TextAlign = HorizontalAlignment.Right;

            Button ok = new Button();
            ok.Text = "Add selected"; ok.DialogResult = DialogResult.OK;
            ok.SetBounds(278, 398, 96, 30); ok.Click += OnOk;
            Button cancel = new Button();
            cancel.Text = "Cancel"; cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(382, 398, 76, 30);

            Controls.Add(hint);
            Controls.Add(list);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Populate();
        }

        // Windows shell, OS, security, and Xbox-mode processes that should never be
        // suggested for closing (closing them is pointless or breaks Xbox mode).
        static readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CouchMode",
            // Xbox mode / Game Bar components
            "XboxPcApp", "XboxPcAppFT", "XboxPcTray", "GameBar", "GameBarFTServer",
            "XboxGameBarWidgets", "GamingServices", "GamingServicesNet",
            // Windows shell / core
            "explorer", "dwm", "csrss", "wininit", "winlogon", "services", "lsass",
            "smss", "fontdrvhost", "RuntimeBroker", "sihost", "taskhostw", "ctfmon",
            "conhost", "dllhost", "spoolsv", "SearchHost", "SearchIndexer",
            "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
            "ApplicationFrameHost", "LockApp", "SystemSettings", "audiodg", "WmiPrvSE",
            "svchost", "WUDFHost", "TabTip", "PhoneExperienceHost", "WidgetService",
            "Widgets", "WindowsPackageManagerServer", "GameInputRedistService",
            "smartscreen", "backgroundTaskHost", "UserOOBEBroker",
            // Security
            "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
            // Shared runtimes / helper subprocesses: pointless to close on their own
            // (they belong to a host app and just respawn or break it).
            "msedgewebview2", "QtWebEngineProcess", "crashpad_handler",
            "CefSharp.BrowserSubprocess", "vcpkgsrv", "msvsmon",
        };

        void Populate()
        {
            string winDir = "";
            try { winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows); } catch { }
            int cores = Environment.ProcessorCount; if (cores < 1) cores = 1;

            // Pass 1: group processes by name; record RAM, window, path, and the
            // first CPU-time / disk-bytes sample. Keep the Process objects so we
            // can take a second sample after a short interval.
            Dictionary<string, Row> groups = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return; }
            foreach (Process p in all)
            {
                try
                {
                    string name = p.ProcessName;
                    if (Blocked.Contains(name)) { p.Dispose(); continue; }
                    Row r;
                    if (!groups.TryGetValue(name, out r)) { r = new Row(); r.Name = name; groups[name] = r; }
                    r.Procs.Add(p);
                    try { r.RamBytes += p.WorkingSet64; } catch { }
                    if (p.MainWindowHandle != IntPtr.Zero) r.HasWindow = true;
                    if (r.Path == null) { string pth = GetProcessPath(p); if (pth != null) r.Path = pth; }
                    try { p.Refresh(); r.Cpu0Ms += p.TotalProcessorTime.TotalMilliseconds; } catch { }
                    try { IO_COUNTERS c; if (GetProcessIoCounters(p.Handle, out c)) r.Io0Bytes += c.ReadBytes + c.WriteBytes; } catch { }
                }
                catch { try { p.Dispose(); } catch { } }
            }

            // Let a moment pass so CPU and disk activity can be measured.
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Thread.Sleep(750);
            sw.Stop();
            double intervalMs = sw.Elapsed.TotalMilliseconds; if (intervalMs < 1) intervalMs = 1;

            // Pass 2: second sample, compute CPU% and disk rate per group.
            foreach (Row r in groups.Values)
            {
                double cpu1 = 0; ulong io1 = 0;
                foreach (Process p in r.Procs)
                {
                    try { p.Refresh(); cpu1 += p.TotalProcessorTime.TotalMilliseconds; } catch { }
                    try { IO_COUNTERS c; if (GetProcessIoCounters(p.Handle, out c)) io1 += c.ReadBytes + c.WriteBytes; } catch { }
                }
                double cpu = (cpu1 - r.Cpu0Ms) / (intervalMs * cores) * 100.0;
                r.CpuPct = cpu > 0 ? cpu : 0;
                double bytes = io1 >= r.Io0Bytes ? (io1 - r.Io0Bytes) : 0;
                r.DiskKBs = (bytes / 1024.0) / (intervalMs / 1000.0);
            }

            // Include an app if it has a window, OR is a user app (outside Windows)
            // with notable RAM, OR is actively using CPU or disk right now (the real
            // stutter causes). Then rank by overall gameplay impact.
            List<Row> rows = new List<Row>();
            foreach (Row r in groups.Values)
            {
                if (r.Path == null) { DisposeProcs(r); continue; }
                bool underWindows = winDir.Length > 0 && r.Path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
                bool include = r.HasWindow
                    || (!underWindows && r.RamMB >= 30)
                    || r.CpuPct >= 1.0
                    || r.DiskKBs >= 50;
                if (include) rows.Add(r); else DisposeProcs(r);
            }

            rows.Sort(delegate(Row a, Row b) { return b.Score.CompareTo(a.Score); });
            foreach (Row r in rows)
            {
                string friendly = GetFriendly(r.Path);
                ListViewItem it = new ListViewItem(string.IsNullOrEmpty(friendly) ? r.Name : friendly);
                it.SubItems.Add(r.Name);
                it.SubItems.Add(r.RamMB + " MB");
                it.SubItems.Add(r.CpuPct.ToString("0") + "%");
                it.SubItems.Add((r.DiskKBs / 1024.0).ToString("0.0") + " MB/s");
                it.ToolTipText = r.Path;
                it.Tag = r.Path;
                list.Items.Add(it);
                DisposeProcs(r);
            }
            list.ShowItemToolTips = true;
        }

        // Friendly product name from the file's version info (e.g. "Google Chrome").
        static string GetFriendly(string path)
        {
            try
            {
                System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string d = vi.FileDescription;
                if (!string.IsNullOrEmpty(d) && d.Trim().Length > 0) return d.Trim();
                string pn = vi.ProductName;
                if (!string.IsNullOrEmpty(pn) && pn.Trim().Length > 0) return pn.Trim();
            }
            catch { }
            return null;
        }

        static void DisposeProcs(Row r)
        {
            foreach (Process p in r.Procs) { try { p.Dispose(); } catch { } }
        }

        void OnOk(object sender, EventArgs e)
        {
            foreach (ListViewItem it in list.CheckedItems)
            {
                string p = it.Tag as string;
                if (p != null) SelectedPaths.Add(p);
            }
        }
    }

    // ---------------------------------------------------------------------
    //  About dialog
    // ---------------------------------------------------------------------
    class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "About " + Program.AppName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(380, 180);

            Label name = new Label();
            name.Text = Program.AppName + "  v" + Program.Version;
            name.Font = new Font(name.Font.FontFamily, 11f, FontStyle.Bold);
            name.SetBounds(16, 16, 348, 24);

            Label desc = new Label();
            desc.Text = "Automatically switches the Windows 11 Xbox full screen experience "
                + "on and off based on your controller's power state.";
            desc.SetBounds(16, 46, 348, 44);

            LinkLabel link = new LinkLabel();
            link.Text = "View the project on GitHub";
            link.SetBounds(16, 96, 250, 22);
            link.LinkClicked += OnLink;

            Label lic = new Label();
            lic.Text = "MIT License · Free and open source";
            lic.ForeColor = SystemColors.GrayText;
            lic.SetBounds(16, 122, 348, 22);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(284, 144, 80, 28);

            Controls.Add(name);
            Controls.Add(desc);
            Controls.Add(link);
            Controls.Add(lic);
            Controls.Add(ok);
            AcceptButton = ok;
        }

        void OnLink(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try { Process.Start(Program.RepoUrl); }
            catch { }
        }
    }
}
