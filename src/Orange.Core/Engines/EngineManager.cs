using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orange.Core.Engines;

/// <summary>An optional helper program Orange can download on request (never automatically).</summary>
public sealed record EngineSpec(
    string Id,                 // "pandoc"
    string Name,               // "Pandoc"
    string Tool,               // ToolLocator name the engine provides
    string Version,
    string Url,                // https only, host must be in AllowedHosts
    string Sha256,             // lowercase hex of the downloaded file
    long SizeBytes,            // expected download size (download is refused if it grows past 110 %)
    string ExeRelativePath,    // path of the executable inside the archive
    string License,
    string Homepage,
    string Purpose,
    string Format = "zip",     // zip | nsis (extracted with the bundled 7-Zip, nothing runs) | msi (administrative image: msiexec /a, no system changes)
    bool Mirrors = false);     // the project hands out downloads from many HTTPS mirrors; the pinned SHA-256 is what's trusted

public sealed record InstalledEngine(string Id, string Version, string ExePath, DateTimeOffset InstalledAt);

public sealed record EngineProgress(string Stage, long Done, long Total);

/// <summary>
/// Settings → Engines. Downloads an engine only when the user clicks Install, over HTTPS from an allow-listed host,
/// streams it to a private temp file with a size cap, checks SHA-256 against the pinned value, extracts it with the
/// same path checks as the archive provider, and only then moves it into %LOCALAPPDATA%\Orange\engines\id\version.
/// Uninstall deletes that folder. Nothing else in Orange uses the network.
/// </summary>
public sealed class EngineManager
{
    public static readonly string[] AllowedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    private readonly string _root;
    private readonly HttpMessageHandler? _handler;
    private readonly ToolLocator _locator;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public EngineManager(string root, ToolLocator? locator = null, HttpMessageHandler? handler = null)
    {
        _root = root; _locator = locator ?? ToolLocator.Default; _handler = handler;
    }

    public string Root => _root;

    /// <summary>%LOCALAPPDATA%\Orange\engines — the folder the app and the command line share.</summary>
    public static EngineManager ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orange", "engines"));

    /// <summary>Engines Orange knows how to fetch. Versions and hashes are pinned in code and reviewed per release.</summary>
    public static IReadOnlyList<EngineSpec> Catalog { get; } =
    [
        new("pandoc", "Pandoc", "pandoc", "3.5",
            "https://github.com/jgm/pandoc/releases/download/3.5/pandoc-3.5-windows-x86_64.zip",
            "887e980e2228d8d45a431936e8bc6270a50cc9a5a4ec7e62064a3a5de6423b71", 37194586,
            "pandoc-3.5/pandoc.exe", "GPL-2.0-or-later", "https://pandoc.org",
            "Markdown, HTML, EPUB, ODT and RTF conversions"),
        new("ghostscript", "Ghostscript", "ghostscript", "10.08.0",
            "https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10080/gs10080w64.exe",
            "52a91b8bf09298788d7a57b9206127026c23eacd75405f0a131e26dc381dce50", 65093120,
            "bin/gswin64c.exe", "AGPL-3.0 (downloaded separately, not part of Orange)", "https://ghostscript.com",
            "Stronger PDF compression (fonts too) and PDF page rendering", Format: "nsis"),
        new("libreoffice", "LibreOffice", "libreoffice", "26.8.1",
            "https://download.documentfoundation.org/libreoffice/stable/26.8.1/win/x86_64/LibreOffice_26.8.1_Win_x86-64.msi",
            "c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312", 374870016,
            "program/soffice.exe", "MPL-2.0", "https://www.libreoffice.org",
            "Legacy DOC, XLS and PPT files and Word/ODT/RTF to PDF with exact layout", Format: "msi", Mirrors: true),
    ];

    public IReadOnlyList<InstalledEngine> Installed()
    {
        var list = new List<InstalledEngine>();
        if (!Directory.Exists(_root)) return list;
        foreach (var f in Directory.EnumerateFiles(_root, "engine.json", SearchOption.AllDirectories))
        {
            try { if (JsonSerializer.Deserialize<InstalledEngine>(File.ReadAllText(f)) is { } e && File.Exists(e.ExePath)) list.Add(e); }
            catch { /* ignore broken records */ }
        }
        return list;
    }

    /// <summary>Points the tool locator at every installed engine (call at start-up).</summary>
    public void Register()
    {
        foreach (var e in Installed())
            if (Catalog.FirstOrDefault(c => c.Id == e.Id) is { } spec) _locator.Override(spec.Tool, e.ExePath);
    }

    public async Task<InstalledEngine> InstallAsync(EngineSpec spec, IProgress<EngineProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(spec.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new OrangeException("Engines are only downloaded over HTTPS.");
        if (!spec.Mirrors && !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new OrangeException($"{uri.Host} isn't an allowed download host.");
        if (spec.Sha256.Length != 64 || !spec.Sha256.All(Uri.IsHexDigit))
            throw new OrangeException($"{spec.Name} has no pinned checksum in this build, so Orange won't download it.");
        if (spec.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || spec.Version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || spec.Id.Contains("..") || spec.Version.Contains(".."))
            throw new OrangeException("Bad engine id.");

        Directory.CreateDirectory(_root);
        var staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zip = Path.Combine(staging, "download." + (spec.Format switch { "nsis" => "exe", "msi" => "msi", _ => "zip" }));
            long cap = spec.SizeBytes > 0 ? spec.SizeBytes + spec.SizeBytes / 10 : 512L * 1024 * 1024;
            using (var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false))
            {
                http.Timeout = TimeSpan.FromMinutes(30);
                using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                // Redirects (GitHub → its CDN) must stay on HTTPS and on an allowed host.
                var final = resp.RequestMessage?.RequestUri ?? uri;
                if (final.Scheme != Uri.UriSchemeHttps || (!spec.Mirrors && !AllowedHosts.Contains(final.Host, StringComparer.OrdinalIgnoreCase)))
                    throw new OrangeException($"The download was redirected to {final.Host}, which isn't allowed.");
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? spec.SizeBytes;
                if (total > cap) throw new OrangeException($"{spec.Name} is larger than expected; download stopped.");
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    done += n;
                    if (done > cap) throw new OrangeException($"{spec.Name} is larger than expected; download stopped.");
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    progress?.Report(new EngineProgress("Downloading", done, total));
                }
                var got = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(got, spec.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new OrangeException($"{spec.Name} failed its checksum check and was deleted. Nothing was installed.");
            }

            progress?.Report(new EngineProgress("Extracting", 0, 0));
            var extract = Path.Combine(staging, "x");
            switch (spec.Format)
            {
                case "nsis": await ExtractNsisAsync(zip, extract, ct); break;
                case "msi": await ExtractMsiAsync(zip, extract, ct); break;
                default: SafeExtract(zip, extract); break;
            }
            var rel = FindExe(extract, spec.ExeRelativePath);
            var exe = Path.GetFullPath(Path.Combine(extract, rel));
            if (!SafePaths.IsInside(extract, exe) || !File.Exists(exe))
                throw new OrangeException($"{spec.Name}'s download doesn't contain {spec.ExeRelativePath}.");

            var target = Path.Combine(_root, spec.Id, spec.Version);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(extract, target);
            var record = new InstalledEngine(spec.Id, spec.Version, Path.Combine(target, rel), DateTimeOffset.Now);
            File.WriteAllText(Path.Combine(target, "engine.json"), JsonSerializer.Serialize(record, Json));
            _locator.Override(spec.Tool, record.ExePath);
            progress?.Report(new EngineProgress("Installed", 1, 1));
            return record;
        }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }

    public bool Uninstall(string id)
    {
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return false;
        var dir = Path.Combine(_root, id);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, true);
        if (Catalog.FirstOrDefault(c => c.Id == id) is { } spec) _locator.Override(spec.Tool, "");
        return true;
    }

    /// <summary>The exe at its documented place, or (layouts differ between releases) the one file with that name.</summary>
    internal static string FindExe(string root, string relative)
    {
        var rel = relative.Replace('/', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(root, rel))) return rel;
        var name = Path.GetFileName(rel);
        var hit = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
        return hit is null ? rel : Path.GetRelativePath(root, hit);
    }

    /// <summary>Unpacks an NSIS installer with the bundled 7-Zip: the installer itself never runs.</summary>
    private async Task ExtractNsisAsync(string exe, string dest, CancellationToken ct)
    {
        var sevenZip = _locator.Find("7zip") ?? throw new OrangeException("Installing this engine needs the bundled 7-Zip, which is missing. Reinstall Orange.");
        Directory.CreateDirectory(dest);
        var res = await ProcessRunner.RunAsync(sevenZip, ["x", "-y", "-bd", "-o" + dest, exe], ct, timeout: TimeSpan.FromMinutes(10));
        if (res.ExitCode != 0) throw new OrangeException("The engine couldn't be unpacked.", ProcessRunner.Tail(res.StdErr + res.StdOut));
        foreach (var junk in new[] { "$PLUGINSDIR", "$TEMP" }) { var d = Path.Combine(dest, junk); if (Directory.Exists(d)) Directory.Delete(d, true); }
        foreach (var u in Directory.EnumerateFiles(dest, "uninst*.exe", SearchOption.TopDirectoryOnly)) File.Delete(u);
        CheckInside(dest);
    }

    /// <summary>Administrative image of an MSI (msiexec /a): copies the files to a folder, no registry, no shortcuts, no admin rights.</summary>
    private static async Task ExtractMsiAsync(string msi, string dest, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new OrangeException("This engine can only be installed on Windows.");
        Directory.CreateDirectory(dest);
        var log = Path.Combine(Path.GetDirectoryName(msi)!, "msi.log");
        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var res = await ProcessRunner.RunAsync(msiexec, ["/a", msi, "/qn", "/norestart", "TARGETDIR=" + dest, "/l*", log], ct, timeout: TimeSpan.FromMinutes(30));
        if (res.ExitCode != 0)
        {
            var tail = File.Exists(log) ? ProcessRunner.Tail(File.ReadAllText(log), 8) : "";
            throw new OrangeException($"Windows Installer couldn't unpack this engine (code {res.ExitCode}).", tail);
        }
        foreach (var m in Directory.EnumerateFiles(dest, "*.msi", SearchOption.TopDirectoryOnly)) File.Delete(m);   // the copied package
        CheckInside(dest);
    }

    private static void CheckInside(string root)
    {
        var full = Path.GetFullPath(root);
        foreach (var f in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
            if (!SafePaths.IsInside(full, Path.GetFullPath(f))) throw new OrangeException("Engine contains an unsafe path; nothing was installed.");
    }

    /// <summary>ZIP extraction with the archive provider's rules: no rooted, drive or ".." entries, every target re-checked.</summary>
    internal static void SafeExtract(string zipPath, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest);
        using var z = ZipFile.OpenRead(zipPath);
        if (z.Entries.Count > 20000) throw new OrangeException("Engine archive has too many files.");
        foreach (var e in z.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Contains(".."))
                throw new OrangeException("Engine archive contains an unsafe path; nothing was installed.");
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!SafePaths.IsInside(root, target)) throw new OrangeException("Engine archive contains an unsafe path; nothing was installed.");
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: false);
        }
    }
}
