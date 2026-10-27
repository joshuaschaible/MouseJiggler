using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseJigglerApp
{
    // Runs the actual app constructor/message loop with native side effects and
    // registry paths isolated by Verify-Startup.ps1 before compilation.
    static class StartupChecks
    {
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr window);

        internal static int ToggleCalls;
        static int checks;

        [STAThread]
        static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                CheckSilentStartup();
                CheckNormalStartup();
                Console.WriteLine("PASS: " + checks + " startup checks on .NET " + Environment.Version);
                Console.WriteLine("Silent startup activates once before Load, stays hidden through the message loop, and later Load cannot retoggle. Normal startup remains IDLE.");
                Console.WriteLine("Test copy uses isolated settings and no-op mouse input, global shortcuts, execution-state requests, and tray visibility.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        static void CheckSilentStartup()
        {
            Exception failure = null;
            int callsBefore = ToggleCalls;
            using (var form = new MainForm(true))
            using (var probe = new Timer { Interval = 40 })
            {
                Assert(!form.Visible && !IsWindowVisible(form.Handle), "Silent construction never shows the window");
                Assert(!Get<NotifyIcon>(form, "tray").Visible, "Isolated test does not add a tray icon");
                probe.Tick += delegate
                {
                    probe.Stop();
                    try
                    {
                        Assert(!form.Visible && !IsWindowVisible(form.Handle), "Silent message-loop startup stays natively hidden");
                        Assert(Get<Timer>(form, "timer").Enabled, "Silent startup jiggles before the first Load event");
                        Assert(Get<Timer>(form, "animTimer").Enabled, "Silent startup animates its active status");
                        Assert(Get<Label>(form, "status").Text == "ACTIVE", "Silent startup status is ACTIVE");
                        Assert(ToggleCalls == callsBefore + 1, "Silent startup toggles exactly once");
                        Assert(Get<NotifyIcon>(form, "tray").Text == "Mouse Jiggler (ON)", "Silent startup tray status is ON");

                        // Exercise the actual event that a later first tray show
                        // raises, without ever exposing a desktop window.
                        Invoke(form, "OnLoad", EventArgs.Empty);
                        Assert(Get<Timer>(form, "timer").Enabled, "First later Load preserves ACTIVE");
                        Assert(ToggleCalls == callsBefore + 1, "First later Load does not toggle again");
                        Invoke(form, "OnLoad", EventArgs.Empty);
                        Assert(ToggleCalls == callsBefore + 1, "Repeated Load does not toggle again");
                        Assert(!IsWindowVisible(form.Handle), "Load checks do not show the window");

                        // A user's later stop must also survive a Load event.
                        Invoke(form, "Toggle");
                        Assert(!Get<Timer>(form, "timer").Enabled, "User can stop silent-started jiggling");
                        Invoke(form, "OnLoad", EventArgs.Empty);
                        Assert(!Get<Timer>(form, "timer").Enabled, "Later Load preserves user's IDLE choice");
                        Assert(ToggleCalls == callsBefore + 2, "Only startup and the explicit user action toggle");
                    }
                    catch (Exception error) { failure = error; }
                    finally { Application.ExitThread(); }
                };
                probe.Start();
                // This calls MainForm.SetVisibleCore(true), just as production
                // Application.Run does, and verifies its initial-show suppression.
                Application.Run(form);
                if (failure != null) throw failure;
            }
        }

        static void CheckNormalStartup()
        {
            int callsBefore = ToggleCalls;
            using (var form = new MainForm(false))
            {
                Assert(!Get<Timer>(form, "timer").Enabled, "Normal construction starts IDLE");
                Assert(!Get<Timer>(form, "animTimer").Enabled, "Normal construction leaves animation stopped");
                Assert(Get<Label>(form, "status").Text == "IDLE", "Normal status is IDLE");
                Assert(Get<NotifyIcon>(form, "tray").Text == "Mouse Jiggler (OFF)", "Normal tray status is OFF");
                Invoke(form, "OnLoad", EventArgs.Empty);
                Assert(!Get<Timer>(form, "timer").Enabled, "Normal first Load remains IDLE");
                Assert(ToggleCalls == callsBefore, "Normal construction and Load never toggle");
                Assert(!IsWindowVisible(form.Handle), "Normal state test does not show a desktop window");
            }
        }

        static T Get<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        }

        static void Invoke(object instance, string name, params object[] arguments)
        {
            instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, arguments);
        }

        static void Assert(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
        }
    }
}
