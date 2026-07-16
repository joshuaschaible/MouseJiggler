using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseJigglerApp
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false;
            foreach (var a in args)
            {
                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/silent",  StringComparison.OrdinalIgnoreCase))
                    silent = true;
            }

            Application.Run(new MainForm(silent));
        }
    }

    public class MainForm : Form
    {
        // ---- SendInput ----
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
        }

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // ---- Global hotkey ----
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 0xA731;

        private const uint MOD_ALT     = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT   = 0x0004;
        private const uint MOD_WIN     = 0x0008;

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        // ---- Settings storage ----
        private const string RegRoot   = @"Software\MouseJiggler";
        private const string RunKey    = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue  = "MouseJiggler";

        // ---- UI ----
        private readonly Timer timer;
        private readonly Label status;
        private readonly Button toggleButton;
        private readonly NumericUpDown intervalBox;
        private readonly NumericUpDown distanceBox;
        private readonly Button hotkeyButton;
        private readonly CheckBox startupCheck;
        private readonly NotifyIcon tray;
        private bool direction = false;

        // ---- Hotkey state ----
        private uint hotkeyMods = MOD_CONTROL | MOD_ALT;
        private Keys hotkeyKey  = Keys.J;
        private bool capturingHotkey = false;
        private bool hotkeyRegistered = false;

        // ---- Startup behavior ----
        private bool startHidden = false;
        private bool autoStartJiggling = false;

        public MainForm(bool silent)
        {
            LoadSettings();

            if (silent)
            {
                startHidden = true;
                autoStartJiggling = true;
            }

            Text = "Mouse Jiggler";
            ClientSize = new Size(280, 330);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            TopMost = true;
            BackColor = Color.FromArgb(30, 30, 30);

            // App icon (form titlebar / taskbar / Alt+Tab)
            var appIcon = LoadAppIcon();
            if (appIcon != null) Icon = appIcon;

            status = new Label
            {
                Text = "OFF",
                Font = new Font("Segoe UI", 28, FontStyle.Bold),
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(260, 60),
                Location = new Point(10, 10)
            };
            Controls.Add(status);

            toggleButton = new Button
            {
                Text = "Start",
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                Size = new Size(220, 55),
                Location = new Point(30, 80),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White
            };
            toggleButton.FlatAppearance.BorderSize = 0;
            toggleButton.Click += (s, e) => Toggle();
            Controls.Add(toggleButton);

            var intervalLabel = new Label
            {
                Text = "Interval (sec):",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9),
                Location = new Point(30, 150),
                Size = new Size(100, 20)
            };
            Controls.Add(intervalLabel);

            intervalBox = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 600,
                Value = 30,
                Location = new Point(135, 148),
                Size = new Size(115, 22),
                Font = new Font("Segoe UI", 10)
            };
            intervalBox.ValueChanged += (s, e) =>
            {
                timer.Interval = (int)intervalBox.Value * 1000;
                SaveSettings();
            };
            Controls.Add(intervalBox);

            var distanceLabel = new Label
            {
                Text = "Distance (px):",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9),
                Location = new Point(30, 180),
                Size = new Size(100, 20)
            };
            Controls.Add(distanceLabel);

            distanceBox = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 10000,
                Value = 1,
                Location = new Point(135, 178),
                Size = new Size(115, 22),
                Font = new Font("Segoe UI", 10)
            };
            distanceBox.ValueChanged += (s, e) => SaveSettings();
            Controls.Add(distanceBox);

            var hotkeyLabel = new Label
            {
                Text = "Hotkey:",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9),
                Location = new Point(30, 210),
                Size = new Size(100, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(hotkeyLabel);

            hotkeyButton = new Button
            {
                Text = FormatHotkey(hotkeyMods, hotkeyKey),
                Location = new Point(135, 207),
                Size = new Size(115, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            hotkeyButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            hotkeyButton.Click += (s, e) => BeginCaptureHotkey();
            Controls.Add(hotkeyButton);

            startupCheck = new CheckBox
            {
                Text = "Start with Windows",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9),
                Location = new Point(30, 245),
                Size = new Size(220, 22),
                Checked = IsStartupEnabled()
            };
            startupCheck.CheckedChanged += (s, e) =>
            {
                try { SetStartupEnabled(startupCheck.Checked); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not update Windows startup entry:\n" + ex.Message,
                        "Mouse Jiggler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(startupCheck);

            var hint = new Label
            {
                Text = "Close hides to tray. Right-click tray to exit.",
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8),
                Location = new Point(10, 285),
                Size = new Size(260, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(hint);

            // Apply loaded settings to controls (clamped)
            try { intervalBox.Value = Math.Min(intervalBox.Maximum, Math.Max(intervalBox.Minimum, intervalBox.Value)); } catch { }
            try { distanceBox.Value = Math.Min(distanceBox.Maximum, Math.Max(distanceBox.Minimum, distanceBox.Value)); } catch { }

            timer = new Timer { Interval = (int)intervalBox.Value * 1000 };
            timer.Tick += (s, e) => Nudge();

            var trayMenu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("Show");
            showItem.Click += (s, e) => ShowFromTray();
            var toggleItem = new ToolStripMenuItem("Toggle");
            toggleItem.Click += (s, e) => Toggle();
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => { tray.Visible = false; Application.Exit(); };
            trayMenu.Items.Add(showItem);
            trayMenu.Items.Add(toggleItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(exitItem);

            tray = new NotifyIcon
            {
                Icon = appIcon ?? SystemIcons.Application,
                Text = "Mouse Jiggler (OFF)",
                Visible = true,
                ContextMenuStrip = trayMenu
            };
            tray.DoubleClick += (s, e) => ShowFromTray();

            Load += (s, e) =>
            {
                RegisterAppHotkey();
                if (autoStartJiggling) Toggle();
            };
        }

        // ---- Start-hidden plumbing ----
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                if (!IsHandleCreated) CreateHandle();
                startHidden = false;
                value = false;
            }
            base.SetVisibleCore(value);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // ---- Hotkey ----
        private void RegisterAppHotkey()
        {
            if (hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HOTKEY_ID);
                hotkeyRegistered = false;
            }
            if (hotkeyKey == Keys.None) return;

            bool ok = RegisterHotKey(Handle, HOTKEY_ID, hotkeyMods, (uint)hotkeyKey);
            hotkeyRegistered = ok;
            if (!ok)
            {
                hotkeyButton.Text = FormatHotkey(hotkeyMods, hotkeyKey) + " (in use)";
            }
            else
            {
                hotkeyButton.Text = FormatHotkey(hotkeyMods, hotkeyKey);
            }
        }

        private void BeginCaptureHotkey()
        {
            capturingHotkey = true;
            hotkeyButton.Text = "Press keys… (Esc to cancel)";
            // Pull focus off the button so it doesn't eat Space/Enter
            ActiveControl = null;
            Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (capturingHotkey)
            {
                Keys k    = keyData & Keys.KeyCode;
                Keys mods = keyData & Keys.Modifiers;

                // Ignore pure modifier presses
                if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu ||
                    k == Keys.LWin || k == Keys.RWin || k == Keys.None)
                    return base.ProcessCmdKey(ref msg, keyData);

                if (k == Keys.Escape)
                {
                    capturingHotkey = false;
                    hotkeyButton.Text = FormatHotkey(hotkeyMods, hotkeyKey);
                    return true;
                }

                uint m = 0;
                if ((mods & Keys.Control) != 0) m |= MOD_CONTROL;
                if ((mods & Keys.Alt)     != 0) m |= MOD_ALT;
                if ((mods & Keys.Shift)   != 0) m |= MOD_SHIFT;

                if (m == 0)
                {
                    // Require at least one modifier to avoid hijacking plain keys
                    hotkeyButton.Text = "Need a modifier (Ctrl/Alt/Shift)…";
                    return true;
                }

                hotkeyMods = m;
                hotkeyKey  = k;
                capturingHotkey = false;
                SaveSettings();
                RegisterAppHotkey();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                Toggle();
            }
            base.WndProc(ref m);
        }

        private static string FormatHotkey(uint mods, Keys key)
        {
            if (key == Keys.None) return "(none)";
            var parts = new System.Collections.Generic.List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT)     != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT)   != 0) parts.Add("Shift");
            if ((mods & MOD_WIN)     != 0) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join("+", parts.ToArray());
        }

        private static string KeyName(Keys k)
        {
            // KeyConverter prints "Oemcomma" etc; clean up the common ones
            switch (k)
            {
                case Keys.Oemcomma:    return ",";
                case Keys.OemPeriod:   return ".";
                case Keys.OemQuestion: return "/";
                case Keys.OemSemicolon:return ";";
                case Keys.OemPipe:     return "\\";
                case Keys.Oemtilde:    return "`";
                case Keys.OemMinus:    return "-";
                case Keys.Oemplus:     return "=";
                default: return k.ToString();
            }
        }

        // ---- Settings ----
        private void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegRoot))
                {
                    if (key == null) return;
                    object iv = key.GetValue("Interval");
                    object dv = key.GetValue("Distance");
                    object hm = key.GetValue("HotkeyMods");
                    object hk = key.GetValue("HotkeyKey");
                    if (iv is int)        { /* applied after intervalBox created */ pendingInterval = (int)iv; }
                    if (dv is int)        { pendingDistance = (int)dv; }
                    if (hm is int)        { hotkeyMods = (uint)(int)hm; }
                    if (hk is int)        { hotkeyKey  = (Keys)(int)hk; }
                }
            }
            catch { /* ignore */ }
        }

        // Pending values from registry, applied once UI is built
        private int pendingInterval = 30;
        private int pendingDistance = 1;

        private void SaveSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegRoot))
                {
                    key.SetValue("Interval",  (int)intervalBox.Value, RegistryValueKind.DWord);
                    key.SetValue("Distance",  (int)distanceBox.Value, RegistryValueKind.DWord);
                    key.SetValue("HotkeyMods", (int)hotkeyMods,        RegistryValueKind.DWord);
                    key.SetValue("HotkeyKey",  (int)hotkeyKey,         RegistryValueKind.DWord);
                }
            }
            catch { /* ignore */ }
        }

        // ---- Startup ----
        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key == null) return false;
                    var v = key.GetValue(RunValue) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        private static void SetStartupEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) return;
                if (enabled)
                {
                    string cmd = "\"" + Application.ExecutablePath + "\" --silent";
                    key.SetValue(RunValue, cmd, RegistryValueKind.String);
                }
                else
                {
                    if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue, false);
                }
            }
        }

        // ---- Icon ----
        private static Icon LoadAppIcon()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";
                string icoPath = Path.Combine(exeDir, "MouseJiggler.ico");
                if (File.Exists(icoPath)) return new Icon(icoPath);
            }
            catch { /* fall through */ }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        // ---- Jiggling ----
        private void Toggle()
        {
            if (timer.Enabled)
            {
                timer.Stop();
                status.Text = "OFF";
                status.ForeColor = Color.Gray;
                toggleButton.Text = "Start";
                toggleButton.BackColor = Color.FromArgb(0, 120, 215);
                tray.Text = "Mouse Jiggler (OFF)";
            }
            else
            {
                timer.Interval = (int)intervalBox.Value * 1000;
                timer.Start();
                status.Text = "ON";
                status.ForeColor = Color.FromArgb(0, 200, 100);
                toggleButton.Text = "Stop";
                toggleButton.BackColor = Color.FromArgb(200, 50, 50);
                tray.Text = "Mouse Jiggler (ON)";
            }
        }

        private void Nudge()
        {
            int d = (direction ? 1 : -1) * (int)distanceBox.Value;
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dx = d;
            inputs[0].mi.dy = 0;
            inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            direction = !direction;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Apply persisted numeric values now that the controls exist
            try
            {
                intervalBox.Value = Math.Min(intervalBox.Maximum, Math.Max(intervalBox.Minimum, pendingInterval));
                distanceBox.Value = Math.Min(distanceBox.Maximum, Math.Max(distanceBox.Minimum, pendingDistance));
                timer.Interval = (int)intervalBox.Value * 1000;
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            if (hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HOTKEY_ID);
                hotkeyRegistered = false;
            }
            timer.Stop();
            tray.Visible = false;
            base.OnFormClosing(e);
        }
    }
}
