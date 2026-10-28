using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseJigglerApp
{
    static class Program
    {
        internal static bool StreamDeckEnabled;
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
            bool background = false;
            foreach (var a in args)
            {
                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/silent",  StringComparison.OrdinalIgnoreCase))
                    silent = true;
                if (a.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/background", StringComparison.OrdinalIgnoreCase))
                    background = true;
            }

            bool createdNew;
            using (var single = new System.Threading.Mutex(true, @"Local\MouseJiggler.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // Already running: hand the window over to the existing copy and bail.
                    // Launching a second instance would otherwise fail to grab the hotkey.
                    if (!silent && !background) PostMessage(HWND_BROADCAST, WM_SHOW_INSTANCE, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                StreamDeckEnabled = true;
                Application.Run(new MainForm(silent, background));
                GC.KeepAlive(single);
            }
        }
    }

    // Local, same-user/session IPC. The worker never calls a UI control directly;
    // every state read/change is dispatched to the form's current UI handle.
    internal sealed class StreamDeckServer : IDisposable
    {
        internal const int MaxRequestBytes = 256;
        internal const int RequestTimeoutMilliseconds = 2000;
        internal const int UiTimeoutMilliseconds = 2000;
        internal const int WriteTimeoutMilliseconds = 2000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint sessionId);

        private readonly Control owner;
        private readonly Func<string, string> handleCommand;
        private readonly string pipeName;
        private readonly int sessionId;
        private readonly System.Security.Principal.SecurityIdentifier userSid;
        private readonly object gate = new object();
        private readonly System.Threading.ManualResetEvent stopped = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.Thread worker;
        private NamedPipeServerStream currentPipe;
        private volatile bool stopping;

        private sealed class UiRequest
        {
            internal readonly object Gate = new object();
            internal readonly System.Threading.ManualResetEvent Done = new System.Threading.ManualResetEvent(false);
            internal bool Cancelled;
            internal string Response;
        }

        internal StreamDeckServer(Control owner, Func<string, string> handleCommand,
            string pipeName, string descriptorPath)
        {
            this.owner = owner;
            this.handleCommand = handleCommand;
            this.pipeName = pipeName;
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                userSid = identity.User;
            if (userSid == null) throw new InvalidOperationException("Current user identity is unavailable.");
            int processId;
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                processId = process.Id;
                sessionId = process.SessionId;
            }
            try
            {
                currentPipe = CreatePipe();
                WriteDescriptor(descriptorPath, pipeName, processId);
                worker = new System.Threading.Thread(Run);
                worker.IsBackground = true;
                worker.Name = "Mouse Jiggler Stream Deck";
                worker.Start();
            }
            catch
            {
                if (currentPipe != null) currentPipe.Dispose();
                stopped.Dispose();
                throw;
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            var security = new System.IO.Pipes.PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(userSid);
            security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(userSid,
                System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, MaxRequestBytes, 512, security);
        }

        private void Run()
        {
            try
            {
                while (!stopping)
                {
                    NamedPipeServerStream pipe = null;
                    bool failed = false;
                    try
                    {
                        lock (gate)
                        {
                            if (stopping) break;
                            if (currentPipe == null) currentPipe = CreatePipe();
                            pipe = currentPipe;
                        }
                        IAsyncResult connection = pipe.BeginWaitForConnection(null, null);
                        using (connection.AsyncWaitHandle)
                        {
                            int ready = System.Threading.WaitHandle.WaitAny(new[] { connection.AsyncWaitHandle, stopped });
                            if (ready != 0) CancelPending(pipe);
                            pipe.EndWaitForConnection(connection);
                            if (ready != 0 || stopping) continue;
                        }
                        uint clientSession;
                        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out clientSession) || clientSession != (uint)sessionId)
                        {
                            WriteResponse(pipe, Error("Client session is not permitted."));
                            continue;
                        }
                        HandleConnection(pipe);
                    }
                    catch (Exception) { failed = true; }
                    finally
                    {
                        lock (gate)
                            if (Object.ReferenceEquals(currentPipe, pipe)) currentPipe = null;
                        if (pipe != null) pipe.Dispose();
                    }
                    // A denied/broken client cannot terminate the server. Avoid a
                    // busy retry loop if the endpoint itself becomes unavailable.
                    if (failed && !stopping) stopped.WaitOne(100);
                }
            }
            finally { stopped.Dispose(); }
        }

        private void HandleConnection(NamedPipeServerStream pipe)
        {
            try
            {
                string command = ReadRequest(pipe);
                string response = Dispatch(command);
                if (!stopping) WriteResponse(pipe, response);
            }
            catch (TimeoutException) { TryWriteError(pipe, "Request timed out."); }
            catch (InvalidDataException) { TryWriteError(pipe, "Request is too long or incomplete."); }
            catch (DecoderFallbackException) { TryWriteError(pipe, "Request must be valid UTF-8."); }
        }

        private string ReadRequest(NamedPipeServerStream pipe)
        {
            var buffer = new byte[MaxRequestBytes];
            int used = 0;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (used < buffer.Length)
            {
                int remaining = RequestTimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException();
                IAsyncResult read = pipe.BeginRead(buffer, used, buffer.Length - used, null, null);
                int count;
                using (read.AsyncWaitHandle)
                {
                    int ready = System.Threading.WaitHandle.WaitAny(new[] { read.AsyncWaitHandle, stopped }, remaining);
                    if (ready != 0)
                    {
                        CancelPending(pipe);
                        try { pipe.EndRead(read); } catch (IOException) { } catch (ObjectDisposedException) { }
                        throw new TimeoutException();
                    }
                    count = pipe.EndRead(read);
                }
                if (count == 0) throw new InvalidDataException();
                int end = Array.IndexOf(buffer, (byte)'\n', used, count);
                used += count;
                if (end >= 0)
                    return new UTF8Encoding(false, true).GetString(buffer, 0, end).Trim().ToLowerInvariant();
            }
            throw new InvalidDataException();
        }

        private string Dispatch(string command)
        {
            var request = new UiRequest();
            try
            {
                try
                {
                    owner.BeginInvoke(new MethodInvoker(delegate
                    {
                        lock (request.Gate)
                        {
                            if (request.Cancelled) return;
                            request.Response = stopping ? Error("App is shutting down.") : InvokeCommand(command);
                            request.Done.Set();
                        }
                    }));
                }
                catch (InvalidOperationException) { return Error("App is temporarily unavailable."); }
                int ready = System.Threading.WaitHandle.WaitAny(new[] { request.Done, stopped }, UiTimeoutMilliseconds);
                lock (request.Gate)
                {
                    if (ready == 0) return request.Response;
                    request.Cancelled = true;
                    return Error(ready == 1 ? "App is shutting down." : "UI request timed out.");
                }
            }
            finally
            {
                // A timed-out callback is cancelled before closing its event, so
                // it cannot mutate state or touch a disposed wait handle later.
                lock (request.Gate)
                {
                    request.Cancelled = true;
                    request.Done.Dispose();
                }
            }
        }

        private string InvokeCommand(string command)
        {
            try { return handleCommand(command); }
            catch (Exception) { return Error("Command could not be completed."); }
        }

        private void WriteResponse(NamedPipeServerStream pipe, string response)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(response + "\n");
            IAsyncResult write = pipe.BeginWrite(bytes, 0, bytes.Length, null, null);
            using (write.AsyncWaitHandle)
            {
                int ready = System.Threading.WaitHandle.WaitAny(new[] { write.AsyncWaitHandle, stopped }, WriteTimeoutMilliseconds);
                if (ready != 0)
                {
                    CancelPending(pipe);
                    try { pipe.EndWrite(write); } catch (IOException) { } catch (ObjectDisposedException) { }
                    throw new TimeoutException();
                }
                pipe.EndWrite(write);
            }
        }

        private void TryWriteError(NamedPipeServerStream pipe, string message)
        {
            if (stopping) return;
            try { WriteResponse(pipe, Error(message)); } catch (Exception) { }
        }

        private static void CancelPending(NamedPipeServerStream pipe)
        {
            try { CancelIoEx(pipe.SafePipeHandle, IntPtr.Zero); }
            catch (ObjectDisposedException) { }
        }

        private static void WriteDescriptor(string path, string pipeName, int processId)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "streamdeck." + processId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string json = "{\"protocolVersion\":1,\"pipeName\":\"" + Escape(pipeName) +
                "\",\"executablePath\":\"" + Escape(Application.ExecutablePath) + "\",\"processId\":" + processId + "}";
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string Error(string message)
        {
            return "{\"ok\":false,\"error\":\"" + Escape(message) + "\"}";
        }

        private static string Escape(string value)
        {
            var text = new StringBuilder();
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': text.Append("\\\\"); break;
                    case '"': text.Append("\\\""); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < 32) text.Append("\\u" + ((int)c).ToString("x4"));
                        else text.Append(c);
                        break;
                }
            }
            return text.ToString();
        }

        public void Dispose()
        {
            NamedPipeServerStream pipe;
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                stopped.Set();
                pipe = currentPipe;
            }
            if (pipe != null)
            {
                CancelPending(pipe);
                pipe.Dispose();
            }
            // Keep the descriptor as a launch hint. It is not a running-state
            // claim: clients must probe the pipe, and a new process replaces it.
        }
    }

    static class AlphaSurface
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc,
            ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y,
            int width, int height, IntPtr src, int sx, int sy, uint rop);

        internal static Bitmap Render(Size size, Action<Graphics> paint)
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    paint(graphics);
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        internal static void Paint(Graphics destination, Size size, Action<Graphics> paint)
        {
            if (size.Width <= 0 || size.Height <= 0) return;
            using (var bitmap = Render(size, paint))
            {
                IntPtr target = destination.GetHdc();
                IntPtr memory = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    memory = CreateCompatibleDC(target);
                    var info = new BITMAPINFOHEADER {
                        Size = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                        Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32
                    };
                    IntPtr bits;
                    dib = CreateDIBSection(target, ref info, 0, out bits, IntPtr.Zero, 0);
                    if (memory == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero)
                        throw new System.ComponentModel.Win32Exception();
                    var data = bitmap.LockBits(new Rectangle(Point.Empty, size),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        var bytes = new byte[size.Width * size.Height * 4];
                        for (int row = 0; row < size.Height; row++)
                            Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), bytes,
                                row * size.Width * 4, size.Width * 4);
                        Marshal.Copy(bytes, 0, bits, bytes.Length);
                    }
                    finally { bitmap.UnlockBits(data); }
                    old = SelectObject(memory, dib);
                    if (old == IntPtr.Zero || old == new IntPtr(-1))
                        throw new System.ComponentModel.Win32Exception();
                    if (!BitBlt(target, 0, 0, size.Width, size.Height, memory, 0, 0, 0x00CC0020))
                        throw new System.ComponentModel.Win32Exception();
                }
                finally
                {
                    if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(memory, old);
                    if (dib != IntPtr.Zero) DeleteObject(dib);
                    if (memory != IntPtr.Zero) DeleteDC(memory);
                    destination.ReleaseHdc(target);
                }
            }
        }
    }

    class ThemedLabel : Label
    {
        public ThemedLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.Opaque | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            AlphaSurface.Paint(e.Graphics, ClientSize, PaintSurface);
        }
        internal void PaintSurface(Graphics graphics)
        {
            graphics.Clear(Theme.Surface);
            using (var brush = new SolidBrush(ForeColor))
            using (var format = new StringFormat())
            {
                format.Alignment = TextAlign == ContentAlignment.MiddleCenter
                    ? StringAlignment.Center : StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString(Text, Font, brush, ClientRectangle, format);
            }
        }
    }

    // ---- Shared palette ----
    static class Theme
    {
        // An explicit alpha surface carries this tint over the native material.
        internal static bool MaterialEnabled;
        internal static Color Surface { get { return MaterialEnabled && !HighContrast
            ? Color.FromArgb(IsDark ? 220 : 235, Bg) : Bg; } }
        public static bool IsDark { get; private set; }
        public static bool HighContrast { get; private set; }
        public static Color Bg, TitleDivider, Panel, PanelHover, PanelBorder;
        public static Color TextPrimary, TextSecondary, TextDim, Violet, Cyan;
        public static Color RingOff, RingOffHover, ToggleThumb;

        static Theme() { Refresh(); }

        // Windows lets app and shell themes differ; use the app preference here.
        public static bool Refresh()
        {
            bool dark = false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    dark = value is int && (int)value == 0;
                }
            }
            catch { dark = IsDark; } // Keep the current palette if access fails.
            bool highContrast = SystemInformation.HighContrast;
            bool changed = dark != IsDark || highContrast != HighContrast;
            SetPalette(dark, highContrast);
            return changed;
        }

        // Keep the preference lookup separate from rendering, so each palette can
        // also be verified without changing the user's Windows settings.
        internal static void SetPalette(bool dark, bool highContrast)
        {
            HighContrast = highContrast;
            IsDark = dark;
            Bg            = dark ? Color.FromArgb(27, 28, 35) : Color.FromArgb(243, 243, 247);
            TitleDivider  = dark ? Color.FromArgb(48, 49, 60) : Color.FromArgb(222, 223, 232);
            Panel         = dark ? Color.FromArgb(37, 39, 49) : Color.FromArgb(253, 253, 255);
            PanelHover    = dark ? Color.FromArgb(47, 49, 62) : Color.FromArgb(234, 234, 244);
            PanelBorder   = dark ? Color.FromArgb(63, 65, 79) : Color.FromArgb(204, 205, 219);
            TextPrimary   = dark ? Color.FromArgb(242, 243, 249) : Color.FromArgb(30, 31, 44);
            TextSecondary = dark ? Color.FromArgb(181, 185, 202) : Color.FromArgb(81, 85, 107);
            TextDim       = dark ? Color.FromArgb(166, 172, 193) : Color.FromArgb(89, 94, 115);
            Violet        = dark ? Color.FromArgb(146, 116, 255) : Color.FromArgb(112, 74, 230);
            Cyan          = dark ? Color.FromArgb(0, 212, 255) : Color.FromArgb(0, 103, 136);
            RingOff       = dark ? Color.FromArgb(76, 80, 100) : Color.FromArgb(181, 184, 201);
            RingOffHover  = dark ? Color.FromArgb(120, 126, 152) : Color.FromArgb(120, 125, 152);
            ToggleThumb   = dark ? Color.FromArgb(247, 248, 252) : Color.White;
            if (HighContrast)
            {
                Bg = Panel = SystemColors.Window;
                PanelHover = SystemColors.Control;
                TextPrimary = TextSecondary = TextDim = SystemColors.WindowText;
                TitleDivider = PanelBorder = RingOff = RingOffHover = SystemColors.WindowText;
                Violet = Cyan = SystemColors.Highlight;
                ToggleThumb = SystemColors.HighlightText;
            }
        }

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

    class ThemeMenuColors : ProfessionalColorTable
    {
        public ThemeMenuColors() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        public override Color MenuItemSelected { get { return Theme.PanelHover; } }
        public override Color MenuItemBorder { get { return Theme.PanelBorder; } }
        public override Color MenuBorder { get { return Theme.PanelBorder; } }
        public override Color SeparatorDark { get { return Theme.TitleDivider; } }
        public override Color SeparatorLight { get { return Theme.Panel; } }
    }

    // ---- Base for flicker-free custom-drawn controls ----
    class CustomControl : Control
    {
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            AlphaSurface.Paint(e.Graphics, ClientSize, PaintSurface);
        }
        protected virtual void PaintSurface(Graphics graphics) { }
        public CustomControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.Opaque |
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

        protected override void PaintSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Extended DWM frames still draw native caption glyphs underneath
            // the client. The custom header must occlude those pixels completely,
            // including each separate child HWND used for our title buttons.
            g.Clear(Theme.Bg);

            if (hover)
            {
                var bg = Theme.HighContrast ? SystemColors.Highlight
                    : (isClose ? Color.FromArgb(196, 63, 78) : Theme.PanelHover);
                using (var b = new SolidBrush(bg))
                using (var p = Theme.RoundedRect(new Rectangle(3, 3, Width - 7, Height - 7), 6))
                    g.FillPath(b, p);
            }

            var glyph = hover && Theme.HighContrast ? SystemColors.HighlightText
                : (hover && isClose ? Color.White : (hover ? Theme.TextPrimary : Theme.TextSecondary));
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

        protected override void PaintSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            int cx = Width / 2, cy = Height / 2;

            // Soft outer glow while active, breathing with the pulse phase
            if (active && !Theme.HighContrast)
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
            var glyphColor = active ? Theme.TextPrimary : (hover ? Theme.TextSecondary : Theme.TextDim);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) repeat.Dispose();
            base.Dispose(disposing);
        }

        protected override void PaintSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

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
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
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

        protected override void PaintSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(body, 8))
            {
                using (var b = new SolidBrush(hover ? Theme.PanelHover : Theme.Panel)) g.FillPath(b, path);
                using (var pen = new Pen(Theme.PanelBorder, 1f)) g.DrawPath(pen, path);
            }

            using (var f = new Font("Segoe UI", 8.75f))
            using (var b = new SolidBrush(hover ? Theme.TextPrimary : Theme.TextSecondary))
            using (var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }

        protected override void PaintSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(body, (Height - 1) / 2))
            {
                // Off-gray track, with the accent gradient fading in as the thumb travels
                using (var off = new SolidBrush(Theme.RingOff)) g.FillPath(off, path);
                if (Theme.HighContrast)
                {
                    using (var on = new SolidBrush(isChecked ? SystemColors.Highlight : Theme.RingOff))
                        g.FillPath(on, path);
                }
                else if (pos > 0.01f)
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
            var thumb = Theme.HighContrast
                ? (isChecked ? SystemColors.HighlightText : SystemColors.Window) : Theme.ToggleThumb;
            using (var b = new SolidBrush(thumb))
                g.FillEllipse(b, tx, 3, thumbD, thumbD);
        }
    }

    // Geometry is independent of live displays so irregular layouts can be tested.
    static class WindowPlacement
    {
        public static Rectangle Clamp(Rectangle window, Rectangle workArea)
        {
            int x = Math.Max(workArea.Left, Math.Min(window.X,
                workArea.Left + Math.Max(0, workArea.Width - window.Width)));
            int y = Math.Max(workArea.Top, Math.Min(window.Y,
                workArea.Top + Math.Max(0, workArea.Height - window.Height)));
            return new Rectangle(x, y, window.Width, window.Height);
        }

        public static Rectangle Center(Rectangle window, Rectangle workArea)
        {
            return Clamp(new Rectangle(workArea.Left + (workArea.Width - window.Width) / 2,
                workArea.Top + (workArea.Height - window.Height) / 2,
                window.Width, window.Height), workArea);
        }

        public static int FindScreen(Rectangle window, Rectangle[] workAreas)
        {
            int best = -1;
            double bestArea = -1, bestDistance = double.MaxValue;
            double cx = (double)window.X + window.Width / 2.0;
            double cy = (double)window.Y + window.Height / 2.0;
            for (int i = 0; i < workAreas.Length; i++)
            {
                Rectangle area = workAreas[i];
                // Use doubles for corrupt/stale saved coordinates near int limits.
                double width = Math.Max(0, Math.Min((double)window.X + window.Width,
                    (double)area.X + area.Width) - Math.Max(window.X, area.X));
                double height = Math.Max(0, Math.Min((double)window.Y + window.Height,
                    (double)area.Y + area.Height) - Math.Max(window.Y, area.Y));
                double overlap = width * height;
                double dx = cx - ((double)area.X + area.Width / 2.0);
                double dy = cy - ((double)area.Y + area.Height / 2.0);
                double distance = dx * dx + dy * dy;
                if (overlap > bestArea || (overlap == bestArea && distance < bestDistance))
                {
                    best = i;
                    bestArea = overlap;
                    bestDistance = distance;
                }
            }
            return best;
        }

        public static int FindScreenInDirection(int current, Rectangle[] workAreas, Keys direction)
        {
            if (current < 0 || current >= workAreas.Length) return -1;
            Rectangle source = workAreas[current];
            double cx = (double)source.X + source.Width / 2.0;
            double cy = (double)source.Y + source.Height / 2.0;
            int best = -1;
            double bestScore = double.MaxValue;
            bool bestAligned = false;
            for (int i = 0; i < workAreas.Length; i++)
            {
                if (i == current) continue;
                Rectangle target = workAreas[i];
                double dx = (double)target.X + target.Width / 2.0 - cx;
                double dy = (double)target.Y + target.Height / 2.0 - cy;
                double forward, sideways;
                bool aligned;
                switch (direction)
                {
                    case Keys.Left:
                        if ((double)target.X + target.Width > source.X) continue;
                        forward = -dx; sideways = Math.Abs(dy);
                        aligned = target.Top < source.Bottom && target.Bottom > source.Top;
                        break;
                    case Keys.Right:
                        if (target.X < (double)source.X + source.Width) continue;
                        forward = dx; sideways = Math.Abs(dy);
                        aligned = target.Top < source.Bottom && target.Bottom > source.Top;
                        break;
                    case Keys.Up:
                        if ((double)target.Y + target.Height > source.Y) continue;
                        forward = -dy; sideways = Math.Abs(dx);
                        aligned = target.Left < source.Right && target.Right > source.Left;
                        break;
                    case Keys.Down:
                        if (target.Y < (double)source.Y + source.Height) continue;
                        forward = dy; sideways = Math.Abs(dx);
                        aligned = target.Left < source.Right && target.Right > source.Left;
                        break;
                    default: return -1;
                }
                if (forward <= 0) continue;
                // Prefer nearby displays aligned with the requested direction.
                double score = forward + sideways * 2;
                if ((aligned && !bestAligned) || (aligned == bestAligned && score < bestScore))
                {
                    best = i;
                    bestScore = score;
                    bestAligned = aligned;
                }
            }
            return best;
        }

        public static Rectangle Move(Rectangle window, Rectangle source, Rectangle target)
        {
            Rectangle visible = Clamp(window, source);
            int sourceTravelX = Math.Max(0, source.Width - window.Width);
            int sourceTravelY = Math.Max(0, source.Height - window.Height);
            double x = sourceTravelX == 0 ? 0.5 : (double)(visible.X - source.X) / sourceTravelX;
            double y = sourceTravelY == 0 ? 0.5 : (double)(visible.Y - source.Y) / sourceTravelY;
            return Clamp(new Rectangle(target.X + (int)Math.Round(x * Math.Max(0, target.Width - window.Width)),
                target.Y + (int)Math.Round(y * Math.Max(0, target.Height - window.Height)),
                window.Width, window.Height), target);
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

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        // ---- Rounded window ----
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left, Right, Top, Bottom;
            public MARGINS(int value) { Left = Right = Top = Bottom = value; }
        }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled(out bool enabled);

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
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HOTKEY_ID = 0xA731;
        private const int RECOVERY_HOTKEY_ID = 0xA732;

        private const uint MOD_ALT     = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT   = 0x0004;
        private const uint MOD_WIN     = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

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
        private readonly Timer placementSaveTimer;
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
        private StreamDeckServer streamDeckServer;
        private Icon trayIconOn;
        private Icon trayIconOff;
        private bool themeReady;
        private bool backdropEnabled;
        private bool direction = false;
        private double pulsePhase = 0;

        // ---- Hotkey state ----
        private uint hotkeyMods = MOD_CONTROL | MOD_ALT;
        private Keys hotkeyKey  = Keys.J;
        private bool capturingHotkey = false;
        private bool hotkeyRegistered = false;
        private bool recoveryHotkeyRegistered = false;
        private ToolStripMenuItem recoveryHintItem;

        // ---- Startup behavior ----
        private bool startHidden = false;
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
        private Size compactWindowSize;

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr Hwnd, InsertAfter;
            public int X, Y, Width, Height, Flags;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Keep a normal shell window, even though we draw our own frame.
                // Removing these styles also removes standard monitor movement.
                cp.Style &= unchecked((int)~0x80000000u); // WS_POPUP
                cp.Style |= 0x00CF0000; // WS_OVERLAPPEDWINDOW
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        public MainForm(bool silent) : this(silent, false) { }

        public MainForm(bool silent, bool background)
        {
            Theme.Refresh();
            LoadSettings();

            if (silent || background)
            {
                startHidden = true;
            }

            Text = "Mouse Jiggler";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            // WM_NCCALCSIZE makes the whole window client area. ClientSize would
            // otherwise add the hidden standard frame when calculating Size.
            Size = new Size(300, 590);
            compactWindowSize = Size;
            TopMost = alwaysOnTop;
            BackColor = Theme.Bg;
            DoubleBuffered = false;
            SetOpaqueRegion();

            // Clamp stale or partially offscreen positions into a connected display.
            if (hasSavedLocation)
            {
                Rectangle[] workAreas = GetWorkingAreas();
                Rectangle bounds = new Rectangle(savedLocation, Size);
                int screen = WindowPlacement.FindScreen(bounds, workAreas);
                StartPosition = FormStartPosition.Manual;
                if (screen >= 0) Location = WindowPlacement.Clamp(bounds, workAreas[screen]).Location;
                savedLocation = Location;
            }

            // App icon (taskbar / Alt+Tab)
            var appIcon = LoadAppIcon();
            if (appIcon != null) Icon = appIcon;

            // -- Title bar buttons --
            var minButton = new TitleButton(false) { Location = new Point(Width - 82, 7) };
            minButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(minButton);

            var closeButton = new TitleButton(true) { Location = new Point(Width - 44, 7) };
            closeButton.Click += delegate { SaveSettings(); Hide(); };
            Controls.Add(closeButton);

            // -- Power button --
            powerButton = new PowerButton
            {
                Size = new Size(136, 136),
                Location = new Point((Width - 136) / 2, 58)
            };
            powerButton.Click += delegate { Toggle(); };
            Controls.Add(powerButton);

            status = new ThemedLabel
            {
                Text = "IDLE",
                Font = new Font("Segoe UI Semibold", 13f),
                ForeColor = Theme.TextDim,
                BackColor = Theme.Bg,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(Width - 2, 28),
                Location = new Point(1, 198)
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

            var hint = new ThemedLabel
            {
                Text = "Alt + arrows: move  ·  Ctrl + Alt + Home: recover\nClose hides to tray  ·  right-click tray to quit",
                Tag = "Hint",
                ForeColor = Theme.TextDim,
                BackColor = Theme.Bg,
                Font = new Font("Segoe UI", 8f),
                Location = new Point(1, Height - 50),
                Size = new Size(Width - 2, 40),
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

            // Shell monitor moves need persistence too; coalesce move events
            // so dragging does not write settings for every pixel.
            placementSaveTimer = new Timer { Interval = 250 };
            placementSaveTimer.Tick += delegate { placementSaveTimer.Stop(); SaveSettings(); };

            var trayMenu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("Show on this monitor");
            showItem.Click += delegate { ShowFromTray(); };
            recoveryHintItem = new ToolStripMenuItem("Recover window: Ctrl+Alt+Home") { Enabled = false };
            var toggleItem = new ToolStripMenuItem("Toggle");
            toggleItem.Click += delegate { Toggle(); };
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { tray.Visible = false; Application.Exit(); };
            trayMenu.Items.Add(showItem);
            trayMenu.Items.Add(recoveryHintItem);
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
            themeReady = true;
            if (!IsHandleCreated) { IntPtr windowHandle = Handle; }
            else
            {
                ApplyTheme();
                RegisterWindowHotkeys();
            }

            // Load waits until the first visible show. Start here so a silent
            // launch jiggles immediately and later showing it cannot retoggle.
            if (silent) Toggle();
            if (Program.StreamDeckEnabled) StartStreamDeckServer();
        }

        private void StartStreamDeckServer()
        {
            try
            {
                streamDeckServer = new StreamDeckServer(this, HandleStreamDeckCommand,
                    GetStreamDeckPipeName(), GetStreamDeckDescriptorPath());
            }
            catch (Exception)
            {
                // Integration is optional; a file/pipe permission failure must
                // not stop ordinary tray, shortcut, or startup behavior.
                streamDeckServer = null;
            }
        }

        internal static string GetStreamDeckPipeName()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                return "MouseJiggler.StreamDeck." + identity.User.Value + "." + process.SessionId;
        }

        internal static string GetStreamDeckDescriptorPath()
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseJiggler"), "streamdeck.json");
        }

        internal string HandleStreamDeckCommand(string command)
        {
            if (IsDisposed || Disposing) return StreamDeckServer.Error("App is shutting down.");
            switch (command)
            {
                case "status": break;
                case "toggle": Toggle(); break;
                case "on": if (!timer.Enabled) Toggle(); break;
                case "off": if (timer.Enabled) Toggle(); break;
                default: return StreamDeckServer.Error("Unknown command.");
            }
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                return "{\"ok\":true,\"protocolVersion\":1,\"running\":true,\"active\":" +
                    (timer.Enabled ? "true" : "false") + ",\"processId\":" + process.Id + "}";
        }

        private void SetOpaqueRegion()
        {
            IntPtr roundedRegion = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 16, 16);
            if (roundedRegion == IntPtr.Zero) return;
            try
            {
                Region old = Region;
                Region = Region.FromHrgn(roundedRegion);
                if (old != null) old.Dispose();
            }
            finally { DeleteObject(roundedRegion); }
        }

        private static bool TransparencyAllowed()
        {
            if (Theme.HighContrast || SystemInformation.TerminalServerSession) return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("EnableTransparency");
                    return !(value is int) || (int)value != 0;
                }
            }
            catch { return false; }
        }

        private void ApplyBackdrop()
        {
            if (!IsHandleCreated) return;
            bool enabled = false;
            try
            {
                bool composition;
                bool allowed = TransparencyAllowed() &&
                    DwmIsCompositionEnabled(out composition) == 0 && composition;
                int dark = Theme.IsDark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                int material = allowed ? 3 : 1; // Desktop Acrylic / no backdrop
                bool supported = DwmSetWindowAttribute(Handle, 38, ref material, sizeof(int)) == 0;
                var margins = new MARGINS(allowed && supported ? -1 : 0);
                enabled = allowed && supported &&
                    DwmExtendFrameIntoClientArea(Handle, ref margins) == 0;
                if (!enabled)
                {
                    material = 1;
                    DwmSetWindowAttribute(Handle, 38, ref material, sizeof(int));
                    margins = new MARGINS(0);
                    DwmExtendFrameIntoClientArea(Handle, ref margins);
                }
                int corners = 2;
                DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            backdropEnabled = Theme.MaterialEnabled = enabled;
            if (enabled)
            {
                Region old = Region;
                Region = null;
                if (old != null) old.Dispose();
            }
            else SetOpaqueRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (themeReady && !backdropEnabled && Width > 0 && Height > 0) SetOpaqueRegion();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (themeReady)
            {
                ApplyTheme();
                RegisterWindowHotkeys();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterWindowHotkeys();
            base.OnHandleDestroyed(e);
        }

        private void ApplyTheme()
        {
            ApplyBackdrop();
            BackColor = Theme.Bg;
            foreach (Control control in Controls)
            {
                control.BackColor = Theme.Bg;
                if (control is Label)
                    control.ForeColor = control == status
                        ? (timer.Enabled ? Theme.Cyan : Theme.TextDim)
                        : ((string)control.Tag == "Hint" ? Theme.TextDim : Theme.TextSecondary);
                control.Invalidate();
            }
            var menu = tray.ContextMenuStrip;
            menu.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColors());
            menu.BackColor = Theme.Panel;
            menu.ForeColor = Theme.TextPrimary;
            foreach (ToolStripItem item in menu.Items) item.ForeColor = Theme.TextPrimary;
            menu.Invalidate();

            Icon oldOn = trayIconOn, oldOff = trayIconOff;
            trayIconOn = MakeTrayIcon(true);
            trayIconOff = MakeTrayIcon(false);
            tray.Icon = (timer.Enabled ? trayIconOn : trayIconOff) ?? Icon ?? SystemIcons.Application;
            if (oldOn != null) oldOn.Dispose();
            if (oldOff != null) oldOff.Dispose();
            Invalidate(true);
        }

        private void AddRowLabel(string text, int x, int y, int rowH)
        {
            Controls.Add(new ThemedLabel
            {
                Text = text,
                ForeColor = Theme.TextSecondary,
                BackColor = Theme.Bg,
                Font = new Font("Segoe UI", 9.25f),
                Location = new Point(x, y),
                Size = new Size(120, rowH),
                TextAlign = ContentAlignment.MiddleLeft
            });
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            AlphaSurface.Paint(e.Graphics, ClientSize, PaintForm);
        }
        private void PaintForm(Graphics g)
        {
            g.Clear(Theme.Surface);
            // WM_NCCALCSIZE removes the ordinary frame geometry, but not a frame
            // extended by DwmExtendFrameIntoClientArea. Keep native shell styles
            // and Acrylic for the body; cover its native caption with alpha 255.
            using (var header = new SolidBrush(Theme.Bg))
                g.FillRectangle(header, 0, 0, ClientSize.Width, TitleBarHeight);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var outline = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (var pen = new Pen(Theme.PanelBorder)) g.DrawPath(pen, outline);

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
            // The tray, global recovery shortcut, and reopening all recover onto
            // the monitor under the pointer, even if the old monitor is connected.
            Rectangle bounds = NormalWindowBounds();
            Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            Rectangle target = workArea.Contains(bounds) ? bounds : WindowPlacement.Center(bounds, workArea);
            // Showing a hidden minimized form first is essential: WinForms can
            // defer a WindowState change while hidden and keep the native icon.
            Show();
            WindowState = FormWindowState.Normal;
            Bounds = target;
            Activate();
            RememberWindowLocation();
        }

        private Rectangle NormalWindowBounds()
        {
            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Size normalSize = compactWindowSize.IsEmpty ? Size : compactWindowSize;
            if (bounds.Width <= 0 || bounds.Height <= 0 ||
                (bounds.X <= -30000 && bounds.Y <= -30000 &&
                 bounds.Width < normalSize.Width && bounds.Height < normalSize.Height))
                return new Rectangle(hasSavedLocation ? savedLocation : Location, normalSize);
            return new Rectangle(bounds.Location, normalSize);
        }

        private static Rectangle[] GetWorkingAreas()
        {
            Screen[] screens = Screen.AllScreens;
            var areas = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) areas[i] = screens[i].WorkingArea;
            return areas;
        }

        private void RememberWindowLocation()
        {
            if (placementSaveTimer != null) placementSaveTimer.Stop();
            savedLocation = NormalWindowBounds().Location;
            hasSavedLocation = true;
            SaveSettings();
        }

        private void MoveToMonitor(Keys direction)
        {
            Rectangle[] areas = GetWorkingAreas();
            Rectangle bounds = NormalWindowBounds();
            int current = WindowPlacement.FindScreen(bounds, areas);
            int target = WindowPlacement.FindScreenInDirection(current, areas, direction);
            if (target < 0) return;
            Bounds = WindowPlacement.Move(bounds, areas[current], areas[target]);
            RememberWindowLocation();
        }

        private void EnsureWindowVisible()
        {
            if (IsDisposed || Disposing) return;
            Rectangle bounds = NormalWindowBounds();
            Rectangle[] areas = GetWorkingAreas();
            int screen = WindowPlacement.FindScreen(bounds, areas);
            if (screen < 0) return;
            Rectangle visible = WindowPlacement.Clamp(bounds, areas[screen]);
            if (visible == bounds) return;
            // WinForms updates restoredWindowBounds when bounds are assigned
            // while minimized. Changing visibility/state first can accidentally
            // capture the minimized icon rectangle as the normal restore size.
            Bounds = visible;
            savedLocation = visible.Location;
            hasSavedLocation = true;
            if (placementSaveTimer != null) placementSaveTimer.Stop();
            SaveSettings();
        }

        // ---- Hotkey ----
        private void RegisterWindowHotkeys()
        {
            if (!themeReady || !IsHandleCreated) return;
            RegisterAppHotkey();
            if (recoveryHotkeyRegistered) return;
            recoveryHotkeyRegistered = RegisterHotKey(Handle, RECOVERY_HOTKEY_ID,
                MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.Home);
            recoveryHintItem.Text = recoveryHotkeyRegistered
                ? "Recover window: Ctrl+Alt+Home"
                : "Ctrl+Alt+Home unavailable; use Show";
        }

        private void UnregisterWindowHotkeys()
        {
            if (IsHandleCreated)
            {
                if (hotkeyRegistered) UnregisterHotKey(Handle, HOTKEY_ID);
                if (recoveryHotkeyRegistered) UnregisterHotKey(Handle, RECOVERY_HOTKEY_ID);
            }
            hotkeyRegistered = recoveryHotkeyRegistered = false;
        }

        private void RegisterAppHotkey()
        {
            if (hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HOTKEY_ID);
                hotkeyRegistered = false;
            }
            if (hotkeyKey == Keys.None) return;

            // Recovery has its own fixed shortcut and cannot become the jiggle toggle.
            bool recovery = hotkeyMods == (MOD_CONTROL | MOD_ALT) && hotkeyKey == Keys.Home;
            bool movement = hotkeyMods == MOD_ALT && IsArrowKey(hotkeyKey);
            bool reserved = recovery || movement;
            bool ok = !reserved && RegisterHotKey(Handle, HOTKEY_ID, hotkeyMods | MOD_NOREPEAT, (uint)hotkeyKey);
            hotkeyRegistered = ok;
            if (!ok)
            {
                hotkeyButton.Text = FormatHotkey(hotkeyMods, hotkeyKey) +
                    (recovery ? " (recovery)" : (movement ? " (move screen)" : " (in use)"));
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

                if (m == (MOD_CONTROL | MOD_ALT) && k == Keys.Home)
                {
                    hotkeyButton.Text = "Reserved for recovery";
                    return true;
                }
                if (m == MOD_ALT && IsArrowKey(k))
                {
                    hotkeyButton.Text = "Reserved for moving";
                    return true;
                }

                hotkeyMods = m;
                hotkeyKey  = k;
                capturingHotkey = false;
                SaveSettings();
                RegisterAppHotkey();
                return true;
            }

            // Only consume this while Mouse Jiggler has keyboard focus. Other
            // applications keep their usual Alt+arrow navigation shortcuts.
            Keys direction = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.Alt &&
                IsArrowKey(direction))
            {
                MoveToMonitor(direction);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            // Retain native window capabilities but draw the entire compact
            // surface ourselves, without a second standard caption/border.
            if (m.Msg == WM_NCCALCSIZE)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_GETMINMAXINFO && !compactWindowSize.IsEmpty)
            {
                base.WndProc(ref m);
                var sizes = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                sizes.MinTrackSize = sizes.MaxTrackSize = new Point(compactWindowSize.Width, compactWindowSize.Height);
                sizes.MaxSize = sizes.MinTrackSize;
                Marshal.StructureToPtr(sizes, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_WINDOWPOSCHANGING && !compactWindowSize.IsEmpty &&
                WindowState == FormWindowState.Normal && !IsIconic(Handle))
            {
                var position = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                if ((position.Flags & 0x0001) == 0) // SWP_NOSIZE
                {
                    position.Width = compactWindowSize.Width;
                    position.Height = compactWindowSize.Height;
                    Marshal.StructureToPtr(position, m.LParam, false);
                }
            }
            if (m.Msg == WM_SYSCOMMAND)
            {
                long command = m.WParam.ToInt64() & 0xFFF0;
                if (command == 0xF000 || command == 0xF030) // SC_SIZE / SC_MAXIMIZE
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
            }

            // Broadcasts arrive on the UI thread, including while hidden in the tray.
            if (themeReady && (m.Msg == 0x001A || m.Msg == 0x031A ||
                m.Msg == 0x031E || m.Msg == 0x0218)) // settings, theme, composition, power
            {
                Theme.Refresh();
                ApplyTheme();
            }

            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                Toggle();
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == RECOVERY_HOTKEY_ID)
            {
                ShowFromTray();
                return;
            }

            // A second copy of the app was launched; surface this window instead.
            if (m.Msg == Program.WM_SHOW_INSTANCE && Program.WM_SHOW_INSTANCE != 0)
            {
                ShowFromTray();
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                var p = PointToClient(new Point(x, y));
                bool title = p.X >= 0 && p.X < ClientSize.Width &&
                    p.Y >= 0 && p.Y < TitleBarHeight && !(GetChildAtPoint(p) is TitleButton);
                m.Result = (IntPtr)(title ? HTCAPTION : HTCLIENT);
                return;
            }

            base.WndProc(ref m);
            if (themeReady && IsHandleCreated && !Disposing && (m.Msg == 0x007E ||
                (m.Msg == 0x001A && m.WParam.ToInt64() == 0x002F))) // display/work area changed
                BeginInvoke(new MethodInvoker(EnsureWindowVisible));
        }

        private static bool IsArrowKey(Keys key)
        {
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down;
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
                if (themeReady && placementSaveTimer != null)
                {
                    placementSaveTimer.Stop();
                    placementSaveTimer.Start();
                }
            }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (placementSaveTimer != null) placementSaveTimer.Stop();
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
            UnregisterWindowHotkeys();
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
                if (streamDeckServer != null) streamDeckServer.Dispose();
                SetThreadExecutionState(ES_CONTINUOUS);
                if (tray != null) tray.Visible = false;
                if (tray != null)
                {
                    if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
                    tray.Dispose();
                }
                if (timer != null) timer.Dispose();
                if (animTimer != null) animTimer.Dispose();
                if (placementSaveTimer != null) placementSaveTimer.Dispose();
                if (trayIconOn  != null) trayIconOn.Dispose();
                if (trayIconOff != null) trayIconOff.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
