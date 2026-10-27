using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseJigglerApp
{
    // Compiled with an isolated temporary copy of the actual application source.
    // Verify-Appearance.ps1 redirects registry paths and removes input/hotkey effects.
    static class AppearanceChecks
    {
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        struct NativeBitmapInfo
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
            ref NativeBitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

        static int checks;
        static string output;
        internal static bool PreferDark;

        [STAThread]
        static int Main(string[] args)
        {
            output = Path.GetFullPath(args[0]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var form = new MainForm(false))
                {
                    Assert(!form.Visible, "Rendering must not show a desktop window");
                    Assert(!Get<NotifyIcon>(form, "tray").Visible, "Rendering must not show a tray icon");
                    RenderTheme(form, false, false, "light");
                    RenderTheme(form, true, false, "dark");
                    RenderTheme(form, false, true, "high-contrast");
                    CheckNativeAlpha(form, false, "light");
                    CheckNativeAlpha(form, true, "dark");
                    CheckHiddenThemeNotifications(form);
                    CheckShapeRefresh(form);
                    CheckSettingsLayout(form);
                }
                Console.WriteLine("PASS: " + checks + " appearance checks; rendered PNGs in " + output);
                Console.WriteLine("Validated native premultiplied-alpha buffers/BitBlt transfer, worst-background text contrast, opaque fallback, hover/on/off states, system colors, hidden theme notifications, tray menu, and fixed rounded shape.");
                Console.WriteLine("Alpha previews use simulated black/white backgrounds; they do not capture desktop Acrylic composition.");
                Console.WriteLine("Windows appearance preferences and production app settings, startup entries, shortcuts, and mouse input were not modified.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        static void RenderTheme(MainForm form, bool dark, bool highContrast, string name)
        {
            SetActiveVisual(form, false);
            SetHovers(form, false);
            Theme.SetPalette(dark, highContrast);
            Invoke(form, "ApplyTheme");
            Assert(!Theme.MaterialEnabled, "Deterministic raster suite uses opaque fallback");
            CheckColors(form);
            Assert(Contrast(Theme.TextPrimary, Theme.Bg) >= 4.5, name + " primary-text contrast");
            Assert(Contrast(Theme.TextSecondary, Theme.Bg) >= 4.5, name + " settings-label contrast");
            Assert(Contrast(Theme.TextDim, Theme.Bg) >= 4.5, name + " hint/status contrast");
            Assert(Contrast(Theme.Cyan, Theme.Bg) >= 4.5, name + " active-status contrast");
            RenderForm(form, name + "-idle");
            RenderMenu(form, name + "-tray-menu");

            SetActiveVisual(form, true);
            SetHovers(form, true);
            Theme.SetPalette(dark, highContrast);
            Invoke(form, "ApplyTheme");
            CheckColors(form);
            RenderForm(form, name + "-active-hover");
            CheckControlBackgrounds(form, name);

            if (highContrast)
            {
                Assert(Theme.Bg == SystemColors.Window, "Contrast-theme background uses system color");
                Assert(Theme.TextPrimary == SystemColors.WindowText, "Contrast-theme text uses system color");
                var close = FindCloseButton(form);
                using (var bitmap = RenderControl(close))
                    AssertColor(bitmap.GetPixel(8, 14), SystemColors.Highlight, "Contrast close-button hover");
                var off = Get<ToggleSwitch>(form, "startupCheck");
                var on = Get<ToggleSwitch>(form, "topMostCheck");
                using (var bitmap = RenderControl(off))
                    AssertColor(bitmap.GetPixel(11, 11), SystemColors.Window, "Contrast off-switch thumb");
                using (var bitmap = RenderControl(on))
                    AssertColor(bitmap.GetPixel(on.Width - 12, 11), SystemColors.HighlightText, "Contrast on-switch thumb");
            }
            SetActiveVisual(form, false);
        }

        static void CheckHiddenThemeNotifications(MainForm form)
        {
            SetHovers(form, false);
            int[] messages = { 0x001A, 0x031A, 0x031E, 0x0218 };
            var interval = Get<Stepper>(form, "intervalBox");
            int originalInterval = interval.Value;
            var timer = Get<Timer>(form, "timer");
            for (int index = 0; index < messages.Length; index++)
            {
                bool dark = index % 2 == 0;
                SetActiveVisual(form, index >= 2);
                PreferDark = dark;
                SendMessage(form.Handle, messages[index], IntPtr.Zero, IntPtr.Zero);
                Assert(Theme.IsDark == dark, "Hidden message refreshes fake app-theme preference " + messages[index]);
                Assert(Theme.HighContrast == SystemInformation.HighContrast, "Notification reads actual contrast preference");
                CheckColors(form);
                Assert(!form.Visible, "Theme notification keeps window hidden");
                Assert(!Get<NotifyIcon>(form, "tray").Visible, "Theme notification keeps tray test suppressed");
                Assert(interval.Value == originalInterval, "Theme notification preserves interval");
                Assert(timer.Enabled == (index >= 2), "Theme notification preserves active state");
            }
            SetActiveVisual(form, false);
        }

        static void CheckShapeRefresh(MainForm form)
        {
            Assert(form.Region != null, "Opaque window has rounded region");
            Assert(!form.Region.IsVisible(0, 0), "Window corner is clipped");
            var original = form.Size;
            form.Size = new Size(original.Width + 30, original.Height + 30);
            Assert(form.Size == original, "Compact window rejects accidental resizing");
            form.Size = original;
            Assert(form.Region.IsVisible(form.Width / 2, form.Height / 2), "Rounded region retains window interior");
        }

        static void CheckSettingsLayout(MainForm form)
        {
            // Sibling HWND clipping follows native Z-order, which can differ
            // from DrawToBitmap's composition order. A label background must
            // never cover a numeric/button/switch edge in the same row.
            foreach (Control label in form.Controls)
            {
                if (!(label is ThemedLabel)) continue;
                foreach (Control control in form.Controls)
                {
                    if (!(control is Stepper || control is PillButton || control is ToggleSwitch)) continue;
                    Assert(!label.Bounds.IntersectsWith(control.Bounds),
                        "Settings label '" + label.Text + "' does not overlap " + control.GetType().Name);
                }
            }
        }

        static void CheckNativeAlpha(MainForm form, bool dark, string name)
        {
            SetActiveVisual(form, true);
            SetHovers(form, false);
            Theme.SetPalette(dark, false);
            Invoke(form, "ApplyTheme");
            Theme.MaterialEnabled = true;
            try
            {
                Assert(Theme.Surface.A > 0 && Theme.Surface.A < 255, "Native " + name + " surface remains translucent");
                Color worstBackground = Blend(Theme.Surface, dark ? Color.White : Color.Black);
                Assert(Contrast(Theme.TextPrimary, worstBackground) >= 4.5, "Native " + name + " worst-background primary contrast");
                Assert(Contrast(Theme.TextSecondary, worstBackground) >= 4.5, "Native " + name + " worst-background label contrast");
                Assert(Contrast(Theme.TextDim, worstBackground) >= 4.5, "Native " + name + " worst-background hint contrast");
                Assert(Contrast(Theme.Cyan, worstBackground) >= 4.5, "Native " + name + " worst-background active contrast");

                using (var frame = RenderPaintBuffer(form))
                {
                    CheckAlphaBytes(frame, "Native " + name + " form");
                    AssertSurface(frame.GetPixel(12, 80), "Native " + name + " form tint");
                    using (var expected = AlphaSurface.Render(form.ClientSize, delegate(Graphics graphics) { Invoke(form, "PaintForm", graphics); }))
                        Assert(SamePixels(expected, frame), "Native BitBlt preserves exact form premultiplied bytes");

                    using (var graphics = Graphics.FromImage(frame))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        foreach (Control control in form.Controls)
                        {
                            using (var child = RenderPaintBuffer(control))
                            {
                                CheckAlphaBytes(child, "Native " + name + " " + control.GetType().Name);
                                if (control is TitleButton)
                                {
                                    AssertColor(child.GetPixel(0, 0), Theme.Bg, "Native " + name + " caption-child background");
                                    Assert(AllPixelsOpaque(child), "Native " + name + " caption-child fully occludes native glyphs");
                                }
                                else
                                    AssertSurface(child.GetPixel(0, 0), "Native " + name + " child tint");
                                if (control is CustomControl)
                                {
                                    using (var expected = AlphaSurface.Render(control.ClientSize,
                                        delegate(Graphics surface) { Invoke(control, "PaintSurface", surface); }))
                                        Assert(SamePixels(expected, child), "Native BitBlt preserves exact " + control.GetType().Name + " bytes");
                                }
                                if (control is ThemedLabel)
                                    Assert(ContainsOpaquePixel(child), "Native label glyphs retain opaque foreground alpha");
                                graphics.DrawImageUnscaled(child, control.Location);
                            }
                        }
                    }
                    SaveComposite(frame, Color.Black, "alpha-" + name + "-over-black");
                    SaveComposite(frame, Color.White, "alpha-" + name + "-over-white");
                    frame.Save(Path.Combine(output, "alpha-" + name + "-native-buffer.png"), ImageFormat.Png);
                    SaveColorComposite(frame, "translucent-" + name + "-composite");
                }

                Theme.SetPalette(dark, true);
                Assert(Theme.Surface.A == 255, "High Contrast forces opaque surface even with stale material flag");
            }
            finally
            {
                Theme.MaterialEnabled = false;
                Theme.SetPalette(dark, false);
                SetActiveVisual(form, false);
                Invoke(form, "ApplyTheme");
            }
        }

        static Bitmap RenderPaintBuffer(Control control)
        {
            // Graphics.FromImage.GetHdc/ReleaseHdc normalizes the Bitmap's alpha.
            // A native DIB DC instead preserves the actual SRCCOPY destination
            // bytes and exercises the production OnPaint/AlphaSurface.Paint path.
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero), dib = IntPtr.Zero, old = IntPtr.Zero;
            int width = control.ClientSize.Width, height = control.ClientSize.Height;
            try
            {
                var info = new NativeBitmapInfo { Size = (uint)Marshal.SizeOf(typeof(NativeBitmapInfo)),
                    Width = width, Height = -height, Planes = 1, BitCount = 32 };
                IntPtr bits;
                dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
                if (dc == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("Could not create native capture DIB");
                old = SelectObject(dc, dib);
                if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new InvalidOperationException("Could not select native capture DIB");
                var bytes = new byte[width * height * 4];
                Marshal.Copy(bytes, 0, bits, bytes.Length);
                using (var graphics = Graphics.FromHdc(dc))
                    Invoke(control, "OnPaint", new PaintEventArgs(graphics, control.ClientRectangle));
                Marshal.Copy(bits, bytes, 0, bytes.Length);
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    for (int row = 0; row < height; row++)
                        Marshal.Copy(bytes, row * width * 4, IntPtr.Add(data.Scan0, row * data.Stride), width * 4);
                }
                finally { bitmap.UnlockBits(data); }
                return bitmap;
            }
            finally
            {
                if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(dc, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (dc != IntPtr.Zero) DeleteDC(dc);
            }
        }

        static void CheckAlphaBytes(Bitmap bitmap, string name)
        {
            byte[] bytes = RawPixels(bitmap);
            bool premultiplied = true, backgroundAlphaPreserved = true;
            for (int index = 0; index < bytes.Length; index += 4)
            {
                byte alpha = bytes[index + 3];
                if (bytes[index] > alpha || bytes[index + 1] > alpha || bytes[index + 2] > alpha) premultiplied = false;
                if (alpha < Theme.Surface.A) backgroundAlphaPreserved = false;
            }
            Assert(premultiplied, name + " is valid premultiplied BGRA");
            Assert(backgroundAlphaPreserved, name + " has no erased/zero-alpha holes");
        }

        static byte[] RawPixels(Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = new byte[bitmap.Width * bitmap.Height * 4];
                for (int row = 0; row < bitmap.Height; row++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), bytes, row * bitmap.Width * 4, bitmap.Width * 4);
                return bytes;
            }
            finally { bitmap.UnlockBits(data); }
        }

        static bool SamePixels(Bitmap first, Bitmap second)
        {
            byte[] a = RawPixels(first), b = RawPixels(second);
            if (a.Length != b.Length) return false;
            for (int index = 0; index < a.Length; index++) if (a[index] != b[index]) return false;
            return true;
        }

        static bool ContainsOpaquePixel(Bitmap bitmap)
        {
            byte[] bytes = RawPixels(bitmap);
            for (int index = 3; index < bytes.Length; index += 4) if (bytes[index] == 255) return true;
            return false;
        }

        static bool AllPixelsOpaque(Bitmap bitmap)
        {
            byte[] bytes = RawPixels(bitmap);
            for (int index = 3; index < bytes.Length; index += 4) if (bytes[index] != 255) return false;
            return true;
        }

        static void AssertSurface(Color actual, string message)
        {
            Color expected = Theme.Surface;
            Assert(actual.A == expected.A && Math.Abs(actual.R - expected.R) <= 1 &&
                Math.Abs(actual.G - expected.G) <= 1 && Math.Abs(actual.B - expected.B) <= 1,
                message + " (expected " + expected + ", got " + actual + ")");
        }

        static Color Blend(Color source, Color background)
        {
            double opacity = source.A / 255.0;
            return Color.FromArgb((int)Math.Round(source.R * opacity + background.R * (1 - opacity)),
                (int)Math.Round(source.G * opacity + background.G * (1 - opacity)),
                (int)Math.Round(source.B * opacity + background.B * (1 - opacity)));
        }

        static void SaveComposite(Bitmap foreground, Color background, string name)
        {
            using (var bitmap = new Bitmap(foreground.Width, foreground.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(background);
                    graphics.DrawImageUnscaled(foreground, Point.Empty);
                }
                bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            }
        }

        static void SaveColorComposite(Bitmap foreground, string name)
        {
            using (var bitmap = new Bitmap(foreground.Width, foreground.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var bounds = new Rectangle(Point.Empty, bitmap.Size);
                    using (var gradient = new LinearGradientBrush(bounds,
                        Color.FromArgb(246, 111, 102), Color.FromArgb(54, 132, 221), 55f))
                        graphics.FillRectangle(gradient, bounds);
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(-140, 180, 500, 620);
                        using (var glow = new PathGradientBrush(path))
                        {
                            glow.CenterColor = Color.FromArgb(230, 74, 193, 150);
                            glow.SurroundColors = new Color[] { Color.FromArgb(0, 74, 193, 150) };
                            graphics.FillPath(glow, path);
                        }
                    }
                    graphics.DrawImageUnscaled(foreground, Point.Empty);
                }
                bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            }
        }

        static void CheckColors(MainForm form)
        {
            AssertColor(form.BackColor, Theme.Bg, "Form background follows palette");
            foreach (Control control in form.Controls)
            {
                AssertColor(control.BackColor, Theme.Bg, control.GetType().Name + " opaque background");
                var label = control as Label;
                if (label == null) continue;
                Color expected = label == Get<Label>(form, "status")
                    ? (Get<Timer>(form, "timer").Enabled ? Theme.Cyan : Theme.TextDim)
                    : ((string)label.Tag == "Hint" ? Theme.TextDim : Theme.TextSecondary);
                AssertColor(label.ForeColor, expected, "Label foreground follows palette");
            }
            var menu = Get<NotifyIcon>(form, "tray").ContextMenuStrip;
            AssertColor(menu.BackColor, Theme.Panel, "Tray-menu surface follows palette");
            AssertColor(menu.ForeColor, Theme.TextPrimary, "Tray-menu foreground follows palette");
            var renderer = menu.Renderer as ToolStripProfessionalRenderer;
            Assert(renderer != null, "Tray menu has themed renderer");
            AssertColor(renderer.ColorTable.ToolStripDropDownBackground, Theme.Panel, "Tray-menu renderer follows palette");
            foreach (ToolStripItem item in menu.Items)
                AssertColor(item.ForeColor, Theme.TextPrimary, "Tray-menu item follows palette");
        }

        static void RenderForm(MainForm form, string name)
        {
            using (var bitmap = RenderControl(form))
            {
                AssertColor(bitmap.GetPixel(12, 80), Theme.Bg, name + " empty power-area background");
                AssertColor(bitmap.GetPixel(10, 205), Theme.Bg, name + " status-label background");
                AssertColor(bitmap.GetPixel(15, 280), Theme.Bg, name + " settings-label background");
                foreach (Control control in form.Controls)
                {
                    Rectangle paintedArea = control.Bounds;
                    paintedArea.Inflate(-4, -3);
                    Assert(CountNonBackground(bitmap, paintedArea) >= 8,
                        name + " includes " + control.GetType().Name + " visible text/glyphs");
                }
                bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            }
        }

        static int CountNonBackground(Bitmap bitmap, Rectangle area)
        {
            int count = 0;
            Rectangle visibleArea = Rectangle.Intersect(area, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            for (int y = visibleArea.Top; y < visibleArea.Bottom; y++)
                for (int x = visibleArea.Left; x < visibleArea.Right; x++)
                    if (bitmap.GetPixel(x, y).ToArgb() != Theme.Bg.ToArgb()) count++;
            return count;
        }

        static void RenderMenu(MainForm form, string name)
        {
            var menu = Get<NotifyIcon>(form, "tray").ContextMenuStrip;
            menu.Size = menu.PreferredSize;
            menu.PerformLayout();
            using (var bitmap = RenderControl(menu))
                bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        }

        static void CheckControlBackgrounds(MainForm form, string name)
        {
            foreach (Control control in form.Controls)
            {
                if (!(control is CustomControl)) continue;
                using (var bitmap = RenderControl(control))
                    AssertColor(bitmap.GetPixel(0, 0), Theme.Bg, name + " " + control.GetType().Name + " corner has no glass/black patch");
            }
        }

        static Bitmap RenderControl(Control control)
        {
            // Hidden forms do not lazily create their children's HWNDs. WM_PRINT
            // requires those handles to include labels and the first render's
            // custom controls, while the window itself remains hidden.
            EnsureHandles(control);
            var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppArgb);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            return bitmap;
        }

        static void EnsureHandles(Control control)
        {
            IntPtr unused = control.Handle;
            foreach (Control child in control.Controls) EnsureHandles(child);
        }

        static void SetActiveVisual(MainForm form, bool active)
        {
            var timer = Get<Timer>(form, "timer");
            timer.Stop();
            timer.Interval = Int32.MaxValue; // No nudge can fire during this render-only check.
            if (active) timer.Start();
            var power = Get<PowerButton>(form, "powerButton");
            power.Active = active;
            power.Pulse = active ? 0.7f : 0;
            Get<Label>(form, "status").Text = active ? "ACTIVE" : "IDLE";
            string[] switches = { "startupCheck", "topMostCheck", "idleCheck", "awakeCheck" };
            foreach (string name in switches)
            {
                var control = Get<ToggleSwitch>(form, name);
                bool on = name != "startupCheck";
                Set(control, "isChecked", on);
                Set(control, "pos", on ? 1f : 0f);
                Get<Timer>(control, "anim").Stop();
            }
        }

        static void SetHovers(MainForm form, bool hover)
        {
            foreach (Control control in form.Controls)
            {
                if (control is TitleButton || control is PillButton || control is PowerButton)
                    Invoke(control, hover ? "OnMouseEnter" : "OnMouseLeave", EventArgs.Empty);
                else if (control is Stepper)
                    Invoke(control, hover ? "OnMouseMove" : "OnMouseLeave",
                        hover ? (object)new MouseEventArgs(MouseButtons.None, 0, 6, 12, 0) : EventArgs.Empty);
            }
        }

        static TitleButton FindCloseButton(MainForm form)
        {
            foreach (Control control in form.Controls)
                if (control is TitleButton && Get<bool>(control, "isClose")) return (TitleButton)control;
            throw new InvalidOperationException("Close button missing");
        }

        static T Get<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        }

        static void Set(object instance, string name, object value)
        {
            instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
        }

        static void Invoke(object instance, string name, params object[] args)
        {
            instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, args);
        }

        static double Contrast(Color first, Color second)
        {
            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        static double Luminance(Color color)
        {
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        static double Linear(byte component)
        {
            double value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        static void AssertColor(Color actual, Color expected, string message)
        {
            Assert(actual.A == 255 && actual.ToArgb() == expected.ToArgb(),
                message + " (expected " + expected + ", got " + actual + ")");
        }

        static void Assert(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
        }
    }
}
