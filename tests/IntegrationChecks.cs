using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MouseJigglerApp
{
    // Verify-Integration.ps1 adapts only a private copy of the production source.
    // The real IPC server, UI dispatch, command handler and state updates remain.
    static class IntegrationChecks
    {
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr window);

        internal static string DescriptorPath;
        internal static int ToggleCalls, InputCalls, WakeCalls, HotkeyRegistrationCalls;
        static string outputDirectory, pipeName;
        static Control dispatcher;
        static MainForm currentForm;
        static bool hasOpened;
        static int checks;
        static Exception failure;
        static readonly List<object> results = new List<object>();
        static readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        sealed class State
        {
            public bool Active, Animation, Visible, NativeVisible, TrayVisible, PowerActive;
            public string Status, TrayText;
            public int Toggles;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length != 3) return 2;
            outputDirectory = Path.GetFullPath(args[0]);
            string pipePrefix = args[1];
            DescriptorPath = Path.GetFullPath(args[2]);
            string settingsRoot = (string)typeof(MainForm).GetField("RegRoot", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            if (!settingsRoot.StartsWith(@"Software\MouseJigglerIntegrationChecks\", StringComparison.Ordinal) ||
                !pipePrefix.StartsWith("MouseJiggler.IntegrationChecks.", StringComparison.Ordinal) ||
                !DescriptorPath.StartsWith(outputDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Isolation guard failed. Run Verify-Integration.ps1 only.");
                return 2;
            }
            pipeName = pipePrefix + WindowsIdentity.GetCurrent().User.Value + "." + Process.GetCurrentProcess().SessionId;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(outputDirectory);
            Program.StreamDeckEnabled = true;
            Task worker = null;
            using (dispatcher = new Control())
            using (var context = new ApplicationContext())
            using (var watchdog = new System.Windows.Forms.Timer { Interval = 45000 })
            {
                IntPtr unused = dispatcher.Handle;
                watchdog.Tick += delegate
                {
                    failure = new TimeoutException("Integration fixture exceeded its 45-second safety deadline.");
                    context.ExitThread();
                };
                watchdog.Start();
                worker = Task.Factory.StartNew(delegate
                {
                    try { RunChecks(); }
                    catch (Exception error) { failure = error; }
                    finally
                    {
                        try { dispatcher.BeginInvoke(new MethodInvoker(context.ExitThread)); }
                        catch (InvalidOperationException) { }
                    }
                });
                try { Application.Run(context); }
                finally
                {
                    watchdog.Stop();
                    if (currentForm != null) { currentForm.Dispose(); currentForm = null; }
                    Program.StreamDeckEnabled = false;
                }
            }
            if (worker != null && !worker.IsCompleted) worker.Wait(1000);
            if (failure != null) Record("Fixture completed without unexpected error", false, failure.ToString());
            string report = serializer.Serialize(new {
                utc = DateTime.UtcNow.ToString("o"), source = "isolated production source copy",
                actualNamedPipeRoundtrips = true, externalInput = false, realHotkeys = false,
                realWakeRequests = false, realTrayIcon = false, productionSettings = false,
                pipeName = pipeName, descriptorPath = DescriptorPath, checks = results,
                stubInputCalls = InputCalls, stubWakeCalls = WakeCalls,
                stubHotkeyRegistrations = HotkeyRegistrationCalls,
                failures = failure == null ? 0 : 1
            });
            File.WriteAllText(Path.Combine(outputDirectory, "integration-report.json"), report);
            Console.WriteLine((failure == null ? "PASS: " : "FAIL: ") + checks + " integration checks.");
            return failure == null ? 0 : 1;
        }

        static void RunChecks()
        {
            CheckStartupMode(false, false, false, "Normal");
            CheckStartupMode(false, true, false, "Background");
            CheckStartupMode(true, false, true, "Silent");
            OpenForm(false, true);
            try
            {
                CheckCommands();
                CheckExternalStateChanges();
                CheckMalformedAndBoundedRequests();
                CheckUiDispatchDeadline();
                CheckConcurrentReaders();
                Assert(InputCalls == 0, "IPC never injects mouse input before the configured first interval");
                Assert(HotkeyRegistrationCalls > 0, "Native hotkey registration is replaced by the isolated stub");
                Assert(WakeCalls > 0, "Active state uses the isolated wake-request stub");
                State final = Snapshot();
                Assert(!final.Visible && !final.NativeVisible && !final.TrayVisible, "IPC keeps the background fixture and tray icon hidden");
            }
            finally { CloseForm(); }
            Assert(!CanConnect(200), "Disposed server no longer accepts its old endpoint");
            OpenForm(false, true);
            try { Assert(!Success("status"), "A replacement server owns the same private endpoint and starts IDLE"); }
            finally { CloseForm(); }
        }

        static void CheckStartupMode(bool silent, bool background, bool expectedActive, string name)
        {
            int togglesBefore = OnUi(delegate { return ToggleCalls; });
            OpenForm(silent, background);
            try
            {
                if (silent || background)
                    OnUi(delegate { Invoke(currentForm, "SetVisibleCore", true); });
                State state = Snapshot();
                Assert(state.Active == expectedActive, name + " startup has the expected timer state");
                Assert(state.Toggles == togglesBefore + (silent ? 1 : 0), name + " initializes activity exactly once");
                Assert(!state.Visible && !state.NativeVisible && !state.TrayVisible, name + " fixture stays hidden without a real tray icon");
                Assert(Success("status") == expectedActive, name + " status reports the real startup state over the pipe");
                AssertUiState(expectedActive, name + " startup UI state");
                OnUi(delegate { Invoke(currentForm, "OnLoad", EventArgs.Empty); Invoke(currentForm, "OnLoad", EventArgs.Empty); });
                Assert(Success("status") == expectedActive && Snapshot().Toggles == state.Toggles,
                    name + " later Load events never retoggle startup");
                CheckDescriptor();
            }
            finally { CloseForm(); }
        }

        static void OpenForm(bool silent, bool background)
        {
            if (!hasOpened) Assert(!File.Exists(DescriptorPath), "The private descriptor path starts empty");
            else
            {
                Assert(File.Exists(DescriptorPath) && !CanConnect(200), "A retained descriptor is only a launch hint after shutdown");
                // Simulate an older/stale descriptor. Construction must replace
                // it atomically rather than treating its mere presence as live.
                File.WriteAllText(DescriptorPath, "{\"protocolVersion\":0,\"stale\":true}");
            }
            OnUi(delegate { currentForm = new MainForm(silent, background); });
            WaitUntil(delegate { return File.Exists(DescriptorPath); }, 4000, "Server publishes a descriptor");
            Assert(CanConnect(2000), "The descriptor's private pipe is reachable");
            CheckDescriptor();
            hasOpened = true;
        }

        static void CloseForm()
        {
            Thread worker = OnUi(delegate { return Get<Thread>(Get<StreamDeckServer>(currentForm, "streamDeckServer"), "worker"); });
            OnUi(delegate { currentForm.Dispose(); currentForm = null; });
            Assert(worker.Join(4000), "Disposal terminates the listener even while waiting for a client");
            Assert(File.Exists(DescriptorPath), "Disposal retains the private descriptor as a launch hint");
            Assert(!CanConnect(200), "A retained descriptor does not leave a live pipe after disposal");
        }

        static void CheckDescriptor()
        {
            var descriptor = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(DescriptorPath));
            Assert(Convert.ToInt32(descriptor["protocolVersion"]) == 1, "Descriptor declares protocol version 1");
            Assert((string)descriptor["pipeName"] == pipeName, "Descriptor pipe is scoped to the isolated prefix, current SID and session");
            Assert(Convert.ToInt32(descriptor["processId"]) == Process.GetCurrentProcess().Id, "Descriptor identifies the actual server process");
            Assert(string.Equals(Path.GetFullPath((string)descriptor["executablePath"]), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase),
                "Descriptor names the actual executable hosting the server");
        }

        static void CheckCommands()
        {
            int before = Snapshot().Toggles;
            Assert(!Success("status"), "Status begins IDLE");
            Assert(Snapshot().Toggles == before, "Status never changes activity");
            Assert(!Success("off") && Snapshot().Toggles == before, "Off is idempotent while IDLE");
            Assert(Success("on"), "On starts activity through the pipe");
            AssertUiState(true, "On updates real UI and tray state");
            Assert(Snapshot().Toggles == before + 1, "On performs one state transition");
            Assert(Success("on") && Snapshot().Toggles == before + 1, "Repeated On does not stop or restart activity");
            Assert(!Success("off"), "Off stops activity through the pipe");
            AssertUiState(false, "Off updates real UI and tray state");
            Assert(!Success("off") && Snapshot().Toggles == before + 2, "Repeated Off performs no extra transition");
            Assert(Success("toggle") && Snapshot().Toggles == before + 3, "Toggle from IDLE executes exactly once");
            Assert(!Success("toggle") && Snapshot().Toggles == before + 4, "Toggle from ACTIVE executes exactly once");
        }

        static void CheckExternalStateChanges()
        {
            OnUi(delegate { Invoke(Get<Control>(currentForm, "powerButton"), "OnClick", EventArgs.Empty); });
            Assert(Success("status"), "Status observes an independent power-button click");
            AssertUiState(true, "Power-button state is consistent");
            OnUi(delegate
            {
                foreach (ToolStripItem item in Get<NotifyIcon>(currentForm, "tray").ContextMenuStrip.Items)
                    if (item.Text == "Toggle") { ((ToolStripMenuItem)item).PerformClick(); return; }
                throw new InvalidOperationException("Tray toggle action was not found.");
            });
            Assert(!Success("status"), "Status observes an independent tray toggle");
            OnUi(delegate
            {
                int id = (int)typeof(MainForm).GetField("HOTKEY_ID", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
                SendMessage(currentForm.Handle, 0x0312, (IntPtr)id, IntPtr.Zero);
            });
            Assert(Success("status"), "Status observes the real WM_HOTKEY handler without keyboard input");
            Assert(!Success("off"), "Pipe can stop activity started outside the pipe");
        }

        static void CheckMalformedAndBoundedRequests()
        {
            int before = Snapshot().Toggles;
            foreach (string command in new[] { "", "unknown", "{\"command\":\"on\"}", "on please" })
            {
                var response = Exchange(Encoding.UTF8.GetBytes(command + "\n"), false, 6000);
                AssertError(response, "Malformed command is rejected: " + command);
            }
            var oversized = Exchange(Encoding.UTF8.GetBytes(new string('x', 1024) + "\n"), true, 6000);
            if (oversized != null) AssertError(oversized, "Oversized command receives a bounded error");
            Assert(!Success("status"), "Server remains reachable after malformed/oversized requests");
            var invalidUtf8 = Exchange(new byte[] { 0xFF, 0xFE, 0xFF, 0x0A }, true, 6000);
            if (invalidUtf8 != null) AssertError(invalidUtf8, "Invalid UTF-8 receives an error");
            Assert(!Success("status"), "Server remains reachable after invalid bytes");
            var watch = Stopwatch.StartNew();
            using (var stalled = Connect(2000))
            {
                var stalledReply = ReadReply(stalled, true, 6000);
                if (stalledReply != null) AssertError(stalledReply, "A client without a newline receives an error");
            }
            Assert(watch.ElapsedMilliseconds < 6000, "A connected client cannot stall the server beyond its request deadline");
            Assert(!Success("status"), "A fresh client succeeds after a stalled connection");
            var firstOnly = Exchange(Encoding.UTF8.GetBytes("off\non\n"), false, 6000);
            Assert(!ValidateSuccess(firstOnly), "One connection acknowledges its first request");
            Assert(!Success("status"), "Extra buffered commands on one connection do not execute");
            Assert(Snapshot().Toggles == before, "Rejected, incomplete and extra commands never change activity");
        }

        static void CheckUiDispatchDeadline()
        {
            int before = Snapshot().Toggles;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                dispatcher.BeginInvoke(new MethodInvoker(delegate { entered.Set(); release.WaitOne(5000); }));
                Assert(entered.WaitOne(2000), "The isolated UI deadline fixture started");
                try
                {
                    var watch = Stopwatch.StartNew();
                    AssertError(Exchange(Encoding.UTF8.GetBytes("on\n"), false, 6000), "An unavailable UI returns an error instead of claiming success");
                    Assert(watch.ElapsedMilliseconds < 6000, "UI dispatch has a bounded deadline");
                }
                finally { release.Set(); }
            }
            Assert(!Success("status") && Snapshot().Toggles == before, "A timed-out queued command never executes late after the UI resumes");
        }

        static void CheckConcurrentReaders()
        {
            var tasks = new List<Task<bool>>();
            for (int index = 0; index < 6; index++) tasks.Add(Task.Factory.StartNew(delegate { return ValidateSuccess(Exchange(Encoding.UTF8.GetBytes("status\n"), false, 6000)); }));
            Assert(Task.WaitAll(tasks.ToArray(), 10000), "A bounded burst of independent pipe clients completes");
            foreach (Task<bool> task in tasks) Assert(!task.Result, "Concurrent status clients read the same actual IDLE state");
        }

        static bool Success(string command)
        {
            return ValidateSuccess(Exchange(Encoding.UTF8.GetBytes(command + "\n"), false, 6000));
        }

        static bool ValidateSuccess(Dictionary<string, object> response)
        {
            Assert(response != null && response.ContainsKey("ok") && response["ok"] is bool && (bool)response["ok"], "Pipe response acknowledges a successful request");
            Assert(Convert.ToInt32(response["protocolVersion"]) == 1 && response["running"] is bool && (bool)response["running"], "Pipe response declares a running protocol version 1 server");
            Assert(Convert.ToInt32(response["processId"]) == Process.GetCurrentProcess().Id, "Pipe response identifies its actual process");
            Assert(response["active"] is bool, "Pipe active state is a JSON Boolean");
            return (bool)response["active"];
        }

        static void AssertError(Dictionary<string, object> response, string message)
        {
            Assert(response != null && response.ContainsKey("ok") && response["ok"] is bool && !(bool)response["ok"] &&
                response.ContainsKey("error") && response["error"] is string && !string.IsNullOrWhiteSpace((string)response["error"]), message);
        }

        static NamedPipeClientStream Connect(int timeout)
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try { client.Connect(timeout); return client; }
            catch { client.Dispose(); throw; }
        }

        static bool CanConnect(int timeout)
        {
            try { using (var client = Connect(timeout)) return client.IsConnected; }
            catch (TimeoutException) { return false; }
            catch (IOException) { return false; }
        }

        static Dictionary<string, object> Exchange(byte[] request, bool allowDisconnect, int timeout)
        {
            using (var client = Connect(timeout))
            {
                try { client.Write(request, 0, request.Length); client.Flush(); }
                catch (IOException) { if (allowDisconnect) return null; throw; }
                return ReadReply(client, allowDisconnect, timeout);
            }
        }

        static Dictionary<string, object> ReadReply(NamedPipeClientStream client, bool allowDisconnect, int timeout)
        {
            var data = new List<byte>();
            var watch = Stopwatch.StartNew();
            try
            {
                while (data.Count < 4096)
                {
                    int remaining = timeout - (int)watch.ElapsedMilliseconds;
                    if (remaining <= 0) throw new TimeoutException("Pipe reply exceeded its fixture deadline.");
                    var one = new byte[1];
                    Task<int> read = client.ReadAsync(one, 0, 1);
                    if (!read.Wait(remaining)) throw new TimeoutException("Pipe reply exceeded its fixture deadline.");
                    if (read.Result == 0)
                    {
                        if (allowDisconnect && data.Count == 0) return null;
                        throw new IOException("Pipe closed without a newline-delimited JSON response.");
                    }
                    if (one[0] == 10)
                        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(data.ToArray()).TrimEnd('\r'));
                    data.Add(one[0]);
                }
                throw new InvalidOperationException("Pipe response exceeded the 4096-byte test bound.");
            }
            catch (IOException) { if (allowDisconnect && data.Count == 0) return null; throw; }
            catch (AggregateException error)
            {
                if (allowDisconnect && data.Count == 0 && error.GetBaseException() is IOException) return null;
                throw;
            }
        }

        static State Snapshot()
        {
            return OnUi(delegate
            {
                var tray = Get<NotifyIcon>(currentForm, "tray");
                return new State {
                    Active = Get<System.Windows.Forms.Timer>(currentForm, "timer").Enabled,
                    Animation = Get<System.Windows.Forms.Timer>(currentForm, "animTimer").Enabled,
                    Visible = currentForm.Visible, NativeVisible = IsWindowVisible(currentForm.Handle),
                    TrayVisible = tray.Visible, Status = Get<Label>(currentForm, "status").Text,
                    TrayText = tray.Text, PowerActive = Get<PowerButton>(currentForm, "powerButton").Active,
                    Toggles = ToggleCalls
                };
            });
        }

        static void AssertUiState(bool active, string message)
        {
            State state = Snapshot();
            Assert(state.Active == active && state.Animation == active && state.PowerActive == active &&
                state.Status == (active ? "ACTIVE" : "IDLE") && state.TrayText == (active ? "Mouse Jiggler (ON)" : "Mouse Jiggler (OFF)"), message);
        }

        static T OnUi<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>();
            dispatcher.BeginInvoke(new MethodInvoker(delegate
            {
                try { completion.SetResult(action()); }
                catch (Exception error) { completion.SetException(error); }
            }));
            if (!completion.Task.Wait(7000)) throw new TimeoutException("Isolated UI action did not complete.");
            return completion.Task.Result;
        }

        static void OnUi(Action action) { OnUi(delegate { action(); return true; }); }
        static T Get<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }
        static void Invoke(object instance, string name, params object[] args)
        {
            instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, args);
        }
        static void WaitUntil(Func<bool> predicate, int timeout, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.ElapsedMilliseconds < timeout) Thread.Sleep(20);
            Assert(predicate(), message);
        }
        static void Assert(bool condition, string message)
        {
            Record(message, condition, null);
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
        }
        static void Record(string message, bool passed, string details)
        {
            lock (results)
            {
                checks++;
                results.Add(new { name = message, passed = passed, details = details });
                Console.WriteLine((passed ? "PASS " : "FAIL ") + message + (details == null ? "" : " — " + details));
            }
        }
    }
}
