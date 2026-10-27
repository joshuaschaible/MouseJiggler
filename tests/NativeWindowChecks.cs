using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using MouseJigglerApp;

// Compiled with an isolated copy of MouseJiggler.cs by Verify-Window.ps1.
// Messages target only this process's own window. The copy uses private test
// registry keys, distinct shortcuts, and no-op mouse/sleep APIs.
sealed class CheckedForm : MainForm
{
    public CheckedForm() : base(false) { }
    protected override bool ShowWithoutActivation { get { return true; } }
    public bool Command(Keys keys)
    {
        Message message = Message.Create(Handle, 0x0100, IntPtr.Zero, IntPtr.Zero);
        return base.ProcessCmdKey(ref message, keys);
    }
    public void RecreateNativeHandle() { RecreateHandle(); }
}

static class NativeWindowChecks
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    static extern int DwmGetWindowRectangle(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref NativeMargins margins);
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr dc);
    [DllImport("dwmapi.dll")]
    static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("user32.dll")]
    static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeMargins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    const uint TestModifiers = 0x4003; // Ctrl + Alt + NOREPEAT
    const int ProbeId = 0xAC79;
    static readonly List<object> results = new List<object>();
    static readonly string settingsRoot = (string)typeof(MainForm).GetField("RegRoot", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
    static readonly string startupRoot = (string)typeof(MainForm).GetField("RunKey", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
    static readonly int toggleId = (int)typeof(MainForm).GetField("HOTKEY_ID", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
    static readonly int recoveryId = (int)typeof(MainForm).GetField("RECOVERY_HOTKEY_ID", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
    static int failures;
    static string outputDirectory;
    static bool captionBaseline;

    [STAThread]
    public static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (!settingsRoot.StartsWith(@"Software\MouseJigglerNativeChecks\", StringComparison.Ordinal) ||
            startupRoot != settingsRoot + @"\Startup" || toggleId != 0xAC71 || recoveryId != 0xAC72)
        {
            Console.Error.WriteLine("Isolation guard failed. Compile and run with Verify-Window.ps1 only.");
            return 2;
        }
        outputDirectory = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(outputDirectory);
        bool preview = Array.IndexOf(args, "--preview") >= 0;
        captionBaseline = Array.IndexOf(args, "--caption-baseline") >= 0;
        bool captionProbe = Array.IndexOf(args, "--caption-probe") >= 0;
        bool captionOnly = Array.IndexOf(args, "--caption-only") >= 0;
        try
        {
            if (preview) return Preview(args);
            if (captionProbe) ProbeRedirectionAlpha();
            else if (captionOnly) RunCaptionChecks();
            else RunChecks();
        }
        catch (Exception ex)
        {
            Record("Unexpected harness error", false, ex.ToString());
        }
        finally
        {
            // No real app registry locations are used by this assembly.
            Registry.CurrentUser.DeleteSubKeyTree(settingsRoot, false);
        }
        using (var cleaned = Registry.CurrentUser.OpenSubKey(settingsRoot))
            Record("Temporary settings are removed after validation", cleaned == null, settingsRoot);
        var screens = new List<object>();
        foreach (Screen screen in Screen.AllScreens)
            screens.Add(new { device = screen.DeviceName, bounds = screen.Bounds.ToString(), workArea = screen.WorkingArea.ToString(), primary = screen.Primary });
        string json = new JavaScriptSerializer().Serialize(new {
            utc = DateTime.UtcNow.ToString("o"), source = "isolated production source copy",
            testRegistry = settingsRoot, shortcuts = "Ctrl+Alt+F11 toggle; Ctrl+Alt+F12 recovery",
            noExternalInput = true, mouseAndSleepApisDisabled = true,
            explicitActivationDisabled = true, nativeRestoresUseNoActivation = true,
            captionBaseline = captionBaseline,
            captionProbe = captionProbe,
            captionOnly = captionOnly,
            appearanceEvidence = "Native state queried from this harness's own HWND. No desktop or compositor screenshot is captured.",
            screens = screens, failures = failures, checks = results
        });
        File.WriteAllText(Path.Combine(outputDirectory, "native-window-report.json"), json);
        Console.WriteLine("Native window checks: " + results.Count + " checks, " + failures + " failures.");
        return failures == 0 ? 0 : 1;
    }

    static int Preview(string[] args)
    {
        bool dark = Array.IndexOf(args, "--dark") >= 0;
        SeedSettings();
        using (var form = new CheckedForm())
        using (var autoClose = new System.Windows.Forms.Timer { Interval = 90000 })
        {
            Theme.SetPalette(dark, false);
            Call(form, "ApplyTheme");
            form.Text = "Mouse Jiggler isolated review preview";
            form.TopMost = false;
            Call(form, "ShowFromTray");
            autoClose.Tick += delegate { form.Hide(); Application.ExitThread(); };
            autoClose.Start();
            Application.Run(form);
        }
        return 0;
    }

    static void RunChecks()
    {
        SeedSettings();
        Screen[] screens = Screen.AllScreens;
        using (var probe = new Form())
        {
            IntPtr probeHandle = probe.Handle;
            using (var form = new CheckedForm())
            {
                form.Show();
                Pump();
                CheckCaptionState(form, "Initial handle");
                Record("Recovery shortcut really registered", Field<bool>(form, "recoveryHotkeyRegistered"), Hint(form));
                Record("Toggle shortcut really registered", Field<bool>(form, "hotkeyRegistered"), ButtonText(form));
                AssertUnavailable(probeHandle, Keys.F12, "Recovery registration blocks a second native owner");
                AssertUnavailable(probeHandle, Keys.F11, "Toggle registration blocks a second native owner");

                CheckBackdropTransitions(form);
                CheckNativeShellFrame(form);
                CheckCaptionLifecycle(form);
                CheckMovement(form, screens);
                CheckCaptureReservations(form);
                CheckRecovery(form);
                CheckDisplayMessages(form);
                CheckNativeMovePersistence(form);
                CheckSettingPreservation(form);

                IntPtr oldHandle = form.Handle;
                form.RecreateNativeHandle();
                Pump();
                CheckCaptionState(form, "Recreated handle");
                Record("Handle recreation registers both shortcuts", form.Handle != oldHandle &&
                    Field<bool>(form, "recoveryHotkeyRegistered") && Field<bool>(form, "hotkeyRegistered"),
                    "Old=" + oldHandle + "; new=" + form.Handle);
                AssertUnavailable(probeHandle, Keys.F12, "Recovery registration survives handle recreation");
            }
            Pump();
            AssertAvailable(probeHandle, Keys.F12, "Disposal releases recovery registration");
            AssertAvailable(probeHandle, Keys.F11, "Disposal releases toggle registration");

            Point persisted = SavedPoint();
            using (var reopened = new CheckedForm())
            {
                Record("Saved keyboard/recovery position reopens", reopened.Location == persisted,
                    "Saved=" + persisted + "; actual=" + reopened.Location);
            }

            CheckConflict(probeHandle, Keys.F12, "recoveryHotkeyRegistered", "unavailable", "Recovery shortcut conflict");
            CheckConflict(probeHandle, Keys.F11, "hotkeyRegistered", "in use", "Toggle shortcut conflict");

            using (var key = Registry.CurrentUser.OpenSubKey(startupRoot))
                Record("Harness does not create a startup entry", key == null, startupRoot);

            using (var key = Registry.CurrentUser.CreateSubKey(settingsRoot))
            {
                key.SetValue("WindowX", 100000, RegistryValueKind.DWord);
                key.SetValue("WindowY", 100000, RegistryValueKind.DWord);
            }
            using (var stale = new CheckedForm())
                Record("Stale saved location is fully recovered at startup", FullyVisible(stale.Bounds), stale.Bounds.ToString());

            using (var key = Registry.CurrentUser.CreateSubKey(settingsRoot))
            {
                var area = Screen.PrimaryScreen.WorkingArea;
                key.SetValue("WindowX", area.Right - 1, RegistryValueKind.DWord);
                key.SetValue("WindowY", area.Bottom - 1, RegistryValueKind.DWord);
            }
            using (var clipped = new CheckedForm())
                Record("One-pixel saved intersection is clamped fully", FullyVisible(clipped.Bounds), clipped.Bounds.ToString());
        }
    }

    static void CheckBackdropTransitions(CheckedForm form)
    {
        bool originalDark = Theme.IsDark;
        bool originalContrast = Theme.HighContrast;
        bool transparencyPreference = true;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            object setting = key == null ? null : key.GetValue("EnableTransparency");
            transparencyPreference = !(setting is int) || (int)setting != 0;
        }
        bool composition;
        bool available = DwmIsCompositionEnabled(out composition) == 0 && composition &&
            transparencyPreference && !SystemInformation.TerminalServerSession;
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                Theme.SetPalette(dark, false);
                Call(form, "ApplyTheme");
                Pump();
                int material, immersiveDark;
                int materialResult = DwmGetWindowAttribute(form.Handle, 38, out material, sizeof(int));
                int darkResult = DwmGetWindowAttribute(form.Handle, 20, out immersiveDark, sizeof(int));
                bool enabled = Field<bool>(form, "backdropEnabled");
                CheckCaptionState(form, (dark ? "Dark" : "Light") + " material");
                bool expectedMaterial = available && materialResult == 0;
                Record((dark ? "Dark" : "Light") + " uses the native material or policy fallback",
                    enabled == expectedMaterial && Theme.MaterialEnabled == enabled &&
                    (enabled ? material == 3 && form.Region == null && Theme.Surface.A == (dark ? 220 : 235)
                        : Theme.Surface.A == 255 && form.Region != null),
                    "Enabled=" + enabled + "; nativeType=" + material + "; tintAlpha=" + Theme.Surface.A +
                    "; composition=" + composition + "; transparencyPreference=" + transparencyPreference + "; queryHRESULT=" + materialResult);
                Record((dark ? "Dark" : "Light") + " sets native frame theme consistently", darkResult == 0 && immersiveDark == (dark ? 1 : 0),
                    "Native dark=" + immersiveDark + "; HRESULT=" + darkResult);
            }
            Theme.SetPalette(true, true);
            Call(form, "ApplyTheme");
            Pump();
            int opaqueMaterial;
            int opaqueResult = DwmGetWindowAttribute(form.Handle, 38, out opaqueMaterial, sizeof(int));
            CheckCaptionState(form, "High-contrast fallback");
            Record("High-contrast fallback disables native material with exact opaque surfaces",
                !Field<bool>(form, "backdropEnabled") && !Theme.MaterialEnabled &&
                Theme.Surface.A == 255 && Theme.Surface == SystemColors.Window && form.Region != null &&
                (opaqueResult != 0 || opaqueMaterial == 1),
                "Native type=" + opaqueMaterial + "; surface=" + Theme.Surface + "; rounded region present=" + (form.Region != null));
            Theme.SetPalette(originalDark, originalContrast);
            Call(form, "ApplyTheme");
            Pump();
            CheckCaptionState(form, "Restored current theme");
            Record("Appearance returns from fallback to current policy", Theme.IsDark == originalDark &&
                Theme.HighContrast == originalContrast && Field<bool>(form, "backdropEnabled") == (available && !originalContrast),
                "Enabled=" + Field<bool>(form, "backdropEnabled") + "; dark=" + Theme.IsDark);

            for (int i = 0; i < 10; i++) Repaint(form);
            uint before = GetGuiResources(GetCurrentProcess(), 0);
            for (int i = 0; i < 120; i++) Repaint(form);
            Pump();
            uint after = GetGuiResources(GetCurrentProcess(), 0);
            Record("Repeated native alpha repaints release GDI resources", before > 0 && after <= before + 4,
                "GDI objects before=" + before + "; after=" + after + "; 120 full form/control repaint cycles");
        }
        finally
        {
            Theme.SetPalette(originalDark, originalContrast);
            Call(form, "ApplyTheme");
        }
    }

    static void Repaint(CheckedForm form)
    {
        form.Refresh();
        foreach (Control control in form.Controls) control.Refresh();
    }

    static void CheckNativeShellFrame(CheckedForm form)
    {
        uint style = unchecked((uint)GetWindowLong(form.Handle, -16));
        const uint shellBits = 0x00C00000 | 0x00080000 | 0x00040000 | 0x00020000 | 0x00010000;
        Record("Native styles permit standard shell monitor movement", (style & shellBits) == shellBits && (style & 0x80000000) == 0,
            "Style=0x" + style.ToString("X8") + "; standard Win+Shift+arrow dispatch still requires a manual shell check");
        NativeRect client, window;
        bool clientOk = GetClientRect(form.Handle, out client);
        bool windowOk = GetWindowRect(form.Handle, out window);
        Record("Invisible native frame keeps the compact full client", clientOk && windowOk &&
            client.Right - client.Left == 300 && client.Bottom - client.Top == 590 &&
            window.Right - window.Left == 300 && window.Bottom - window.Top == 590,
            "Client=" + (client.Right-client.Left) + "x" + (client.Bottom-client.Top) + "; window=" + (window.Right-window.Left) + "x" + (window.Bottom-window.Top));

        IntPtr rectMemory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
        IntPtr minMaxMemory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MinMaxInfo)));
        try
        {
            var proposed = new NativeRect { Left = 13, Top = 29, Right = 313, Bottom = 619 };
            Marshal.StructureToPtr(proposed, rectMemory, false);
            SendMessage(form.Handle, 0x0083, IntPtr.Zero, rectMemory);
            var actual = (NativeRect)Marshal.PtrToStructure(rectMemory, typeof(NativeRect));
            Record("WM_NCCALCSIZE preserves the custom full-client rectangle", actual.Left == proposed.Left && actual.Top == proposed.Top && actual.Right == proposed.Right && actual.Bottom == proposed.Bottom,
                "Client rectangle unchanged");
            Marshal.StructureToPtr(new MinMaxInfo(), minMaxMemory, false);
            SendMessage(form.Handle, 0x0024, IntPtr.Zero, minMaxMemory);
            var sizing = (MinMaxInfo)Marshal.PtrToStructure(minMaxMemory, typeof(MinMaxInfo));
            Record("Native min/max tracking keeps the fixed app dimensions", sizing.MinTrackSize.X == 300 && sizing.MinTrackSize.Y == 590 && sizing.MaxTrackSize.X == 300 && sizing.MaxTrackSize.Y == 590,
                "Min=" + sizing.MinTrackSize.X + "x" + sizing.MinTrackSize.Y + "; max=" + sizing.MaxTrackSize.X + "x" + sizing.MaxTrackSize.Y);
        }
        finally { Marshal.FreeHGlobal(rectMemory); Marshal.FreeHGlobal(minMaxMemory); }

        Record("Custom title remains a native drag caption", HitTest(form, new Point(45, 20)) == 2, "HTCAPTION expected");
        Record("Title buttons and content remain client controls", HitTest(form, new Point(form.Width - 60, 20)) == 1 &&
            HitTest(form, new Point(form.Width - 20, 20)) == 1 && HitTest(form, new Point(1, 100)) == 1,
            "Minimize, close, and side edge return HTCLIENT");
        Size size = form.Size;
        var titleButtons = new List<Control>();
        foreach (Control control in form.Controls)
            if (control is TitleButton) titleButtons.Add(control);
        titleButtons.Sort(delegate(Control a, Control b) { return a.Left.CompareTo(b.Left); });
        Record("Exactly one custom minimize/close button pair remains", titleButtons.Count == 2,
            "TitleButton controls=" + titleButtons.Count);
        if (titleButtons.Count == 2)
        {
            typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(titleButtons[0], new object[] { EventArgs.Empty });
            Pump();
            Record("Custom minimize button still minimizes the window", form.WindowState == FormWindowState.Minimized, form.WindowState.ToString());
            RestoreWithoutActivation(form);
            Pump();
            CheckCaptionState(form, "Custom minimize/restore");
            typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(titleButtons[1], new object[] { EventArgs.Empty });
            Pump();
            Record("Custom close button still hides to the tray", !form.Visible && !form.IsDisposed, "Visible=" + form.Visible);
            form.Show();
            Pump();
            CheckCaptionState(form, "Custom close/show");
        }
        SendMessage(form.Handle, 0x0112, (IntPtr)0xF020, IntPtr.Zero);
        Pump();
        Record("Native minimize system command remains usable", form.WindowState == FormWindowState.Minimized, form.WindowState.ToString());
        RestoreWithoutActivation(form);
        Pump();
        Record("Native no-activation restore retains normal app dimensions", form.WindowState == FormWindowState.Normal && form.Size == size,
            "State=" + form.WindowState + "; size=" + form.Size);
        CheckCaptionState(form, "Native restore");
        SendMessage(form.Handle, 0x0112, (IntPtr)0xF030, IntPtr.Zero);
        Pump();
        Record("Native maximize keeps fixed compact window", form.WindowState == FormWindowState.Normal && form.Size == size,
            "State=" + form.WindowState + "; size=" + form.Size);
    }

    static void CheckCaptionLifecycle(CheckedForm form)
    {
        // Deliver the same lifecycle notifications Windows uses, solely to the
        // isolated test window. No system theme, transparency or display setting
        // is changed by this fixture.
        foreach (int message in new[] { 0x001A, 0x031A, 0x031E })
        {
            SendMessage(form.Handle, message, IntPtr.Zero, IntPtr.Zero);
            Pump();
            CheckCaptionState(form, "Lifecycle message 0x" + message.ToString("X4"));
            int material;
            int result = DwmGetWindowAttribute(form.Handle, 38, out material, sizeof(int));
            Record("Lifecycle message 0x" + message.ToString("X4") + " preserves material policy",
                result != 0 ? !Field<bool>(form, "backdropEnabled") :
                    material == (Field<bool>(form, "backdropEnabled") ? 3 : 1),
                "Native backdrop type=" + material + "; HRESULT=" + result);
            Record("Lifecycle message 0x" + message.ToString("X4") + " preserves custom caption hit tests",
                HitTest(form, new Point(45, 20)) == 2 && HitTest(form, new Point(form.Width - 20, 20)) == 1,
                "Title=HTCAPTION; close=HTCLIENT");
        }
    }

    static void CheckCaptionState(CheckedForm form, string phase)
    {
        int enabled;
        int result = DwmGetWindowAttribute(form.Handle, 1, out enabled, sizeof(int));
        NativeRect buttons;
        int buttonsResult = DwmGetWindowRectangle(form.Handle, 5, out buttons, Marshal.SizeOf(typeof(NativeRect)));
        // DWMWA_CAPTION_BUTTON_BOUNDS is attribute 5, not EXTENDED_FRAME_BOUNDS (9).
        // Caption-button bounds are diagnostic only: Microsoft documents undefined
        // bounds when a window is invisible/minimized and does not guarantee that a
        // returned rectangle proves the compositor painted glyphs in that rectangle.
        Record(phase + " native caption rendering state is readable", result == 0,
            "NativeNCRendering=" + enabled + "; HRESULT=" + result +
            "; captionButtonBounds=" + buttons.Left + "," + buttons.Top + "," + buttons.Right + "," + buttons.Bottom +
            "; boundsHRESULT=" + buttonsResult);
        ReadNativePaint(form, phase.ToLowerInvariant().Replace(' ', '-').Replace('/', '-'), !captionBaseline);
    }

    static void ProbeRedirectionAlpha()
    {
        SeedSettings();
        using (var form = new CheckedForm())
        {
            form.Show();
            Pump();
            CheckCaptionState(form, "Original extended frame");
            int alpha = 1;
            int alphaResult = DwmSetWindowAttribute(form.Handle, 39, ref alpha, sizeof(int));
            var margins = new NativeMargins();
            int frameResult = DwmExtendFrameIntoClientArea(form.Handle, ref margins);
            Pump();
            CheckCaptionState(form, "Redirection alpha + zero margins");
            int material, nativeAlpha;
            int materialResult = DwmGetWindowAttribute(form.Handle, 38, out material, sizeof(int));
            int alphaQuery = DwmGetWindowAttribute(form.Handle, 39, out nativeAlpha, sizeof(int));
            Record("Redirection bitmap alpha probe reports native results", true,
                "Set alpha HRESULT=" + alphaResult + "; zero frame HRESULT=" + frameResult +
                "; material=" + material + "; material HRESULT=" + materialResult +
                "; read alpha=" + nativeAlpha + "; read alpha HRESULT=" + alphaQuery);
            ReadNativePaint(form, "redirection-alpha-probe", false);
        }
    }

    static void RunCaptionChecks()
    {
        SeedSettings();
        using (var form = new CheckedForm())
        {
            form.Show();
            Pump();
            foreach (bool dark in new[] { false, true })
            {
                Theme.SetPalette(dark, false);
                Call(form, "ApplyTheme");
                Pump();
                CheckCaptionState(form, (dark ? "Dark" : "Light") + " caption regression");
            }
            Theme.SetPalette(true, true);
            Call(form, "ApplyTheme");
            Pump();
            CheckCaptionState(form, "High-contrast caption regression");
        }
    }

    static void ReadNativePaint(CheckedForm form, string name, bool requireOpaqueCaption)
    {
        IntPtr memory = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        int width = form.ClientSize.Width, height = form.ClientSize.Height;
        try
        {
            memory = CreateCompatibleDC(IntPtr.Zero);
            var header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                Width = width, Height = -height, Planes = 1, BitCount = 32 };
            IntPtr bits;
            dib = CreateDIBSection(memory, ref header, 0, out bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();
            old = SelectObject(memory, dib);
            if (old == IntPtr.Zero || old == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception();
            var bytes = new byte[width * height * 4];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = 0xA5;
            Marshal.Copy(bytes, 0, bits, bytes.Length);
            bool painted = PrintWindow(form.Handle, memory, 1); // PW_CLIENTONLY; our own HWND
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            NativeRect buttons;
            int boundsResult = DwmGetWindowRectangle(form.Handle, 5, out buttons, Marshal.SizeOf(typeof(NativeRect)));
            // This is the actual WM_PRINT path into a 32-bit native DIB. It checks
            // pixels emitted by our own controls; DWM's final backdrop/glyph layers
            // are not included. No external window or desktop pixels are accessed.
            bool nativeBoundsAvailable = boundsResult == 0 && buttons.Right > buttons.Left && buttons.Bottom > buttons.Top;
            Rectangle caption = nativeBoundsAvailable
                ? Rectangle.FromLTRB(buttons.Left, buttons.Top, buttons.Right, buttons.Bottom)
                : new Rectangle(0, 0, width, 44);
            caption.Intersect(new Rectangle(0, 0, width, height));
            bool positiveNativeBounds = nativeBoundsAvailable && caption.Width > 0 && caption.Height > 0;
            bool materialEnabled = Field<bool>(form, "backdropEnabled");
            Record(name + " native caption coverage has positive inspected bounds or policy fallback",
                !materialEnabled || positiveNativeBounds,
                "NativeBoundsAvailable=" + nativeBoundsAvailable + "; intersection=" + caption + "; materialEnabled=" + materialEnabled +
                "; empty native bounds in fallback are not compositor coverage evidence");
            int minAlpha = 255, maxAlpha = 0;
            for (int y = caption.Top; y < caption.Bottom; y++)
                for (int x = caption.Left; x < caption.Right; x++)
                {
                    int value = bytes[(y * width + x) * 4 + 3];
                    minAlpha = Math.Min(minAlpha, value); maxAlpha = Math.Max(maxAlpha, value);
                }
            int fullHeaderMinAlpha = 255;
            for (int y = 0; y < Math.Min(44, height); y++)
                for (int x = 0; x < width; x++)
                    fullHeaderMinAlpha = Math.Min(fullHeaderMinAlpha, bytes[(y * width + x) * 4 + 3]);
            int bodyAlpha = bytes[(Math.Min(450, height - 1) * width + Math.Min(10, width - 1)) * 4 + 3];
            string opaqueProof = positiveNativeBounds ? " opaque native client paint covers the caption underlay" : " opaque native client paint covers the complete fallback header";
            Record(name + (requireOpaqueCaption ? opaqueProof : " native client paint alpha is measured"),
                painted && (!materialEnabled || positiveNativeBounds) &&
                (!requireOpaqueCaption || (minAlpha == 255 && fullHeaderMinAlpha == 255)) && bodyAlpha == Theme.Surface.A,
                "PrintWindow=" + painted + "; rectangle=" + caption +
                "; alpha range=" + minAlpha + ".." + maxAlpha + "; full44pxHeaderMinAlpha=" + fullHeaderMinAlpha +
                "; body alpha=" + bodyAlpha + "; expected body alpha=" + Theme.Surface.A +
                "; excludes DWM compositor layers");
            File.WriteAllBytes(Path.Combine(outputDirectory, name + "-native-client.bgra"), bytes);
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try { for (int row = 0; row < height; row++) Marshal.Copy(bytes, row * width * 4, IntPtr.Add(data.Scan0, row * data.Stride), width * 4); }
                finally { bitmap.UnlockBits(data); }
                bitmap.Save(Path.Combine(outputDirectory, name + "-native-client.png"), ImageFormat.Png);
            }
        }
        finally
        {
            if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(memory, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memory != IntPtr.Zero) DeleteDC(memory);
        }
    }

    static long HitTest(CheckedForm form, Point clientPoint)
    {
        Point screenPoint = form.PointToScreen(clientPoint);
        int packed = (screenPoint.X & 0xFFFF) | ((screenPoint.Y & 0xFFFF) << 16);
        return SendMessage(form.Handle, 0x0084, IntPtr.Zero, (IntPtr)packed).ToInt64();
    }

    static void CheckMovement(CheckedForm form, Screen[] screens)
    {
        Screen primary = Screen.PrimaryScreen;
        Rectangle area = primary.WorkingArea;
        form.Bounds = new Rectangle(area.Left + 140, area.Top + 160, form.Width, form.Height);
        foreach (Keys direction in new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down })
        {
            form.Bounds = new Rectangle(area.Left + 140, area.Top + 160, form.Width, form.Height);
            Rectangle before = form.Bounds;
            bool consumed = form.Command(Keys.Alt | direction);
            bool changed = form.Bounds != before;
            // Real physical layout is captured in the report. Horizontal and vertical
            // directions are checked for real transfer or stable no-op at an edge.
            Screen target = Screen.FromRectangle(form.Bounds);
            bool directionCorrect = direction == Keys.Left ? target.WorkingArea.Right <= area.Left
                : direction == Keys.Right ? target.WorkingArea.Left >= area.Right
                : direction == Keys.Up ? target.WorkingArea.Bottom <= area.Top
                : target.WorkingArea.Top >= area.Bottom;
            bool physicalTargetExists = false;
            foreach (Screen candidate in screens)
            {
                Rectangle work = candidate.WorkingArea;
                physicalTargetExists |= direction == Keys.Left ? work.Right <= area.Left
                    : direction == Keys.Right ? work.Left >= area.Right
                    : direction == Keys.Up ? work.Bottom <= area.Top : work.Top >= area.Bottom;
            }
            Record("Focused Alt+" + direction + " follows physical monitor direction",
                consumed && FullyVisible(form.Bounds) && (physicalTargetExists ? changed && directionCorrect : !changed),
                "From=" + before + "; to=" + form.Bounds + "; destination=" + target.DeviceName + "; targetExists=" + physicalTargetExists);
            if (changed)
            {
                Record("Alt+" + direction + " persists new location", SavedPoint() == form.Location, SavedPoint().ToString());
                Rectangle moved = form.Bounds;
                Keys reverse = direction == Keys.Left ? Keys.Right : direction == Keys.Right ? Keys.Left : direction == Keys.Up ? Keys.Down : Keys.Up;
                form.Command(Keys.Alt | reverse);
                Record("Opposite arrow returns to original monitor", Screen.FromRectangle(form.Bounds).DeviceName == primary.DeviceName,
                    "From=" + moved + "; returned=" + form.Bounds);
            }
        }
        Rectangle unchanged = form.Bounds;
        form.Command(Keys.Control | Keys.Alt | Keys.Left);
        Record("Additional modifiers do not move the window", form.Bounds == unchanged, form.Bounds.ToString());
    }

    static void CheckCaptureReservations(CheckedForm form)
    {
        Call(form, "BeginCaptureHotkey");
        form.Command(Keys.Control | Keys.Alt | Keys.F12);
        Record("Recovery combo stays reserved during capture", Field<bool>(form, "capturingHotkey") && ButtonText(form).Contains("Reserved"), ButtonText(form));
        form.Command(Keys.Alt | Keys.Left);
        Record("Movement combo stays reserved during capture", Field<bool>(form, "capturingHotkey") && ButtonText(form).Contains("Reserved"), ButtonText(form));
        form.Command(Keys.Escape);
        Record("Capture cancellation preserves toggle shortcut", !Field<bool>(form, "capturingHotkey") && Field<Keys>(form, "hotkeyKey") == Keys.F11, ButtonText(form));
    }

    static void CheckRecovery(CheckedForm form)
    {
        Screen pointer = Screen.FromPoint(Cursor.Position);
        foreach (Screen other in Screen.AllScreens)
        {
            if (other.DeviceName == pointer.DeviceName) continue;
            Rectangle work = other.WorkingArea;
            form.Bounds = new Rectangle(work.Left + 120, work.Top + 120, 300, 590);
            form.Hide();
            SendMessage(form.Handle, 0x0312, (IntPtr)recoveryId, IntPtr.Zero);
            Pump();
            Record("Global recovery brings a hidden window off connected " + other.DeviceName,
                form.Visible && pointer.WorkingArea.Contains(form.Bounds), "Destination=" + Screen.FromRectangle(form.Bounds).DeviceName);
        }
        foreach (string mode in new[] { "normal", "hidden", "minimized", "hidden-minimized" })
        {
            RestoreWithoutActivation(form);
            form.Show();
            form.Location = new Point(100000, 100000);
            if (mode == "hidden") form.Hide();
            if (mode == "minimized" || mode == "hidden-minimized") form.WindowState = FormWindowState.Minimized;
            if (mode == "hidden-minimized") form.Hide();
            Screen pointerScreen = Screen.FromPoint(Cursor.Position);
            SendMessage(form.Handle, 0x0312, (IntPtr)recoveryId, IntPtr.Zero);
            Pump();
            Record("WM_HOTKEY recovers " + mode + " window onto pointer monitor",
                form.Visible && form.WindowState == FormWindowState.Normal && pointerScreen.WorkingArea.Contains(form.Bounds),
                "Expected=" + pointerScreen.DeviceName + "; bounds=" + form.Bounds + "; state=" + form.WindowState);
            Record("Recovery persists " + mode + " location", SavedPoint() == form.Location, SavedPoint().ToString());
        }
        Rectangle current = form.Bounds;
        Call(form, "ShowFromTray");
        Record("Tray show preserves an already reachable location", form.Bounds == current, form.Bounds.ToString());
        form.Hide();
        form.Location = new Point(-100000, -100000);
        Screen trayTarget = Screen.FromPoint(Cursor.Position);
        Call(form, "ShowFromTray");
        Record("Tray show recovers independently of hotkey", form.Visible && trayTarget.WorkingArea.Contains(form.Bounds), form.Bounds.ToString());
        form.Hide();
        form.Location = new Point(100000, 100000);
        SendMessage(form.Handle, Program.WM_SHOW_INSTANCE, IntPtr.Zero, IntPtr.Zero);
        Pump();
        Record("Second-instance message recovers existing window", form.Visible && FullyVisible(form.Bounds), form.Bounds.ToString());
        bool wasActive = Field<System.Windows.Forms.Timer>(form, "timer").Enabled;
        SendMessage(form.Handle, 0x0312, (IntPtr)toggleId, IntPtr.Zero);
        Record("Toggle message keeps its existing purpose", Field<System.Windows.Forms.Timer>(form, "timer").Enabled != wasActive, "Isolated native toggle message received");
        SendMessage(form.Handle, 0x0312, (IntPtr)toggleId, IntPtr.Zero);
    }

    static void CheckDisplayMessages(CheckedForm form)
    {
        foreach (string mode in new[] { "visible", "hidden", "minimized", "hidden-minimized" })
        {
            RestoreWithoutActivation(form);
            form.Show();
            form.Location = new Point(100000, 100000);
            if (mode == "hidden") form.Hide();
            if (mode == "minimized" || mode == "hidden-minimized") form.WindowState = FormWindowState.Minimized;
            if (mode == "hidden-minimized") form.Hide();
            SendMessage(form.Handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
            Pump();
            Rectangle normal = (Rectangle)Call(form, "NormalWindowBounds");
            bool statePreserved = mode == "hidden" ? !form.Visible : mode == "minimized" ? form.WindowState == FormWindowState.Minimized && form.Visible
                : mode == "hidden-minimized" ? !form.Visible && form.WindowState == FormWindowState.Minimized : form.Visible;
            Record("Display change repairs " + mode + " bounds and preserves state", FullyVisible(normal) && statePreserved && normal.Size == new Size(300, 590),
                "Normal=" + normal + "; visible=" + form.Visible + "; state=" + form.WindowState);
            Record("Display recovery persists " + mode + " location", SavedPoint() == normal.Location, SavedPoint().ToString());
            if (mode == "minimized" || mode == "hidden-minimized")
            {
                if (mode == "hidden-minimized") form.Show();
                RestoreWithoutActivation(form);
                Pump();
                Record("Restoring " + mode + " after display repair keeps full visible app",
                    FullyVisible(form.Bounds) && form.Size == new Size(300, 590) && form.Location == normal.Location,
                    "Restored=" + form.Bounds + "; expected=" + normal);
            }
        }
        RestoreWithoutActivation(form);
        form.Show();
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        form.Location = new Point(area.Right - 1, area.Bottom - 1);
        SendMessage(form.Handle, 0x001A, (IntPtr)0x002F, IntPtr.Zero);
        Pump();
        Record("Work-area change clamps partial intersection", FullyVisible(form.Bounds) && form.Size == new Size(300, 590), form.Bounds.ToString());
    }

    static void CheckSettingPreservation(CheckedForm form)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(settingsRoot))
        {
            bool preserved = (int)key.GetValue("Interval") == 37 && (int)key.GetValue("Distance") == 9 &&
                (int)key.GetValue("AlwaysOnTop") == 0 && (int)key.GetValue("PauseWhenActive") == 0 &&
                (int)key.GetValue("KeepAwake") == 0 && (int)key.GetValue("HotkeyKey") == (int)Keys.F11;
            Record("Recovery preserves behavior settings", preserved, "Interval=37; distance=9; topmost/pause/wake=false; toggle=F11");
        }
    }

    static void CheckNativeMovePersistence(CheckedForm form)
    {
        form.Show();
        RestoreWithoutActivation(form);
        Pump(350);
        Screen[] screens = Screen.AllScreens;
        Rectangle targetArea = screens[screens.Length - 1].WorkingArea;
        Point target = new Point(targetArea.Left + 180, targetArea.Top + 180);
        bool moved = SetWindowPos(form.Handle, IntPtr.Zero, target.X, target.Y, 0, 0, 0x0015);
        Pump(350);
        Record("Native shell-style move saves its position after settling", moved && form.Location == target && SavedPoint() == target && FullyVisible(form.Bounds),
            "Location=" + form.Location + "; saved=" + SavedPoint() + "; expected=" + target);
    }

    static void CheckConflict(IntPtr probe, Keys key, string field, string expectedText, string name)
    {
        bool held = RegisterHotKey(probe, ProbeId, TestModifiers, (uint)key);
        Record(name + " fixture reserves native shortcut", held, "Win32 error=" + Marshal.GetLastWin32Error());
        if (!held) return;
        try
        {
            using (var conflicted = new CheckedForm())
            {
                string text = key == Keys.F12 ? Hint(conflicted) : ButtonText(conflicted);
                Record(name + " reports unavailable without claiming ownership", !Field<bool>(conflicted, field) && text.Contains(expectedText), text);
                if (key == Keys.F12)
                {
                    conflicted.Location = new Point(100000, 100000);
                    Call(conflicted, "ShowFromTray");
                    Record("Tray recovery works when global shortcut is unavailable", conflicted.Visible && FullyVisible(conflicted.Bounds), conflicted.Bounds.ToString());
                }
            }
        }
        finally { UnregisterHotKey(probe, ProbeId); }
        AssertAvailable(probe, key, name + " fixture releases its shortcut");
    }

    static void AssertUnavailable(IntPtr probe, Keys key, string name)
    {
        bool registered = RegisterHotKey(probe, ProbeId, TestModifiers, (uint)key);
        int error = Marshal.GetLastWin32Error();
        if (registered) UnregisterHotKey(probe, ProbeId);
        Record(name, !registered && error == 1409, "Win32 error=" + error);
    }
    static void AssertAvailable(IntPtr probe, Keys key, string name)
    {
        bool registered = RegisterHotKey(probe, ProbeId, TestModifiers, (uint)key);
        int error = Marshal.GetLastWin32Error();
        if (registered) UnregisterHotKey(probe, ProbeId);
        Record(name, registered, "Win32 error=" + error);
    }
    static void SeedSettings()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(settingsRoot))
        {
            key.SetValue("Interval", 37, RegistryValueKind.DWord);
            key.SetValue("Distance", 9, RegistryValueKind.DWord);
            key.SetValue("HotkeyMods", 3, RegistryValueKind.DWord);
            key.SetValue("HotkeyKey", (int)Keys.F11, RegistryValueKind.DWord);
            key.SetValue("AlwaysOnTop", 0, RegistryValueKind.DWord);
            key.SetValue("PauseWhenActive", 0, RegistryValueKind.DWord);
            key.SetValue("KeepAwake", 0, RegistryValueKind.DWord);
        }
    }
    static Point SavedPoint()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(settingsRoot))
            return new Point((int)key.GetValue("WindowX"), (int)key.GetValue("WindowY"));
    }
    static bool FullyVisible(Rectangle bounds)
    {
        foreach (Screen screen in Screen.AllScreens)
            if (screen.WorkingArea.Contains(bounds)) return true;
        return false;
    }
    public static void RestoreWithoutActivation(Form form)
    {
        // Same native normal-state geometry, without bringing this fixture to
        // the foreground while the user is using another app.
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
    }
    static T Field<T>(object instance, string name)
    {
        return (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
    }
    static object Call(object instance, string name)
    {
        return typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, null);
    }
    static string Hint(CheckedForm form) { return Field<ToolStripMenuItem>(form, "recoveryHintItem").Text; }
    static string ButtonText(CheckedForm form) { return Field<Control>(form, "hotkeyButton").Text; }
    static void Pump(int duration = 100)
    {
        for (int i = 0; i < Math.Max(5, (duration + 19) / 20); i++) { Application.DoEvents(); Thread.Sleep(20); }
    }
    static void Record(string name, bool success, string details)
    {
        if (!success) failures++;
        results.Add(new { name = name, passed = success, details = details });
        Console.WriteLine((success ? "PASS " : "FAIL ") + name + " — " + details);
    }
}
