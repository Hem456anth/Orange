using Orange.Core.Logging;
using Xunit;

namespace Orange.Core.Tests;

[Collection("LocalLog")]
public class LocalLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orange-log-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 20, 9, 0, 0);

    public LocalLogTests() { LocalLog.Now = () => _now; LocalLog.MaxBytesPerFile = 2 * 1024 * 1024; }

    public void Dispose()
    {
        LocalLog.Disable();
        LocalLog.Now = () => DateTime.Now;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Off_by_default_and_writes_nothing()
    {
        LocalLog.Disable();
        LocalLog.Write("nothing");
        Assert.False(LocalLog.Enabled);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Writes_one_line_per_event_and_neutralises_newlines()
    {
        LocalLog.Enable(_dir);
        LocalLog.Write("drop index=2 file=evil\r\nFAKE 2026 line.png");
        var lines = File.ReadAllLines(LocalLog.CurrentFile(_dir));
        Assert.Equal(2, lines.Length);                // log-enabled + our event
        Assert.Contains("drop index=2", lines[1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("FAKE"));
    }

    [Fact]
    public void Rolls_over_at_the_size_cap()
    {
        LocalLog.MaxBytesPerFile = 1000;
        LocalLog.Enable(_dir);
        for (int i = 0; i < 50; i++) LocalLog.Write(new string('x', 60));
        var file = LocalLog.CurrentFile(_dir);
        Assert.True(new FileInfo(file).Length <= 1000);
        Assert.True(File.Exists(file + ".1"));
        Assert.True(Directory.GetFiles(_dir).Length <= 2);
    }

    [Fact]
    public void Prunes_old_days_but_leaves_other_files_alone()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "orange-20261001.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "orange-20261018.log"), "recent");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "mine");
        LocalLog.Enable(_dir);
        Assert.False(File.Exists(Path.Combine(_dir, "orange-20261001.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "orange-20261018.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")));
    }
}

public class CommitRaceTests
{
    [Fact]
    public void Parallel_commits_to_the_same_name_keep_both()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orange-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "photo.jpg");
            var results = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 8, i =>
            {
                var tmp = Path.Combine(dir, $"t{i}.tmp"); File.WriteAllText(tmp, i.ToString());
                results.Add(SafePaths.Commit(tmp, target, OverwritePolicy.KeepBoth));
            });
            Assert.Equal(8, results.Distinct().Count());
            Assert.Equal(8, Directory.GetFiles(dir, "photo*.jpg").Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}
