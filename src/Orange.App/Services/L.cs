using System.Security.Cryptography;
using System.Text;

namespace Orange.App.Services;

/// <summary>
/// UI text lookup. Strings live in Strings/{en-US,hi-IN,te-IN}/Resources.resw (compiled into resources.pri by the
/// MSIX tooling); the key is "S" + the first 10 hex digits of the English text's SHA-1, so code reads naturally as
/// L.T("Settings"). The English text is the fallback when a key or the resource system is missing, so the UI never
/// shows a raw key. Language: Settings → Language (System follows Windows), applied at start-up.
/// </summary>
public static class L
{
    private static Microsoft.Windows.ApplicationModel.Resources.ResourceManager? _rm;
    private static Microsoft.Windows.ApplicationModel.Resources.ResourceContext? _ctx;
    private static readonly Dictionary<string, string> Cache = new();
    public static string Language { get; private set; } = "";

    public static void Init(string? language)
    {
        Language = language ?? "";
        try
        {
            _rm = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager();
            _ctx = _rm.CreateResourceContext();
            if (!string.IsNullOrEmpty(language)) _ctx.QualifierValues["Language"] = language;
        }
        catch (Exception e) { _rm = null; Diagnostics.Event("l10n-unavailable " + e.Message); }
        lock (Cache) Cache.Clear();
    }

    public static string Key(string english)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes(english));
        return "S" + Convert.ToHexString(h, 0, 5);
    }

    public static string T(string english)
    {
        lock (Cache) if (Cache.TryGetValue(english, out var hit)) return hit;
        string value = english;
        try
        {
            if (_rm is not null && _ctx is not null)
            {
                var c = _rm.MainResourceMap.TryGetValue("Resources/" + Key(english), _ctx);
                if (c is not null && !string.IsNullOrEmpty(c.ValueAsString)) value = c.ValueAsString;
            }
        }
        catch { }
        lock (Cache) Cache[english] = value;
        return value;
    }

    /// <summary>T with {0}-style placeholders.</summary>
    public static string F(string english, params object[] args) => string.Format(T(english), args);
}
