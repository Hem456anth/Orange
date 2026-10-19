using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Orange.Core.Tests;

/// <summary>Every L.T/L.F literal in the app has an en-US resource; Hindi and Telugu files are valid and use the same keys.</summary>
public class LocalisationTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Orange.App"))) d = d.Parent;
        return d!.FullName;
    }
    private static string Key(string s) => "S" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)), 0, 5);
    private static Dictionary<string, string> Resw(string lang) =>
        XDocument.Load(Path.Combine(Root(), "src", "Orange.App", "Strings", lang, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

    [Fact]
    public void Every_wrapped_ui_string_has_an_english_resource()
    {
        var en = Resw("en-US");
        var lit = new Regex(@"L\.[TF]\(""((?:[^""\\]|\\.)*)""");
        var missing = Directory.EnumerateFiles(Path.Combine(Root(), "src", "Orange.App"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => lit.Matches(File.ReadAllText(f)).Select(m => Regex.Unescape(m.Groups[1].Value)))
            .Distinct().Where(s => !en.ContainsKey(Key(s))).ToList();
        Assert.True(missing.Count == 0, "Run tools/l10n/gen_resw.py. Missing: " + string.Join(" | ", missing));
    }

    [Theory]
    [InlineData("hi-IN", "सेटिंग्स")]
    [InlineData("te-IN", "సెట్టింగ్‌లు")]
    public void Translations_use_known_keys_and_keep_placeholders(string lang, string settings)
    {
        var en = Resw("en-US"); var tr = Resw(lang);
        Assert.Equal(settings, tr[Key("Settings")]);
        Assert.True(tr.Count >= 100);
        foreach (var (k, v) in tr)
        {
            Assert.True(en.ContainsKey(k), k);
            foreach (Match m in Regex.Matches(en[k], @"\{\d\}")) Assert.Contains(m.Value, v);
        }
    }
}
