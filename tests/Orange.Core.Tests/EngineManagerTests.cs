using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Orange.Core.Engines;
using Xunit;

namespace Orange.Core.Tests;

public class EngineManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orange-engines-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHttp(byte[] body, string? redirectHost = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            if (redirectHost != null) req.RequestUri = new Uri($"https://{redirectHost}/x.zip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = req });
        }
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, t) in entries) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(t); }
        return ms.ToArray();
    }

    private static EngineSpec Spec(byte[] body, string url = "https://github.com/x/y/releases/download/1/t.zip", string? sha = null) =>
        new("tool1", "Tool One", "tool1", "1.0", url, sha ?? Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), body.Length, "tool1/tool1.exe", "MIT", "https://example.org", "testing");

    [Fact]
    public async Task Installs_verified_download_registers_it_and_uninstalls()
    {
        var body = Zip(("tool1/tool1.exe", "fake exe"), ("tool1/LICENSE", "MIT"));
        var loc = new ToolLocator();
        var m = new EngineManager(_root, loc, new FakeHttp(body));
        var e = await m.InstallAsync(Spec(body));
        Assert.True(File.Exists(e.ExePath));
        Assert.Equal(e.ExePath, loc.Find("tool1"));
        Assert.Single(m.Installed());
        Assert.DoesNotContain(Directory.GetDirectories(_root), d => Path.GetFileName(d).StartsWith(".staging"));
        Assert.True(m.Uninstall("tool1"));
        Assert.Empty(m.Installed());
        Assert.False(Directory.Exists(Path.Combine(_root, "tool1")));
    }

    [Fact]
    public async Task Checksum_mismatch_installs_nothing()
    {
        var body = Zip(("tool1/tool1.exe", "fake exe"));
        var m = new EngineManager(_root, new ToolLocator(), new FakeHttp(body));
        var ex = await Assert.ThrowsAsync<OrangeException>(() => m.InstallAsync(Spec(body, sha: new string('0', 64))));
        Assert.Contains("checksum", ex.Message);
        Assert.Empty(m.Installed());
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("http://github.com/x.zip")]
    [InlineData("https://evil.example.com/x.zip")]
    public async Task Refuses_plain_http_and_unknown_hosts(string url)
    {
        var body = Zip(("tool1/tool1.exe", "x"));
        var http = new FakeHttp(body);
        var m = new EngineManager(_root, new ToolLocator(), http);
        await Assert.ThrowsAsync<OrangeException>(() => m.InstallAsync(Spec(body, url)));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task Refuses_redirect_to_unknown_host()
    {
        var body = Zip(("tool1/tool1.exe", "x"));
        var m = new EngineManager(_root, new ToolLocator(), new FakeHttp(body, redirectHost: "evil.example.com"));
        await Assert.ThrowsAsync<OrangeException>(() => m.InstallAsync(Spec(body)));
        Assert.Empty(m.Installed());
    }

    [Fact]
    public async Task Zip_slip_entry_installs_nothing()
    {
        var body = Zip(("tool1/tool1.exe", "x"), ("../../evil.txt", "boom"));
        var m = new EngineManager(_root, new ToolLocator(), new FakeHttp(body));
        await Assert.ThrowsAsync<OrangeException>(() => m.InstallAsync(Spec(body)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.txt")));
        Assert.Empty(m.Installed());
    }

    [Fact]
    public void Catalog_entries_are_pinned_https_and_allowed()
    {
        foreach (var s in EngineManager.Catalog)
        {
            var u = new Uri(s.Url);
            Assert.Equal("https", u.Scheme);
            Assert.Contains(u.Host, EngineManager.AllowedHosts);
            Assert.Matches("^[0-9a-f]{64}$", s.Sha256);
            Assert.True(s.SizeBytes > 0);
        }
    }
}
