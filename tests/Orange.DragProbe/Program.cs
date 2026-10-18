using System.IO;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Forms = System.Windows.Forms;

// Orange.DragProbe <Orange.exe> <fixturesDir> <outDir>
// Scenario A: an OLE file drag started by this probe's own window (DoDragDrop), Shift tapped mid-drag, dropped on the "WebP" wedge.
// Scenario B: the same with a real File Explorer window as the drag source (UI Automation finds the file, SendInput drags it).
// Results: probe.json + screenshots. Exit code 0 only if every scenario that could run passed.

internal static class Probe
{
    private static string _events = "";
    private static readonly List<object> Results = new();

    [STAThread]
    private static int Main(string[] args)
    {
        var orangeExe = Path.GetFullPath(args[0]);
        var fixtures = Path.GetFullPath(args[1]);
        var outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outDir);
        _events = Path.Combine(outDir, "events.log");
        File.WriteAllText(_events, "");
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);

        // Settings for the test user: drags from any app count, no Explorer pop-ups or toasts to get in the way.
        var settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orange");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"),
            "{\"DragSources\":\"AnyApp\",\"RevealWhenDone\":false,\"NotifyWhenDone\":false,\"AskOptionsForTools\":false,\"PlaySounds\":false}");

        var psi = new ProcessStartInfo(orangeExe, "--background") { UseShellExecute = false };
        psi.Environment["ORANGE_TEST_EVENTS"] = _events;
        var orange = Process.Start(psi)!;
        try
        {
            if (!WaitFor("ready", 60_000)) { Record("startup", false, "Orange didn't report ready: " + Tail()); return Finish(outDir, 2); }
            Record("startup", true, File.ReadAllText(_events).Contains("hooks-installed") ? "hooks installed" : "ready but hooks not installed");

            ScenarioSelfDrag(fixtures, outDir);
            ScenarioExplorer(fixtures, outDir);
        }
        finally
        {
            try { orange.Kill(true); } catch { }
        }
        var failed = Results.Cast<dynamic>().Any(r => !(bool)r.passed && !(bool)r.skipped);
        return Finish(outDir, failed ? 1 : 0);
    }

    // ---------------- Scenario A
    private static void ScenarioSelfDrag(string fixtures, string outDir)
    {
        var work = Path.Combine(outDir, "a"); Directory.CreateDirectory(work);
        var file = Path.Combine(work, "drag-me.png");
        File.Copy(Path.Combine(fixtures, "sample.png"), file, true);
        var mark = EventCount();

        var form = new Forms.Form { Text = "Orange drag probe", StartPosition = Forms.FormStartPosition.Manual, Left = 120, Top = 120, Width = 360, Height = 240, TopMost = true };
        var label = new Forms.Label { Text = "drag-me.png", Dock = Forms.DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, BackColor = System.Drawing.Color.Bisque };
        form.Controls.Add(label);
        Forms.DragDropEffects effect = Forms.DragDropEffects.None;
        bool started = false;
        label.MouseMove += (_, e) =>
        {
            if (started || e.Button != Forms.MouseButtons.Left) return;
            started = true;
            var data = new Forms.DataObject(Forms.DataFormats.FileDrop, new[] { file });
            effect = label.DoDragDrop(data, Forms.DragDropEffects.Copy | Forms.DragDropEffects.Move | Forms.DragDropEffects.Link);
            form.Close();
        };
        string outcome = "not run";
        bool ok = false;
        form.Shown += (_, _) =>
        {
            form.Activate();
            var center = form.PointToScreen(new System.Drawing.Point(form.ClientSize.Width / 2, form.ClientSize.Height / 2));
            var t = new Thread(() =>
            {
                try { ok = DragToWedge(center.X, center.Y, "WebP", outDir, "a", out outcome); }
                catch (Exception e) { outcome = e.ToString(); }
                finally { Input.LeftUp(); }
            }) { IsBackground = true };
            t.Start();
        };
        Forms.Application.Run(form);
        var produced = Path.Combine(work, "drag-me.webp");
        var done = WaitFor("run-done image.to.webp", 30_000, mark);
        ok = ok && done && File.Exists(produced) && File.Exists(file);
        Record("A: OLE drag from a window, tap Shift, drop on WebP", ok,
            $"{outcome}; DoDragDrop effect={effect}; output exists={File.Exists(produced)}; source kept={File.Exists(file)}");
    }

    // ---------------- Scenario B
    private static void ScenarioExplorer(string fixtures, string outDir)
    {
        var work = Path.Combine(outDir, "explorer-source"); Directory.CreateDirectory(work);
        var file = Path.Combine(work, "explorer-file.png");
        File.Copy(Path.Combine(fixtures, "sample.png"), file, true);
        var mark = EventCount();
        Process.Start("explorer.exe", $"\"{work}\"");
        AutomationElement? item = null; AutomationElement? window = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20_000 && item is null)
        {
            Thread.Sleep(500);
            window = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass"))
                .Cast<AutomationElement>().FirstOrDefault(w => w.Current.Name.Contains("explorer-source", StringComparison.OrdinalIgnoreCase));
            item = window?.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().FirstOrDefault(i => i.Current.Name.StartsWith("explorer-file", StringComparison.OrdinalIgnoreCase));
        }
        if (item is null)
        {
            Shot(Path.Combine(outDir, "b-no-explorer.png"));
            var names = string.Join(" | ", AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().Select(e => $"{e.Current.ClassName}:{e.Current.Name}").Take(15));
            Record("B: real File Explorer drag, tap Shift, drop on WebP", false, $"Couldn't find the Explorer window or file via UI Automation. Top-level windows: {names}", skipped: true);
            return;
        }
        var hwnd = new IntPtr(window!.Current.NativeWindowHandle);
        Input.SetForegroundWindow(hwnd);
        Thread.Sleep(600);
        var r = item.Current.BoundingRectangle;
        int x = (int)(r.Left + Math.Min(40, r.Width / 2)), y = (int)(r.Top + r.Height / 2);
        string outcome;
        bool ok;
        try { ok = DragToWedge(x, y, "WebP", outDir, "b", out outcome); }
        finally { Input.LeftUp(); }
        var produced = Path.Combine(work, "explorer-file.webp");
        var done = WaitFor("run-done image.to.webp", 30_000, mark);
        var selection = File.ReadAllLines(_events).Skip(mark).FirstOrDefault(l => l.Contains("wheel-requested")) ?? "";
        ok = ok && done && File.Exists(produced) && File.Exists(file);
        Record("B: real File Explorer drag, tap Shift, drop on WebP", ok,
            $"{outcome}; {selection.Split(' ').Skip(1).DefaultIfEmpty("").Aggregate((a, b) => a + " " + b)}; output exists={File.Exists(produced)}; source kept despite Shift={File.Exists(file)}");
        try { Input.CloseWindow(hwnd); } catch { }
    }

    /// <summary>Presses at (x,y), drags past the threshold, taps Shift, waits for the wheel, moves onto the named wedge, releases.</summary>
    private static bool DragToWedge(int x, int y, string wedge, string outDir, string tag, out string outcome)
    {
        var mark = EventCount();
        Input.MoveTo(x, y); Thread.Sleep(250);
        Input.LeftDown(); Thread.Sleep(150);
        for (int i = 1; i <= 12; i++) { Input.MoveTo(x + i * 4, y + i * 3); Thread.Sleep(25); }
        Thread.Sleep(300);
        Input.TapShift();
        if (!WaitFor("wheel-shown", 5000, mark)) { Shot(Path.Combine(outDir, $"{tag}-no-wheel.png")); outcome = "wheel did not appear after Shift: " + Tail(); return false; }
        var line = File.ReadAllLines(_events).Skip(mark).First(l => l.Contains("wheel-shown"));
        var rect = Field(line, "rect").Split(',').Select(int.Parse).ToArray();
        // The wheel learns the dragged files on DragEnter (or from the Explorer selection): nudge the cursor and re-read.
        Native.GetCursorPos(out var c0);
        for (int i = 0; i < 8; i++) { Input.MoveTo(c0.X + (i % 2) * 3, c0.Y + (i % 2) * 2); Thread.Sleep(80); }
        Thread.Sleep(500);
        var kindLine = File.ReadAllLines(_events).Skip(mark).LastOrDefault(l => l.Contains("wheel-kind")) ?? line;
        var items = Field(kindLine, "items").Split('|');
        int idx = Array.IndexOf(items, wedge);
        if (idx < 0) { outcome = $"wedge {wedge} not in [{string.Join(",", items)}]"; return false; }
        int cx = (rect[0] + rect[2]) / 2, cy = (rect[1] + rect[3]) / 2, d = rect[2] - rect[0];
        double rr = (0.22 * d + (0.5 * d - 4)) / 2, ang = (-90 + idx * 360.0 / items.Length) * Math.PI / 180;
        int tx = cx + (int)(rr * Math.Cos(ang)), ty = cy + (int)(rr * Math.Sin(ang));
        Native.GetCursorPos(out var cur);
        for (int i = 1; i <= 20; i++) { Input.MoveTo(cur.X + (tx - cur.X) * i / 20, cur.Y + (ty - cur.Y) * i / 20); Thread.Sleep(30); }
        for (int i = 0; i < 6; i++) { Input.MoveTo(tx + (i % 2), ty); Thread.Sleep(60); }   // let DragOver fire on the wedge
        Shot(Path.Combine(outDir, $"{tag}-wheel-over-{wedge}.png"));
        var highlighted = File.ReadAllLines(_events).Skip(mark).LastOrDefault(l => l.Contains(" highlight ")) ?? "none";
        Input.LeftUp();
        bool dropped = WaitFor("drop index=", 5000, mark);
        outcome = $"wheel rect={string.Join(",", rect)} items={string.Join(",", items)} target={idx} last {highlighted.Split(' ').Last()} drop={(dropped ? File.ReadAllLines(_events).Skip(mark).First(l => l.Contains("drop index=")).Split(' ').Last() : "none")}";
        return dropped;
    }

    // ---------------- helpers
    private static string Field(string line, string name) => line.Split(' ').First(p => p.StartsWith(name + "="))[(name.Length + 1)..];
    private static int EventCount() { try { return File.ReadAllLines(_events).Length; } catch { return 0; } }
    private static string Tail() { try { return string.Join(" / ", File.ReadAllLines(_events).TakeLast(6)); } catch { return ""; } }
    private static bool WaitFor(string text, int ms, int fromLine = 0)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            try { if (File.ReadAllLines(_events).Skip(fromLine).Any(l => l.Contains(text))) return true; } catch (IOException) { }
            Thread.Sleep(100);
        }
        return false;
    }
    private static void Record(string name, bool passed, string detail, bool skipped = false) =>
        Results.Add(new { name, passed, skipped, detail });
    private static int Finish(string outDir, int code)
    {
        File.WriteAllText(Path.Combine(outDir, "probe.json"), JsonSerializer.Serialize(new { os = Environment.OSVersion.VersionString, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(Path.Combine(outDir, "probe.json")));
        return code;
    }
    private static void Shot(string path)
    {
        try
        {
            var b = Forms.SystemInformation.VirtualScreen;
            using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
            bmp.Save(path, ImageFormat.Png);
        }
        catch { }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
}

/// <summary>Real input through SendInput, the same path a physical mouse and keyboard take.</summary>
internal static class Input
{
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION u; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static void CloseWindow(IntPtr h) => SendMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);

    private static void Mouse(uint flags) => SendInput(1, [new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dwFlags = flags } } }], Marshal.SizeOf<INPUT>());
    private static void Key(ushort vk, bool up) => SendInput(1, [new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { vk = vk, flags = up ? 2u : 0u } } }], Marshal.SizeOf<INPUT>());
    public static void MoveTo(int x, int y) { SetCursorPos(x, y); Mouse(0x0001 /*MOVE*/); }
    public static void LeftDown() => Mouse(0x0002);
    public static void LeftUp() => Mouse(0x0004);
    public static void TapShift() { Key(0x10, false); Thread.Sleep(80); Key(0x10, true); }
}
