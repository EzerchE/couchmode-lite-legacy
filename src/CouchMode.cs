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
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("CouchMode")]
[assembly: AssemblyProduct("CouchMode")]
[assembly: AssemblyDescription("Automatically switches the Windows 11 Xbox full screen experience based on your controller.")]
[assembly: AssemblyCompany("EzerchE")]
[assembly: AssemblyCopyright("Copyright (c) 2026 EzerchE. MIT License.")]
[assembly: AssemblyVersion("1.3.6.0")]
[assembly: AssemblyFileVersion("1.3.6.0")]
[assembly: AssemblyInformationalVersion("1.3.6-beta")]

namespace CouchMode
{
    static class Program
    {
        public const string AppName = "CouchMode";
        public const string Version = "1.3.6-beta";
        public const string RepoUrl = "https://github.com/EzerchE/CouchMode";

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
                    }
                }
            }
            catch { }
            return s;
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
    //  Tray application
    // ---------------------------------------------------------------------
    class TrayContext : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly DeviceWatcher watcher;
        readonly System.Windows.Forms.Timer debounce;
        readonly Control sink; // marshals work back onto the UI thread
        readonly Icon iconActive;
        readonly Icon iconIdle;
        readonly ToolStripMenuItem miActive;

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

            baseline = prevCount = Native.ControllerCount();
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
                Log.Write(string.Format("Controller connected ({0} -> {1}, baseline {2}) -> entering Xbox mode.", prevCount, count, baseline));
                RunSetMode(true);
            }
            else if (!isAbove && wasAbove && settings.DisableOnDisconnect)
            {
                Log.Write(string.Format("Controller disconnected ({0} -> {1}, baseline {2}) -> exiting Xbox mode.", prevCount, count, baseline));
                RunSetMode(false);
            }

            prevCount = count;
            UpdateIcon();
        }

        // Toggle work runs on a background thread so the UI/tray never freezes.
        // When it finishes, refresh the tray icon on the UI thread so it reflects
        // the final Xbox mode state (the switch takes ~1s to settle).
        void RunSetMode(bool want)
        {
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
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
            if (WaitFor(want, timeout)) { Log.Write(want ? "Xbox mode ON." : "Xbox mode OFF."); return; }

            Log.Write(want
                ? "Xbox mode did not turn on (prompt dismissed or still open)."
                : "Xbox mode did not turn off.");
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
            tray.Icon = (automationOn && on) ? iconActive : iconIdle;
            tray.Text = string.Format("{0}: {1}{2}",
                Program.AppName,
                automationOn ? "Active" : "Paused",
                on ? " (Xbox mode ON)" : "");
        }

        void OnToggleActive(object sender, EventArgs e)
        {
            automationOn = !automationOn;
            miActive.Checked = automationOn;
            Log.Debug("Automation " + (automationOn ? "resumed." : "paused."));
            if (automationOn) prevCount = Native.ControllerCount();
            UpdateIcon();
        }

        void OnSettings(object sender, EventArgs e)
        {
            using (SettingsForm f = new SettingsForm(settings))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    settings = f.Result;
                    settings.Save();
                    Log.Verbose = settings.DebugLogging;
                    Startup.Apply(settings.StartWithWindows);
                    Log.Debug("Settings saved.");
                }
            }
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
            if (watcher != null) watcher.Dispose();
            if (sink != null) sink.Dispose();
            tray.Visible = false;
            Log.Write("Exit.");
            ExitThread();
        }

        // Draws the same minimal gamepad as the exe icon (see generate-assets.ps1).
        // Green when active, grey when paused; white details.
        static Icon MakeIcon(bool active)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color fill = active ? Color.FromArgb(16, 124, 16) : Color.FromArgb(110, 110, 110);
                using (SolidBrush bFill = new SolidBrush(fill))
                using (SolidBrush bDetail = new SolidBrush(Color.White))
                {
                    g.FillEllipse(bFill, 2f, 12f, 13f, 13f);   // left grip
                    g.FillEllipse(bFill, 17f, 12f, 13f, 13f);  // right grip
                    using (System.Drawing.Drawing2D.GraphicsPath body = RoundedRect(4f, 7f, 24f, 13f, 6f))
                        g.FillPath(bFill, body);

                    g.FillRectangle(bDetail, 8.6f, 12f, 1.8f, 7f);   // dpad vertical
                    g.FillRectangle(bDetail, 6f, 14.6f, 7f, 1.8f);   // dpad horizontal
                    g.FillEllipse(bDetail, 20f, 12f, 3f, 3f);        // button A
                    g.FillEllipse(bDetail, 23.3f, 15.3f, 3f, 3f);    // button B
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            float d = r * 2;
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
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
    class SettingsForm : Form
    {
        readonly CheckBox cbConnect;
        readonly CheckBox cbDisconnect;
        readonly CheckBox cbStartup;
        readonly CheckBox cbDebug;
        public Settings Result;

        public SettingsForm(Settings current)
        {
            Result = current;

            Text = Program.AppName + " Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(360, 196);

            cbConnect = new CheckBox();
            cbConnect.Text = "Enter Xbox mode when a controller connects";
            cbConnect.Checked = current.EnableOnConnect;
            cbConnect.SetBounds(16, 16, 330, 22);

            cbDisconnect = new CheckBox();
            cbDisconnect.Text = "Exit Xbox mode when all controllers disconnect";
            cbDisconnect.Checked = current.DisableOnDisconnect;
            cbDisconnect.SetBounds(16, 44, 330, 22);

            cbStartup = new CheckBox();
            cbStartup.Text = "Start automatically with Windows";
            cbStartup.Checked = current.StartWithWindows;
            cbStartup.SetBounds(16, 72, 330, 22);

            cbDebug = new CheckBox();
            cbDebug.Text = "Debug logging (verbose activity log)";
            cbDebug.Checked = current.DebugLogging;
            cbDebug.SetBounds(16, 100, 330, 22);

            Button ok = new Button();
            ok.Text = "Save";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(176, 148, 80, 28);
            ok.Click += OnSave;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(264, 148, 80, 28);

            Controls.Add(cbConnect);
            Controls.Add(cbDisconnect);
            Controls.Add(cbStartup);
            Controls.Add(cbDebug);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        void OnSave(object sender, EventArgs e)
        {
            Settings s = new Settings();
            s.EnableOnConnect = cbConnect.Checked;
            s.DisableOnDisconnect = cbDisconnect.Checked;
            s.StartWithWindows = cbStartup.Checked;
            s.DebugLogging = cbDebug.Checked;
            Result = s;
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
