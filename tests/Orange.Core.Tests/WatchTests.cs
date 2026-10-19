using System.Collections.Concurrent;
using Orange.Core.Presets;
using Orange.Core.Watch;
using Xunit;

namespace Orange.Core.Tests;

public class WatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orange-watch-" + Guid.NewGuid().ToString("N"));
    public WatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Fixture(string n)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures", n);
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 15000)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    [Theory]
    [InlineData("video.mp4.crdownload", true)]
    [InlineData("a.part", true)]
    [InlineData("~$report.docx", true)]
    [InlineData(".hidden.png", true)]
    [InlineData("photo.png", false)]
    public void Ignores_partial_and_temp_names(string name, bool ignored) => Assert.Equal(ignored, FolderWatcher.IsIgnoredName(name));

    [Fact]
    public async Task Waits_until_a_growing_file_settles_then_processes_once()
    {
        var seen = new ConcurrentBag<string>();
        using var w = new FolderWatcher(new WatchSpec { Folder = _dir, Preset = "x" }, (f, ct) => { seen.Add(f); return Task.FromResult<IReadOnlyList<string>>([]); })
            { SettleMs = 600, PollMs = 100 };
        var path = Path.Combine(_dir, "big.bin");
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            for (int i = 0; i < 5; i++) { fs.Write(new byte[1000]); fs.Flush(); await Task.Delay(200); }
            Assert.Empty(seen);   // still being written
        }
        Assert.True(await Until(() => seen.Count == 1));
        await Task.Delay(800);
        Assert.Single(seen);
    }

    [Fact]
    public async Task Converts_with_a_preset_ignores_its_own_output_and_moves_originals()
    {
        var reg = OperationRegistry.CreateDefault();
        var preset = new Preset { Name = "Web", OperationId = "image.to.webp" };
        var events = new ConcurrentBag<WatchEvent>();
        using var w = PresetWatch.Create(new WatchSpec { Folder = _dir, Preset = "Web", MoveOriginals = true }, reg, [preset]);
        w.Activity += events.Add;
        File.Copy(Fixture("sample.png"), Path.Combine(_dir, "drop.png"));
        Assert.True(await Until(() => File.Exists(Path.Combine(_dir, "drop Web.webp")) && File.Exists(Path.Combine(_dir, "Originals", "drop.png")), 30000));
        await Task.Delay(3500);   // longer than the settle time: the output must not be processed again
        Assert.Single(events, e => e.Status == "done");
        Assert.False(File.Exists(Path.Combine(_dir, "drop Web Web.webp")));
    }

    [Fact]
    public async Task Paused_watcher_waits_then_resumes()
    {
        var seen = new ConcurrentBag<string>();
        using var w = new FolderWatcher(new WatchSpec { Folder = _dir, Preset = "x", Paused = true }, (f, ct) => { seen.Add(f); return Task.FromResult<IReadOnlyList<string>>([]); })
            { SettleMs = 300, PollMs = 100 };
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        await Task.Delay(1200);
        Assert.Empty(seen);
        w.Resume();
        Assert.True(await Until(() => seen.Count == 1));
    }

    [Fact]
    public void Store_round_trips()
    {
        var s = new WatchStore(Path.Combine(_dir, "watches.json"));
        Assert.Empty(s.Load());
        s.Save([new WatchSpec { Folder = _dir, Preset = "Web image", MoveOriginals = true }]);
        var back = Assert.Single(s.Load());
        Assert.True(back.MoveOriginals);
        Assert.Equal("Web image", back.Preset);
    }
}
