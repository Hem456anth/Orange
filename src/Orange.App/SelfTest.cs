using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Orange.App.Interop;
using Orange.App.Services;
using Orange.App.Views;
using Orange.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Orange.App;

/// <summary>
/// "Orange.exe --selftest &lt;outDir&gt;": exercises the real Windows pieces in-process on the machine it runs on —
/// hook installation, hotkey registration, Explorer integration (registry + Send To), the wheel window and its
/// context-aware items, screenshots of the wheel in light and dark theme, Explorer selection reading, and
/// end-to-end conversions through the same service the wheel uses. Writes selftest.json; exit code 0 = all required passed.
/// </summary>
internal static class Inject
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct IN { public uint type; public MI mi; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint SendInput(uint n, IN[] i, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    public static void Move(int x, int y) { SetCursorPos(x, y); SendInput(1, [new IN { type = 0, mi = new MI { flags = 1 } }], System.Runtime.InteropServices.Marshal.SizeOf<IN>()); }
}

public static class SelfTest
{
    private sealed record Check(string Name, bool Passed, bool Required, string Detail);

    public static async Task<int> RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var checks = new List<Check>();
        var fixtures = Environment.GetEnvironmentVariable("ORANGE_FIXTURES") ?? FindFixtures();
        var work = Path.Combine(outDir, "work");
        Directory.CreateDirectory(work);
        foreach (var f in new[] { "sample.png", "sample.jpg", "sample.mp4", "sample.wav", "sample.pdf", "with-image.pdf", "sample.docx", "sample-alpha.png", "sample-meta.jpg", "sample.mp3", "sample.svg", "sample.xlsx", "sample.pptx", "sample.epub", "sample.csv", "qr.png" })
            if (File.Exists(Path.Combine(fixtures, f))) File.Copy(Path.Combine(fixtures, f), Path.Combine(work, f), true);
        string W(string n) => Path.Combine(work, n);
        AppSettings.Current.RevealWhenDone = false;
        AppSettings.Current.NotifyWhenDone = false;
        AppSettings.Current.AskOptionsForTools = false;
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        async Task Try(string name, bool required, Func<Task<string>> body)
        {
            try { var d = await body(); checks.Add(new Check(name, true, required, d)); }
            catch (Exception e) { checks.Add(new Check(name, false, required, e.ToString())); }
            await File.WriteAllTextAsync(Path.Combine(outDir, "selftest-progress.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }

        await Try("low-level mouse+keyboard hooks install", true, () =>
        {
            using var w = new DragWatcher(ui); w.Start(); return Task.FromResult("SetWindowsHookEx WH_MOUSE_LL/WH_KEYBOARD_LL ok");
        });
        await Try("hooks receive injected mouse input (needs an active input desktop)", false, async () =>
        {
            using var w = new DragWatcher(ui); w.Start();
            Native.GetCursorPos(out var p0);
            for (int i = 0; i < 10; i++) { Inject.Move(p0.X + i * 5, p0.Y + i * 3); await Task.Delay(30); }
            await Task.Delay(300);
            if (w.EventsSeen == 0) throw new Exception("hook installed but received no mouse events");
            return $"{w.EventsSeen} events";
        });
        await Try("global hotkey registers", false, () =>
        {
            using var t = new TrayAndHotkey();
            if (!t.RegisterHotkey("Ctrl+Alt+Shift+F9")) throw new Exception(t.HotkeyError);
            return Task.FromResult("RegisterHotKey ok");
        });
        await Try("tray icon", false, () => { using var t = new TrayAndHotkey(); t.ShowTray(); return Task.FromResult("Shell_NotifyIcon ok"); });
        await Try("context menu verb (HKCU) install/remove", true, () =>
        {
            ExplorerIntegration.SetContextMenu(true);
            if (!ExplorerIntegration.ContextMenuInstalled) throw new Exception("not found after install");
            var cmd = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\Orange\command")?.GetValue("") as string;
            ExplorerIntegration.SetContextMenu(false);
            if (ExplorerIntegration.ContextMenuInstalled) throw new Exception("still present after removal");
            return Task.FromResult(cmd ?? "");
        });
        await Try("Send To shortcut install/remove", true, () =>
        {
            ExplorerIntegration.SetSendTo(true);
            if (!ExplorerIntegration.SendToInstalled) throw new Exception("shortcut missing");
            ExplorerIntegration.SetSendTo(false);
            return Task.FromResult("ok");
        });

        await Try("wheel: PNG convert items", true, async () =>
        {
            var w = WheelWindow.Open(WheelKind.Convert, [W("sample.png")], Center(), dragMode: false, null);
            await Task.Delay(700);
            var titles = w.Wheel.Items.Select(i => i.Title).ToList();
            await Shot(w, Path.Combine(outDir, "wheel-png-convert.png"));
            w.Wheel.SetHighlight(1);
            await Task.Delay(150);
            await Shot(w, Path.Combine(outDir, "wheel-png-convert-highlight.png"));
            w.SetKind(WheelKind.Tools); await Task.Delay(300);
            await Shot(w, Path.Combine(outDir, "wheel-png-tools.png"));
            var tools = w.Wheel.Items.Select(i => i.Title).ToList();
            w.SetKind(WheelKind.Actions); await Task.Delay(300);
            await Shot(w, Path.Combine(outDir, "wheel-actions.png"));
            ((FrameworkElement)w.Content).RequestedTheme = ElementTheme.Dark; w.SetKind(WheelKind.Convert); await Task.Delay(300);
            await Shot(w, Path.Combine(outDir, "wheel-png-convert-dark.png"));
            ((FrameworkElement)w.Content).RequestedTheme = ElementTheme.Light;
            w.Wheel.SetHint("Shift: Convert\nShift twice: Tools\nShift+Ctrl: Actions"); await Task.Delay(200);
            await Shot(w, Path.Combine(outDir, "wheel-key-hint.png"));
            w.Wheel.SetHint(null);
            w.Close();
            if (!titles.Contains("WebP") || titles.Contains("PNG")) throw new Exception("unexpected items: " + string.Join(",", titles));
            return "convert=" + string.Join(",", titles) + " tools=" + string.Join(",", tools);
        });
        await Try("wheel: hit-testing maps wedge centres back to wedges", true, async () =>
        {
            var w = WheelWindow.Open(WheelKind.Convert, [W("sample.png")], Center(), dragMode: false, null);
            await Task.Delay(400);
            Native.GetWindowRect(w.Hwnd, out var r);
            double scale = Native.GetDpiForWindow(w.Hwnd) / 96.0;
            for (int i = 0; i < w.Wheel.Items.Count; i++)
            {
                var p = w.WedgeCenterOnScreen(i);
                var local = new Windows.Foundation.Point((p.X - r.Left) / scale, (p.Y - r.Top) / scale);
                if (w.Wheel.HitTest(local) != i) { w.Close(); throw new Exception($"wedge {i} hit {w.Wheel.HitTest(local)}"); }
            }
            w.Close();
            return $"{w.Wheel.Items.Count} wedges";
        });
        await Try("wheel: MP4 and two-PDF selections are context-aware", true, async () =>
        {
            var v = WheelWindow.Open(WheelKind.Convert, [W("sample.mp4")], Center(), false, null); await Task.Delay(500);
            await Shot(v, Path.Combine(outDir, "wheel-mp4-convert.png"));
            var vt = v.Wheel.Items.Select(i => i.Title).ToList(); v.Close();
            var p = WheelWindow.Open(WheelKind.Tools, [W("sample.pdf"), W("with-image.pdf")], Center(), false, null); await Task.Delay(500);
            await Shot(p, Path.Combine(outDir, "wheel-2pdf-tools.png"));
            var pt = p.Wheel.Items.Select(i => i.Title).ToList(); p.Close();
            if (!pt.Contains("Merge PDFs")) throw new Exception("Merge PDFs missing: " + string.Join(",", pt));
            return "mp4=" + string.Join(",", vt) + " | 2 pdf tools=" + string.Join(",", pt);
        });

        await Try("main window renders (convert, settings, about)", true, async () =>
        {
            var m = new MainWindow();
            m.ShowWindow();
            await Task.Delay(1200);
            await Shot(m, Path.Combine(outDir, "main-convert.png"));
            m.ShowConvert([W("sample.png")], "");
            await Task.Delay(800);
            await Shot(m, Path.Combine(outDir, "main-convert-png.png"));
            m.ShowPage("settings"); await Task.Delay(800);
            await Shot(m, Path.Combine(outDir, "main-settings.png"));
            m.ShowPage("about"); await Task.Delay(500);
            await Shot(m, Path.Combine(outDir, "main-about.png"));
            foreach (var sec in new[] { "Output", "Presets", "Engines", "Language", "Updates", "Privacy" })
            {
                m.ShowSettings(sec); await Task.Delay(700);
                await Shot(m, Path.Combine(outDir, $"settings-{sec.ToLowerInvariant()}.png"));
            }
            m.AppWindow.Hide();
            return "ok";
        });
        await Try("localisation: Hindi and Telugu resources load from resources.pri", true, async () =>
        {
            var before = L.Language;
            L.Init("hi-IN"); var hi = L.T("Settings");
            L.Init("te-IN"); var te = L.T("Settings");
            var m = new MainWindow(); m.ShowWindow(); m.ShowSettings("Activation"); await Task.Delay(900);
            await Shot(m, Path.Combine(outDir, "settings-telugu.png"));
            m.AppWindow.Hide();
            L.Init(before);
            if (hi != "सेटिंग्स" || te != "సెట్టింగ్‌లు") throw new Exception($"got '{hi}' / '{te}'");
            return $"hi={hi} te={te}";
        });
        await Try("options sheet renders", true, async () =>
        {
            var t = OptionsWindow.AskAsync(ConversionService.Instance.Registry.Get("video.trim"), [W("sample.mp4")], new Dictionary<string, string>());
            await Task.Delay(1200);
            if (OptionsWindow.Last is { } ow) { await Shot(ow, Path.Combine(outDir, "options-trim.png")); ow.Close(); }
            await Task.WhenAny(t, Task.Delay(1000));
            return "ok";
        });

        foreach (var (opId, fixture) in new[]
        {
            ("image.edit", "sample.png"), ("image.add-background", "sample-alpha.png"), ("image.redact", "sample.png"), ("image.annotate", "sample.png"),
            ("image.crop", "sample.png"), ("image.metadata", "sample-meta.jpg"), ("pdf.reorder", "sample.pdf"),
            ("audio.trim", "sample.wav"), ("audio.bleep", "sample.wav"), ("audio.channels", "sample.wav"),
        })
        {
            bool media = opId.StartsWith("audio.");
            await Try($"panel {opId} renders", !media || ToolLocator.Default.Has("ffmpeg"), async () =>
            {
                var op = ConversionService.Instance.Registry.Get(opId);
                var t = Editors.AskAsync(op, [W(fixture)], new Dictionary<string, string>());
                await Task.Delay(2500);
                var ed = Editors.Last;
                if (ed is null) { OptionsWindow.Last?.Close(); throw new Exception("panel did not open (fell back to the plain sheet); see events.log"); }
                await Shot(ed, Path.Combine(outDir, $"panel-{opId.Replace('.', '-')}.png"));
                var status = ed.StatusText;
                ed.Close();
                await Task.WhenAny(t, Task.Delay(1000));
                if (status.StartsWith("Preview failed") || status.StartsWith("Couldn't")) throw new Exception(status);
                return string.IsNullOrEmpty(status) ? "ok" : status;
            });
        }

        await Try("welcome window renders", true, async () =>
        {
            WelcomeWindow.Show(); await Task.Delay(1200);
            if (WelcomeWindow.Last is { } ww) { await Shot(ww, Path.Combine(outDir, "welcome.png")); ww.Close(); }
            return "ok";
        });

        var svc = ConversionService.Instance;
        async Task<string> Convert(string opId, params string[] files)
        {
            var jobs = await svc.RunAsync(svc.Registry.Get(opId), files, null, ui);
            if (jobs.Count == 0 || jobs.Any(j => j.State != JobState.Succeeded)) throw new Exception(string.Join("; ", jobs.Select(j => j.Result?.Error + " " + j.Result?.Detail)));
            return string.Join(", ", jobs.SelectMany(j => j.Result!.Outputs).Select(Path.GetFileName));
        }
        await Try("convert PNG → WebP via app service", true, () => Convert("image.to.webp", W("sample.png")));
        await Try("convert JPG → HEIC (Magick.NET on Windows)", false, () => Convert("image.to.heic", W("sample.jpg")));
        await Try("merge PDFs via app service", true, () => Convert("pdf.merge", W("sample.pdf"), W("with-image.pdf")));
        await Try("bundled FFmpeg and ffprobe are both found (no PATH)", true, () =>
            Task.FromResult($"{ToolLocator.Default.Find("ffmpeg") ?? throw new Exception("ffmpeg missing")} | {ToolLocator.Default.Find("ffprobe") ?? throw new Exception("ffprobe missing")}"));
        await Try("MP4 → MP3 via app service (bundled FFmpeg)", true, () => Convert("video.to.mp3", W("sample.mp4")));
        await Try("MP4 → GIF via app service", true, () => Convert("video.to.gif", W("sample.mp4")));
        await Try("WAV → MP3 via app service", true, () => Convert("audio.to.mp3", W("sample.wav")));
        await Try("MP3 → OGG/Opus via app service", true, () => Convert("audio.to.opus", W("sample.mp3")));
        await Try("PDF → PNG without Ghostscript (PDFium)", true, () => Convert("pdf.to.png", W("sample.pdf")));
        await Try("PDF compress without Ghostscript", false, () => Convert("pdf.compress", W("with-image.pdf")));
        await Try("DOCX → PDF via doc.to.pdf (LibreOffice id, built-in engine when it's missing)", true, () => Convert("doc.to.pdf", W("sample.docx")));
        await Try("DOC (Word 97) → DOCX built in", true, () => Convert("doc.native.docx", W("sample.doc")));
        await Try("PPT → PDF built in", true, () => Convert("slides.to.pdf", W("sample.ppt")));
        await Try("DOCX → ODT and RTF built in", true, async () => $"{await Convert("doc.native.odt", W("sample.docx"))} {await Convert("doc.native.rtf", W("sample.docx"))}");
        await Try("DOCX → PDF built in (no LibreOffice)", true, () => Convert("doc.native.pdf", W("sample.docx")));
        await Try("XLSX → CSV built in", true, () => Convert("sheet.to.csv", W("sample.xlsx")));
        await Try("PPTX → PDF built in", true, () => Convert("slides.to.pdf", W("sample.pptx")));
        await Try("PDF → PPTX built in (PDFium)", true, () => Convert("pdf.to.pptx", W("sample.pdf")));
        await Try("EPUB → PDF built in", true, () => Convert("ebook.to.pdf", W("sample.epub")));
        await Try("RAR/7z: 7-Zip bundled", true, () => Task.FromResult(ToolLocator.Default.Find("7zip") ?? throw new Exception("7-Zip not found")));
        await Try("PNG → JPEG XL", true, () => Convert("image.to.jxl", W("sample.png")));
        await Try("progress window: finished with errors summary", true, async () =>
        {
            var bad = W("broken.png"); File.WriteAllBytes(bad, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
            await svc.RunAsync(svc.Registry.Get("image.to.webp"), [W("sample.png"), bad], null, ui);
            await Task.Delay(600);
            var pw = ProgressWindow.Last ?? throw new Exception("no progress window");
            await Shot(pw, Path.Combine(outDir, "progress-errors.png"));
            pw.Close();
            return "ok";
        });
        await Try("QR result card", true, async () =>
        {
            await svc.RunAsync(svc.Registry.Get("image.qr-read"), [W("qr.png")], null, ui);
            await Task.Delay(700);
            var q = QrResultWindow.Last ?? throw new Exception("no QR window");
            await Shot(q, Path.Combine(outDir, "qr-result.png"));
            q.Close();
            return "ok";
        });
        await Try("float shelf", true, async () =>
        {
            var sh = ShelfWindow.Open([W("sample.png"), W("sample.pdf"), W("sample.xlsx")]);
            await Task.Delay(700);
            await Shot(sh, Path.Combine(outDir, "shelf.png"));
            var n = sh.Paths.Count; sh.Close();
            if (n != 3) throw new Exception($"shelf holds {n}");
            return "3 files";
        });
        await Try("tray menu (screen capture)", false, async () =>
        {
            using var t = new TrayAndHotkey(); t.ShowTray();
            Native.GetCursorPos(out var p);
            var path = Path.Combine(outDir, "tray-menu.png");
            var cap = Task.Run(async () =>
            {
                await Task.Delay(900);
                ScreenCapture.Save(p.X - 20, p.Y - 320, 380, 640, path);
                PostMessage(t.MessageWindow, 0x001F /*WM_CANCELMODE*/, IntPtr.Zero, IntPtr.Zero);
            });
            t.ShowMenuForTest();
            await cap;
            return "captured";
        });
        await Try("Preset 'Web image' via app service", true, async () =>
        {
            var p = Orange.Core.Presets.PresetStore.Find(svc.Presets, "Web image")!;
            var r = await new Orange.Core.Presets.PresetRunner(svc.Registry).RunAsync(p, [W("sample.png")]);
            if (r[0].State != Orange.Core.Presets.PresetFileState.Done) throw new Exception(r[0].Error);
            return Path.GetFileName(r[0].Output!);
        });
        async Task<string> ConvertWith(string opId, string file, params (string K, string V)[] o)
        {
            var jobs = await svc.RunAsync(svc.Registry.Get(opId), [file], o.ToDictionary(x => x.K, x => x.V), ui);
            if (jobs.Count == 0 || jobs.Any(j => j.State != JobState.Succeeded)) throw new Exception(string.Join("; ", jobs.Select(j => j.Result?.Error + " " + j.Result?.Detail)));
            return string.Join(", ", jobs.SelectMany(j => j.Result!.Outputs).Select(Path.GetFileName));
        }
        await Try("Edit photo via app service", true, () => ConvertWith("image.edit", W("sample.png"), ("exposure", "0.5"), ("contrast", "10")));
        await Try("Add BG via app service", true, () => ConvertWith("image.add-background", W("sample-alpha.png"), ("background", "sunset"), ("padding", "8")));
        await Try("Redact via app service", true, () => ConvertWith("image.redact", W("sample.png"), ("regions", "10,10,60,40"), ("style", "pixelate")));
        await Try("Bleep via app service (FFmpeg)", ToolLocator.Default.Has("ffmpeg"), () => ConvertWith("audio.bleep", W("sample.wav"), ("ranges", "0.5-1"), ("sound", "beep")));
        await Try("Zip a folder (Actions wheel)", true, async () =>
        {
            var dir = Path.Combine(Path.GetDirectoryName(W("sample.png"))!, "zip-me"); Directory.CreateDirectory(dir);
            File.Copy(W("sample.png"), Path.Combine(dir, "a.png"), true);
            return Path.GetFileName(await Orange.Core.Providers.ArchiveProvider.CreateZipAsync([dir], null, CancellationToken.None));
        });

        await Try("read File Explorer selection", false, async () =>
        {
            var p = Process.Start("explorer.exe", $"/select,\"{W("sample.png")}\"");
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(500);
                var sel = ExplorerSelection.Get(Native.GetForegroundWindow());
                if (sel.Any(s => s.EndsWith("sample.png", StringComparison.OrdinalIgnoreCase))) return "foreground Explorer selection = " + string.Join(",", sel.Select(Path.GetFileName));
            }
            throw new Exception("Explorer window didn't report the selection (no interactive shell on this machine?)");
        });

        var report = new
        {
            machine = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            build = Environment.OSVersion.Version.Build,
            dpi = Native.GetDpiForWindow(Main?.Hwnd ?? IntPtr.Zero),
            tools = new[] { "ffmpeg", "ffprobe", "pandoc", "ghostscript", "libreoffice" }.ToDictionary(t => t, t => ToolLocator.Default.Find(t)),
            checks,
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, "selftest.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return checks.Any(c => c.Required && !c.Passed) ? 1 : 0;
    }

    private static MainWindow? Main => null;

    private static Native.POINT Center()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        return new Native.POINT { X = area.X + area.Width / 2, Y = area.Y + area.Height / 2 };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    private static async Task Shot(Window w, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync((UIElement)w.Content);
        var px = await rtb.GetPixelsAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, px.ToArray());
        await enc.FlushAsync();
    }

    private static string FindFixtures()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return d is null ? Path.Combine(AppContext.BaseDirectory, "fixtures") : Path.Combine(d.FullName, "tests", "fixtures");
    }
}

/// <summary>GDI screen grab for surfaces that aren't XAML (the tray's Win32 popup menu). Self-test only.</summary>
internal static class ScreenCapture
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    public static void Save(int x, int y, int w, int h, string path)
    {
        x = Math.Max(0, x); y = Math.Max(0, y);
        var screen = GetDC(IntPtr.Zero); var mem = CreateCompatibleDC(screen); var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 /*SRCCOPY*/);
        SelectObject(mem, old);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, px, ref bi, 0);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        using var fs = File.Create(path);
        var enc = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, px);
        enc.FlushAsync().AsTask().GetAwaiter().GetResult();
    }
}
