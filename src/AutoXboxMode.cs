// AutoXboxMode - automatically toggles the Windows 11 Xbox full screen
// experience (Xbox mode) based on Xbox controller connection state.
//
// Targets .NET Framework 4.8 (ships with Windows 11). Compile with build.ps1.
// License: MIT.

using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoXboxMode
{
    static class Program
    {
        public const string AppName = "AutoXboxMode";
        public const string Version = "1.0.0";

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "AutoXboxMode_SingleInstance_{8F3A1C20-1E4B-4C2A-9D6E-7A1B2C3D4E5F}", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("AutoXboxMode is already running (check the system tray).",
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
    }

    // ---------------------------------------------------------------------
    //  Settings (simple key=value file in %AppData%\AutoXboxMode\config.ini)
    // ---------------------------------------------------------------------
    class Settings
    {
        public bool EnableOnConnect = true;
        public bool DisableOnDisconnect = true;
        public bool StartWithWindows = false;
        public int PollSeconds = 1;

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
                        else if (k == "PollSeconds") { int p; if (int.TryParse(v, out p) && p >= 1 && p <= 10) s.PollSeconds = p; }
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
                sb.AppendLine("PollSeconds=" + PollSeconds);
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch { }
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        public static void Write(string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Settings.LogPath));
                    string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}",
                        DateTime.Now, msg, Environment.NewLine);
                    // keep the log small
                    if (File.Exists(Settings.LogPath) && new FileInfo(Settings.LogPath).Length > 256 * 1024)
                        File.WriteAllText(Settings.LogPath, "");
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
        readonly System.Windows.Forms.Timer timer;
        readonly Icon iconActive;
        readonly Icon iconIdle;
        readonly ToolStripMenuItem miActive;

        Settings settings;
        int prevCount;
        bool automationOn = true;
        volatile bool busy = false;

        public TrayContext()
        {
            settings = Settings.Load();
            Startup.Apply(settings.StartWithWindows); // keep registry in sync

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
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

            tray = new NotifyIcon();
            tray.Icon = iconIdle;
            tray.Text = Program.AppName;
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += OnSettings;

            prevCount = Native.ControllerCount();
            Log.Write(string.Format("Started. Controllers={0}, XboxMode={1}",
                prevCount, Native.IsXboxModeOn() ? "ON" : "OFF"));
            UpdateIcon();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = settings.PollSeconds * 1000;
            timer.Tick += OnTick;
            timer.Start();
        }

        void OnTick(object sender, EventArgs e)
        {
            if (!automationOn || busy) return;

            int count;
            try { count = Native.ControllerCount(); }
            catch { return; }

            if (count > 0 && prevCount == 0 && settings.EnableOnConnect)
            {
                Log.Write("Controller connected -> entering Xbox mode.");
                RunSetMode(true);
            }
            else if (count == 0 && prevCount > 0 && settings.DisableOnDisconnect)
            {
                Log.Write("Controller disconnected -> exiting Xbox mode.");
                RunSetMode(false);
            }

            prevCount = count;
            UpdateIcon();
        }

        // Toggle work runs on a background thread so the UI/tray never freezes.
        void RunSetMode(bool want)
        {
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { SetMode(want); }
                finally { busy = false; }
            });
        }

        static void SetMode(bool want)
        {
            if (Native.IsXboxModeOn() == want) return;

            Native.SendWinF11();
            if (WaitFor(want, 4000)) { Log.Write(want ? "Xbox mode ON." : "Xbox mode OFF."); return; }

            // Wanted ON but nothing happened: the Xbox app may be closed. Launch and retry.
            if (want)
            {
                try
                {
                    Log.Write("First attempt failed, launching Xbox app and retrying...");
                    Process.Start("xbox:");
                }
                catch { }
                Thread.Sleep(4000);
                Native.SendWinF11();
                if (WaitFor(true, 4000)) { Log.Write("Xbox mode ON."); return; }
            }
            Log.Write("WARNING: could not reach requested Xbox mode state.");
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
            tray.Text = string.Format("{0} – {1}{2}",
                Program.AppName,
                automationOn ? "Active" : "Paused",
                on ? " (Xbox mode ON)" : "");
        }

        void OnToggleActive(object sender, EventArgs e)
        {
            automationOn = !automationOn;
            miActive.Checked = automationOn;
            Log.Write("Automation " + (automationOn ? "resumed." : "paused."));
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
                    Startup.Apply(settings.StartWithWindows);
                    timer.Interval = settings.PollSeconds * 1000;
                    Log.Write("Settings saved.");
                }
            }
        }

        void OnOpenLog(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Settings.LogPath));
                if (!File.Exists(Settings.LogPath)) File.WriteAllText(Settings.LogPath, "");
                Process.Start(Settings.LogPath);
            }
            catch { }
        }

        void OnExit(object sender, EventArgs e)
        {
            timer.Stop();
            tray.Visible = false;
            Log.Write("Exit.");
            ExitThread();
        }

        static Icon MakeIcon(bool active)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Color c = active ? Color.FromArgb(16, 124, 16) : Color.FromArgb(110, 110, 110);
                using (SolidBrush b = new SolidBrush(c))
                    g.FillEllipse(b, 1, 1, 30, 30);
                using (Pen p = new Pen(Color.White, 3.2f))
                {
                    g.DrawLine(p, 11, 11, 21, 21);
                    g.DrawLine(p, 21, 11, 11, 21);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
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
        readonly NumericUpDown numPoll;
        public Settings Result;

        public SettingsForm(Settings current)
        {
            Result = current;

            Text = Program.AppName + " Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(360, 200);

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

            Label lbl = new Label();
            lbl.Text = "Check interval (seconds):";
            lbl.SetBounds(16, 104, 160, 22);

            numPoll = new NumericUpDown();
            numPoll.Minimum = 1;
            numPoll.Maximum = 10;
            numPoll.Value = current.PollSeconds;
            numPoll.SetBounds(180, 102, 50, 22);

            Button ok = new Button();
            ok.Text = "Save";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(176, 152, 80, 28);
            ok.Click += OnSave;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(264, 152, 80, 28);

            Controls.Add(cbConnect);
            Controls.Add(cbDisconnect);
            Controls.Add(cbStartup);
            Controls.Add(lbl);
            Controls.Add(numPoll);
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
            s.PollSeconds = (int)numPoll.Value;
            Result = s;
        }
    }
}
