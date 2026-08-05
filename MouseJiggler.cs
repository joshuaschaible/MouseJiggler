using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseJigglerApp
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

        // Same string resolves to the same message id in every process, so a second
        // instance can tell the first one to surface itself.
        internal static readonly int WM_SHOW_INSTANCE = RegisterWindowMessage("MouseJigglerShowWindow");

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

            bool createdNew;
            using (var single = new System.Threading.Mutex(true, @"Local\MouseJiggler.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // Already running: hand the window over to the existing copy and bail.
                    // Launching a second instance would otherwise fail to grab the hotkey.
                    if (!silent) PostMessage(HWND_BROADCAST, WM_SHOW_INSTANCE, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                Application.Run(new MainForm(silent));
                GC.KeepAlive(single);
            }
        }
    }

    // ---- Shared palette ----
    static class Theme
    {
        public static readonly Color Bg            = Color.FromArgb(16, 17, 24);
        public static readonly Color TitleDivider  = Color.FromArgb(30, 33, 48);
        public static readonly Color Panel         = Color.FromArgb(29, 31, 42);
        public static readonly Color PanelHover    = Color.FromArgb(38, 41, 55);
        public static readonly Color PanelBorder   = Color.FromArgb(43, 46, 60);
        public static readonly Color TextPrimary   = Color.FromArgb(236, 238, 245);
        public static readonly Color TextSecondary = Color.FromArgb(154, 159, 178);
        public static readonly Color TextDim       = Color.FromArgb(86, 91, 112);
        public static readonly Color Violet        = Color.FromArgb(124, 92, 255);
        public static readonly Color Cyan          = Color.FromArgb(0, 212, 255);
        public static readonly Color RingOff       = Color.FromArgb(58, 62, 80);
        public static readonly Color RingOffHover  = Color.FromArgb(80, 86, 110);

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Rounded on selected sides only (for stepper end-zone hover fills)
        public static GraphicsPath RoundedRectSides(Rectangle r, int radius, bool left, bool right)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (left)  path.AddArc(r.X, r.Y, d, d, 180, 90);
            else       path.AddLine(r.X, r.Y, r.X, r.Y);
            if (right) path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            else       path.AddLine(r.Right, r.Y, r.Right, r.Y);
            if (right) path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            else       path.AddLine(r.Right, r.Bottom, r.Right, r.Bottom);
            if (left)  path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            else       path.AddLine(r.X, r.Bottom, r.X, r.Bottom);
            path.CloseFigure();
            return path;
        }
    }

    // ---- Base for flicker-free custom-drawn controls ----
    class CustomControl : Control
    {
        public CustomControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }
    }

    // ---- Title bar buttons (minimize / close) ----
    class TitleButton : CustomControl
    {
        private readonly bool isClose;
        private bool hover;

        public TitleButton(bool close)
        {
            isClose = close;
            Size = new Size(38, 30);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            if (hover)
            {
                var bg = isClose ? Color.FromArgb(196, 63, 78) : Theme.PanelHover;
                using (var b = new SolidBrush(bg))
                using (var p = Theme.RoundedRect(new Rectangle(3, 3, Width - 7, Height - 7), 6))
                    g.FillPath(b, p);
            }

            var glyph = hover ? Theme.TextPrimary : Theme.TextSecondary;
            using (var pen = new Pen(glyph, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int cx = Width / 2, cy = Height / 2;
                if (isClose)
                {
                    g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                }
                else
                {
                    g.DrawLine(pen, cx - 6, cy, cx + 6, cy);
                }
            }
        }
    }

    // ---- The big glowing power button ----
    class PowerButton : CustomControl
    {
        private bool active;
        private bool hover;
        private bool pressed;
        private float pulse; // 0..1, driven by the form's animation timer

        public bool Active
        {
            get { return active; }
            set { active = value; Invalidate(); }
        }

        public float Pulse
        {
            get { return pulse; }
            set { pulse = value; Invalidate(); }
        }

        public PowerButton()
        {
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true;  Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)   { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            int cx = Width / 2, cy = Height / 2;

            // Soft outer glow while active, breathing with the pulse phase
            if (active)
            {
                int alpha = (int)(45 + 55 * pulse);
                using (var glowPath = new GraphicsPath())
                {
                    glowPath.AddEllipse(2, 2, Width - 4, Height - 4);
                    using (var pgb = new PathGradientBrush(glowPath))
                    {
                        pgb.CenterColor = Color.FromArgb(alpha, Theme.Cyan);
                        pgb.SurroundColors = new Color[] { Color.FromArgb(0, Theme.Cyan) };
                        g.FillPath(pgb, glowPath);
                    }
                }
            }

            int inset = 16 + (pressed ? 2 : 0);
            var ringRect = new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);

            if (active)
            {
                var gradRect = ringRect;
                gradRect.Inflate(3, 3);
                using (var lgb = new LinearGradientBrush(gradRect, Theme.Violet, Theme.Cyan, 45f))
                using (var pen = new Pen(lgb, 4f))
                    g.DrawEllipse(pen, ringRect);
            }
            else
            {
                using (var pen = new Pen(hover ? Theme.RingOffHover : Theme.RingOff, 4f))
                    g.DrawEllipse(pen, ringRect);
            }

            // Power glyph: open arc + stem through the gap
            var glyphColor = active ? Theme.TextPrimary : (hover ? Theme.TextSecondary : Color.FromArgb(122, 127, 150));
            using (var pen = new Pen(glyphColor, 3f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int r = 20;
                var arcRect = new Rectangle(cx - r, cy - r + 3, r * 2, r * 2);
                g.DrawArc(pen, arcRect, 300, 300);
                g.DrawLine(pen, cx, cy - r - 5, cx, cy - 2);
            }
        }
    }

    // ---- Numeric stepper: [−]  value  [+], with hold-to-repeat ----
    class Stepper : CustomControl
    {
        private int val = 1, min = 1, max = 100;
        private int hoverZone;       // 0 none, 1 minus, 2 plus
        private int repeatDir;
        private int repeatCount;
        private readonly Timer repeat;

        public event EventHandler ValueChanged;

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(min, Math.Min(max, value));
                if (v == val) return;
                val = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public int Minimum { get { return min; } set { min = value; Value = val; } }
        public int Maximum { get { return max; } set { max = value; Value = val; } }

        private const int ZoneW = 32;

        public Stepper()
        {
            Size = new Size(120, 30);
            repeat = new Timer { Interval = 400 };
            repeat.Tick += delegate
            {
                repeat.Interval = 65;
                repeatCount++;
                Step(repeatDir);
            };
        }

        private void Step(int dir)
        {
            int step = 1;
            if (max - min >= 100)
            {
                if (repeatCount > 24) step = 10;
                else if (repeatCount > 10) step = 5;
            }
            Value = val + dir * step;
        }

        private int ZoneAt(int x)
        {
            if (x < ZoneW) return 1;
            if (x >= Width - ZoneW) return 2;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int z = ZoneAt(e.X);
            if (z != hoverZone) { hoverZone = z; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverZone = 0;
            StopRepeat();
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int z = ZoneAt(e.X);
            if (z == 0) return;
            repeatDir = (z == 1) ? -1 : 1;
            repeatCount = 0;
            Step(repeatDir);
            repeat.Interval = 400;
            repeat.Start();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            StopRepeat();
            base.OnMouseUp(e);
        }

        private void StopRepeat()
        {
            repeat.Stop();
            repeatCount = 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(body, 8))
            {
                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);

                if (hoverZone == 1)
                    using (var hb = new SolidBrush(Theme.PanelHover))
                    using (var hp = Theme.RoundedRectSides(new Rectangle(0, 0, ZoneW, Height - 1), 8, true, false))
                        g.FillPath(hb, hp);
                if (hoverZone == 2)
                    using (var hb = new SolidBrush(Theme.PanelHover))
                    using (var hp = Theme.RoundedRectSides(new Rectangle(Width - 1 - ZoneW, 0, ZoneW, Height - 1), 8, false, true))
                        g.FillPath(hb, hp);

                using (var pen = new Pen(Theme.PanelBorder, 1f)) g.DrawPath(pen, path);
            }

            int cy = Height / 2;
            using (var pen = new Pen(Theme.TextSecondary, 1.7f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int mc = ZoneW / 2;
                g.DrawLine(pen, mc - 5, cy, mc + 5, cy);
                int pc = Width - ZoneW / 2;
                g.DrawLine(pen, pc - 5, cy, pc + 5, cy);
                g.DrawLine(pen, pc, cy - 5, pc, cy + 5);
            }

            using (var f = new Font("Segoe UI Semibold", 9.5f))
            using (var b = new SolidBrush(Theme.TextPrimary))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(val.ToString(), f, b,
                    new Rectangle(ZoneW, 0, Width - ZoneW * 2, Height), sf);
            }
        }
    }

    // ---- Rounded pill button (hotkey capture) ----
    class PillButton : CustomControl
    {
        private bool hover;

        public PillButton()
        {
            Size = new Size(120, 30);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(body, 8))
            {
                using (var b = new SolidBrush(hover ? Theme.PanelHover : Theme.Panel)) g.FillPath(b, path);
                using (var pen = new Pen(Theme.PanelBorder, 1f)) g.DrawPath(pen, path);
            }

            using (var f = new Font("Segoe UI", 8.75f))
            using (var b = new SolidBrush(hover ? Theme.TextPrimary : Theme.TextSecondary))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(Text, f, b, new Rectangle(4, 0, Width - 8, Height), sf);
            }
        }
    }

    // ---- Animated toggle switch ----
    class ToggleSwitch : CustomControl
    {
        private bool isChecked;
        private float pos;           // 0 = off, 1 = on
        private readonly Timer anim;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                anim.Start();
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public ToggleSwitch()
        {
            Size = new Size(46, 24);
            Cursor = Cursors.Hand;
            anim = new Timer { Interval = 15 };
            anim.Tick += delegate
            {
                float target = isChecked ? 1f : 0f;
                float d = target - pos;
                if (Math.Abs(d) <= 0.16f) { pos = target; anim.Stop(); }
                else pos += Math.Sign(d) * 0.16f;
                Invalidate();
            };
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(body, (Height - 1) / 2))
            {
                // Off-gray track, with the accent gradient fading in as the thumb travels
                using (var off = new SolidBrush(Theme.PanelBorder)) g.FillPath(off, path);
                if (pos > 0.01f)
                {
                    int a = (int)(255 * pos);
                    using (var lgb = new LinearGradientBrush(body,
                        Color.FromArgb(a, Theme.Violet), Color.FromArgb(a, Theme.Cyan), 0f))
                        g.FillPath(lgb, path);
                }
            }

            int thumbD = Height - 7;
            float travel = Width - thumbD - 7;
            float tx = 3 + pos * travel;
            using (var b = new SolidBrush(Color.FromArgb(245, 246, 250)))
                g.FillEllipse(b, tx, 3, thumbD, thumbD);
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

        // ---- Rounded window ----
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);

        // ---- Idle detection ----
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        // ---- Sleep suppression ----
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS       = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED  = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        // ---- Tray icon generation ----
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const int WM_HOTKEY = 0x0312;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
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

        private const int TitleBarHeight = 44;

        // ---- UI ----
        private readonly Timer timer;
        private readonly Timer animTimer;
        private readonly Label status;
        private readonly PowerButton powerButton;
        private readonly Stepper intervalBox;
        private readonly Stepper distanceBox;
        private readonly PillButton hotkeyButton;
        private readonly ToggleSwitch startupCheck;
        private readonly ToggleSwitch topMostCheck;
        private readonly ToggleSwitch idleCheck;
        private readonly ToggleSwitch awakeCheck;
        private readonly NotifyIcon tray;
        private readonly Icon trayIconOn;
        private readonly Icon trayIconOff;
        private bool direction = false;
        private double pulsePhase = 0;

        // ---- Hotkey state ----
        private uint hotkeyMods = MOD_CONTROL | MOD_ALT;
        private Keys hotkeyKey  = Keys.J;
        private bool capturingHotkey = false;
        private bool hotkeyRegistered = false;

        // ---- Startup behavior ----
        private bool startHidden = false;
        private bool autoStartJiggling = false;
        private bool alwaysOnTop = true;
        private bool pauseWhenActive = true;
        private bool keepAwake = true;

        // Tick of our own last injected nudge, so real input can be told apart from ours
        private uint lastNudgeTick;

        // How close to lastNudgeTick a last-input timestamp must be to count as our own
        private const int NudgeGraceMs = 250;

        // ---- Window placement ----
        private Point savedLocation = Point.Empty;
        private bool hasSavedLocation = false;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        public MainForm(bool silent)
        {
            LoadSettings();

            if (silent)
            {
                startHidden = true;
                autoStartJiggling = true;
            }

            Text = "Mouse Jiggler";
            ClientSize = new Size(300, 568);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            TopMost = alwaysOnTop;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18));

            // Restore the last window position, but only if it still lands on a
            // connected monitor (a disconnected second screen would strand it).
            if (hasSavedLocation && IsOnScreen(new Rectangle(savedLocation, Size)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = savedLocation;
            }

            // App icon (taskbar / Alt+Tab)
            var appIcon = LoadAppIcon();
            if (appIcon != null) Icon = appIcon;

            // -- Title bar buttons --
            var minButton = new TitleButton(false) { Location = new Point(Width - 82, 7) };
            minButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(minButton);

            var closeButton = new TitleButton(true) { Location = new Point(Width - 44, 7) };
            closeButton.Click += delegate { Hide(); };
            Controls.Add(closeButton);

            // -- Power button --
            powerButton = new PowerButton
            {
                Size = new Size(136, 136),
                Location = new Point((Width - 136) / 2, 58)
            };
            powerButton.Click += delegate { Toggle(); };
            Controls.Add(powerButton);

            status = new Label
            {
                Text = "IDLE",
                Font = new Font("Segoe UI Semibold", 13f),
                ForeColor = Theme.TextDim,
                BackColor = Theme.Bg,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(Width, 28),
                Location = new Point(0, 198)
            };
            Controls.Add(status);

            // -- Settings rows --
            int rowY = 254;
            int rowH = 40;
            int labelX = 26;
            int controlX = Width - 26 - 120;

            AddRowLabel("Interval (s)", labelX, rowY, rowH);
            intervalBox = new Stepper
            {
                Minimum = 1,
                Maximum = 600,
                Location = new Point(controlX, rowY + (rowH - 30) / 2)
            };
            intervalBox.Value = pendingInterval;
            intervalBox.ValueChanged += delegate
            {
                timer.Interval = intervalBox.Value * 1000;
                SaveSettings();
            };
            Controls.Add(intervalBox);

            rowY += rowH;
            AddRowLabel("Distance (px)", labelX, rowY, rowH);
            distanceBox = new Stepper
            {
                Minimum = 1,
                Maximum = 10000,
                Location = new Point(controlX, rowY + (rowH - 30) / 2)
            };
            distanceBox.Value = pendingDistance;
            distanceBox.ValueChanged += delegate { SaveSettings(); };
            Controls.Add(distanceBox);

            rowY += rowH;
            AddRowLabel("Hotkey", labelX, rowY, rowH);
            hotkeyButton = new PillButton
            {
                Text = FormatHotkey(hotkeyMods, hotkeyKey),
                Location = new Point(controlX, rowY + (rowH - 30) / 2)
            };
            hotkeyButton.Click += delegate { BeginCaptureHotkey(); };
            Controls.Add(hotkeyButton);

            rowY += rowH;
            AddRowLabel("Start with Windows", labelX, rowY, rowH);
            startupCheck = new ToggleSwitch
            {
                Location = new Point(Width - 26 - 46, rowY + (rowH - 24) / 2)
            };
            startupCheck.Checked = IsStartupEnabled();
            startupCheck.CheckedChanged += delegate
            {
                try { SetStartupEnabled(startupCheck.Checked); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not update Windows startup entry:\n" + ex.Message,
                        "Mouse Jiggler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(startupCheck);

            rowY += rowH;
            AddRowLabel("Always on top", labelX, rowY, rowH);
            topMostCheck = new ToggleSwitch
            {
                Location = new Point(Width - 26 - 46, rowY + (rowH - 24) / 2)
            };
            topMostCheck.Checked = alwaysOnTop;
            topMostCheck.CheckedChanged += delegate
            {
                alwaysOnTop = topMostCheck.Checked;
                TopMost = alwaysOnTop;
                SaveSettings();
            };
            Controls.Add(topMostCheck);

            rowY += rowH;
            AddRowLabel("Pause when active", labelX, rowY, rowH);
            idleCheck = new ToggleSwitch
            {
                Location = new Point(Width - 26 - 46, rowY + (rowH - 24) / 2)
            };
            idleCheck.Checked = pauseWhenActive;
            idleCheck.CheckedChanged += delegate
            {
                pauseWhenActive = idleCheck.Checked;
                SaveSettings();
            };
            Controls.Add(idleCheck);

            rowY += rowH;
            AddRowLabel("Keep screen awake", labelX, rowY, rowH);
            awakeCheck = new ToggleSwitch
            {
                Location = new Point(Width - 26 - 46, rowY + (rowH - 24) / 2)
            };
            awakeCheck.Checked = keepAwake;
            awakeCheck.CheckedChanged += delegate
            {
                keepAwake = awakeCheck.Checked;
                ApplyExecutionState();
                SaveSettings();
            };
            Controls.Add(awakeCheck);

            var hint = new Label
            {
                Text = "Close hides to tray  ·  right-click tray icon to quit",
                ForeColor = Theme.TextDim,
                BackColor = Theme.Bg,
                Font = new Font("Segoe UI", 8f),
                Location = new Point(0, Height - 30),
                Size = new Size(Width, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(hint);

            timer = new Timer { Interval = intervalBox.Value * 1000 };
            timer.Tick += delegate { Nudge(); };

            // Breathes the power-button glow while jiggling
            animTimer = new Timer { Interval = 33 };
            animTimer.Tick += delegate
            {
                pulsePhase += 0.07;
                powerButton.Pulse = (float)(0.5 + 0.5 * Math.Sin(pulsePhase));
            };

            var trayMenu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("Show");
            showItem.Click += delegate { ShowFromTray(); };
            var toggleItem = new ToolStripMenuItem("Toggle");
            toggleItem.Click += delegate { Toggle(); };
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { tray.Visible = false; Application.Exit(); };
            trayMenu.Items.Add(showItem);
            trayMenu.Items.Add(toggleItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(exitItem);

            trayIconOn  = MakeTrayIcon(true);
            trayIconOff = MakeTrayIcon(false);

            tray = new NotifyIcon
            {
                Icon = trayIconOff ?? appIcon ?? SystemIcons.Application,
                Text = "Mouse Jiggler (OFF)",
                Visible = true,
                ContextMenuStrip = trayMenu
            };
            tray.DoubleClick += delegate { ShowFromTray(); };

            Load += delegate
            {
                RegisterAppHotkey();
                if (autoStartJiggling) Toggle();
            };
        }

        private void AddRowLabel(string text, int x, int y, int rowH)
        {
            Controls.Add(new Label
            {
                Text = text,
                ForeColor = Theme.TextSecondary,
                BackColor = Theme.Bg,
                Font = new Font("Segoe UI", 9.25f),
                Location = new Point(x, y),
                Size = new Size(130, rowH),
                TextAlign = ContentAlignment.MiddleLeft
            });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Logo dot + title
            var dotRect = new Rectangle(20, 17, 10, 10);
            using (var lgb = new LinearGradientBrush(dotRect, Theme.Violet, Theme.Cyan, 45f))
                g.FillEllipse(lgb, dotRect);

            using (var f = new Font("Segoe UI Semibold", 9.5f))
            using (var b = new SolidBrush(Theme.TextSecondary))
                g.DrawString("Mouse Jiggler", f, b, 38, 13);

            // Title bar divider
            using (var pen = new Pen(Theme.TitleDivider, 1f))
                g.DrawLine(pen, 0, TitleBarHeight, Width, TitleBarHeight);

            // Divider between power section and settings
            using (var pen = new Pen(Theme.TitleDivider, 1f))
                g.DrawLine(pen, 26, 240, Width - 26, 240);
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
            hotkeyButton.Text = "Press keys… (Esc cancels)";
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
                    hotkeyButton.Text = "Need Ctrl/Alt/Shift…";
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

            // A second copy of the app was launched; surface this window instead.
            if (m.Msg == Program.WM_SHOW_INSTANCE && Program.WM_SHOW_INSTANCE != 0)
            {
                ShowFromTray();
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt64() == HTCLIENT)
                {
                    long lp = m.LParam.ToInt64();
                    int x = (short)(lp & 0xFFFF);
                    int y = (short)((lp >> 16) & 0xFFFF);
                    var p = PointToClient(new Point(x, y));
                    if (p.Y < TitleBarHeight) m.Result = (IntPtr)HTCAPTION;
                }
                return;
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
                    object ot = key.GetValue("AlwaysOnTop");
                    object pa = key.GetValue("PauseWhenActive");
                    object ka = key.GetValue("KeepAwake");
                    object wx = key.GetValue("WindowX");
                    object wy = key.GetValue("WindowY");
                    if (iv is int)        { pendingInterval = (int)iv; }
                    if (dv is int)        { pendingDistance = (int)dv; }
                    if (hm is int)        { hotkeyMods = (uint)(int)hm; }
                    if (hk is int)        { hotkeyKey  = (Keys)(int)hk; }
                    if (ot is int)        { alwaysOnTop = (int)ot != 0; }
                    if (pa is int)        { pauseWhenActive = (int)pa != 0; }
                    if (ka is int)        { keepAwake = (int)ka != 0; }
                    if (wx is int && wy is int)
                    {
                        savedLocation = new Point((int)wx, (int)wy);
                        hasSavedLocation = true;
                    }
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
                    key.SetValue("Interval",  intervalBox.Value, RegistryValueKind.DWord);
                    key.SetValue("Distance",  distanceBox.Value, RegistryValueKind.DWord);
                    key.SetValue("HotkeyMods", (int)hotkeyMods,  RegistryValueKind.DWord);
                    key.SetValue("HotkeyKey",  (int)hotkeyKey,   RegistryValueKind.DWord);
                    key.SetValue("AlwaysOnTop", alwaysOnTop ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("PauseWhenActive", pauseWhenActive ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("KeepAwake", keepAwake ? 1 : 0, RegistryValueKind.DWord);
                    if (hasSavedLocation)
                    {
                        key.SetValue("WindowX", savedLocation.X, RegistryValueKind.DWord);
                        key.SetValue("WindowY", savedLocation.Y, RegistryValueKind.DWord);
                    }
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

        // Tray icon drawn to match the power button: filled gradient when jiggling,
        // hollow dim ring when idle — readable at a glance in the notification area.
        private static Icon MakeTrayIcon(bool active)
        {
            try
            {
                using (var bmp = new Bitmap(32, 32))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        var r = new Rectangle(4, 4, 23, 23);
                        if (active)
                        {
                            using (var lgb = new LinearGradientBrush(r, Theme.Violet, Theme.Cyan, 45f))
                                g.FillEllipse(lgb, r);
                        }
                        else
                        {
                            using (var pen = new Pen(Theme.RingOffHover, 3.5f))
                                g.DrawEllipse(pen, r);
                        }
                    }

                    IntPtr h = bmp.GetHicon();
                    try { return (Icon)Icon.FromHandle(h).Clone(); }
                    finally { DestroyIcon(h); }
                }
            }
            catch { return null; }
        }

        private static bool IsOnScreen(Rectangle bounds)
        {
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(bounds)) return true;
            return false;
        }

        private void ApplyExecutionState()
        {
            try
            {
                if (keepAwake && timer.Enabled)
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                else
                    SetThreadExecutionState(ES_CONTINUOUS);
            }
            catch { /* ignore */ }
        }

        // ---- Jiggling ----
        private void Toggle()
        {
            if (timer.Enabled)
            {
                timer.Stop();
                animTimer.Stop();
                powerButton.Active = false;
                powerButton.Pulse = 0;
                status.Text = "IDLE";
                status.ForeColor = Theme.TextDim;
                tray.Text = "Mouse Jiggler (OFF)";
                if (trayIconOff != null) tray.Icon = trayIconOff;
            }
            else
            {
                timer.Interval = intervalBox.Value * 1000;
                timer.Start();
                pulsePhase = 0;
                animTimer.Start();
                powerButton.Active = true;
                status.Text = "ACTIVE";
                status.ForeColor = Theme.Cyan;
                tray.Text = "Mouse Jiggler (ON)";
                if (trayIconOn != null) tray.Icon = trayIconOn;
                lastNudgeTick = GetTickCount();
            }
            ApplyExecutionState();
        }

        private void Nudge()
        {
            if (pauseWhenActive && UserActiveRecently()) return;

            int d = (direction ? 1 : -1) * distanceBox.Value;
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dx = d;
            inputs[0].mi.dy = 0;
            inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            direction = !direction;

            // Recorded after the injection, so our own synthetic input — which also
            // updates the system's last-input time — never counts as user activity.
            lastNudgeTick = GetTickCount();
        }

        private bool UserActiveRecently()
        {
            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii)) return false;

            // Our own injected nudge also bumps the system idle timer, so if the most
            // recent input is ours there has been no real user activity.
            // Unchecked subtraction keeps this correct across the 49.7-day tick wrap.
            unchecked { if ((int)(lii.dwTime - lastNudgeTick) <= NudgeGraceMs) return false; }

            // Real input happened. Stay paused until the user has been quiet for a full
            // interval. Anchoring to lastNudgeTick instead would latch forever: that
            // timestamp stops advancing while paused, so the gap never closes again.
            uint idleMs = unchecked(GetTickCount() - lii.dwTime);
            return idleMs < (uint)Math.Max(1000, intervalBox.Value * 1000);
        }

        // Track the position as it moves, but only hit the registry once the drag ends.
        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (WindowState == FormWindowState.Normal && Visible)
            {
                savedLocation = Location;
                hasSavedLocation = true;
            }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SaveSettings();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SaveSettings();
                Hide();
                return;
            }
            if (hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HOTKEY_ID);
                hotkeyRegistered = false;
            }
            timer.Stop();
            SetThreadExecutionState(ES_CONTINUOUS);
            SaveSettings();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SetThreadExecutionState(ES_CONTINUOUS);
                if (tray != null) tray.Visible = false;
                if (trayIconOn  != null) trayIconOn.Dispose();
                if (trayIconOff != null) trayIconOff.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
