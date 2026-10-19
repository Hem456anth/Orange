using System.Net;
using Orange.Core.Updates;
using Xunit;

namespace Orange.Core.Tests;

public class UpdateTests
{
    private sealed class Fake(string body, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Asked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Asked = r.RequestUri; return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) }); }
    }

    [Fact]
    public async Task Newer_release_is_reported_with_notes()
    {
        var f = new Fake("{\"tag_name\":\"v0.3.0\",\"body\":\"- Faster\",\"html_url\":\"https://github.com/Hem456anth/Orange/releases/tag/v0.3.0\"}");
        var info = await new UpdateChecker(f).CheckAsync(new Version(0, 2, 0));
        Assert.True(info.IsNewer);
        Assert.Equal("0.3.0", info.Version);
        Assert.Contains("Faster", info.Notes);
        Assert.Equal(UpdateChecker.ReleasesApi, f.Asked!.ToString());
    }

    [Fact]
    public async Task Same_or_older_is_not_newer_and_foreign_links_are_replaced()
    {
        var info = await new UpdateChecker(new Fake("{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://evil.example/x\"}")).CheckAsync(new Version(0, 2, 0));
        Assert.False(info.IsNewer);
        Assert.StartsWith("https://github.com/Hem456anth/Orange/", info.Url);
    }

    [Fact]
    public async Task Server_error_is_a_message()
        => await Assert.ThrowsAsync<OrangeException>(() => new UpdateChecker(new Fake("", HttpStatusCode.InternalServerError)).CheckAsync(new Version(0, 2, 0)));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")] [InlineData("0.2.0-beta", "0.2.0")] [InlineData("nightly", null)]
    public void Parses_tags(string tag, string? expected) => Assert.Equal(expected, UpdateChecker.Parse(tag)?.ToString());
}
