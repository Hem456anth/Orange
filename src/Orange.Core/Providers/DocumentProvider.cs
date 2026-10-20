using System.Net;
using System.Text;

namespace Orange.Core.Providers;

/// <summary>
/// Documents through Pandoc (always with --sandbox: no network, no reading files other than the input) and,
/// for PDF output only, LibreOffice in headless mode with a throwaway profile. Both are optional external tools;
/// the operations that need a missing tool are hidden, never shown as working.
/// </summary>
public sealed class DocumentProvider(ToolLocator tools) : IOperationProvider
{
    public string Name => "Documents (Pandoc, LibreOffice)";
    private static readonly HashSet<string> Inputs = ["docx", "odt", "rtf", "txt", "md", "html", "epub"];

    // output ext → pandoc writer
    private static readonly (string Ext, string Title, string Writer)[] Targets =
    [
        ("docx", "DOCX", "docx"), ("odt", "ODT", "odt"), ("md", "Markdown", "gfm"), ("html", "HTML", "html5"),
        ("txt", "TXT", "plain"), ("rtf", "RTF", "rtf"), ("epub", "EPUB", "epub3"),
    ];

    public IEnumerable<OperationDescriptor> Describe()
    {
        foreach (var t in Targets)
            yield return new OperationDescriptor
            {
                Id = $"doc.to.{t.Ext}", Title = t.Title, Category = FileKind.Document, Group = OperationGroup.Convert,
                // Pandoc has no RTF reader before 3.1.9 and no EPUB→EPUB use; inputs filtered at run time.
                InputExtensions = Inputs, OutputExtension = t.Ext, RequiresTool = "pandoc",
                Keywords = [t.Ext, "convert", "document", t.Ext == "txt" ? "extract text" : t.Ext],
            };
        yield return new OperationDescriptor
        {
            Id = "doc.to.pdf", Title = "PDF", Category = FileKind.Document, Group = OperationGroup.Convert, InputExtensions = Inputs.Append("doc").ToHashSet(),
            OutputExtension = "pdf", RequiresTool = "libreoffice", Keywords = ["pdf", "print", "export"],
        };
        // Legacy binary Office files: LibreOffice only (the native readers cover DOCX/XLSX/XLS/PPTX).
        yield return new OperationDescriptor
        {
            Id = "ppt.to.pdf", Title = "PDF", Category = FileKind.Presentation, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "ppt" },
            OutputExtension = "pdf", RequiresTool = "libreoffice", Keywords = ["powerpoint", "legacy", "pdf"],
        };
        yield return new OperationDescriptor
        {
            Id = "doc.word-count", Title = "Word count", Category = FileKind.Document, Group = OperationGroup.Tool, InputExtensions = new HashSet<string> { "txt", "md" },
            OutputExtension = "txt", Icon = "count", Keywords = ["words", "characters", "statistics", "suffix:Stats"],
        };
    }

    // Always available: when Pandoc or LibreOffice isn't installed, Orange's own engine does the job (same ids,
    // so presets and scripts keep working). The external program is used when present, for its exact layout.
    public bool IsAvailable(OperationDescriptor op) => true;

    private static readonly OfficeProvider Native = new();

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var op = r.Operation;
        var needs = op.Id switch { "doc.to.pdf" or "ppt.to.pdf" => "libreoffice", "doc.word-count" => null, _ => "pandoc" };
        if (needs is not null && !tools.Has(needs))
        {
            var nativeId = op.Id == "ppt.to.pdf" ? "slides.to.pdf" : "doc.native." + op.OutputExtension;
            var nativeOp = Native.Describe().First(d => d.Id == nativeId);
            return await Native.ExecuteAsync(new OperationRequest
            {
                Operation = nativeOp, Inputs = r.Inputs, OutputDirectory = r.OutputDirectory, Options = r.Options, Overwrite = r.Overwrite,
            }, progress, ct);
        }
        var input = r.Inputs[0];
        var stem = Path.GetFileNameWithoutExtension(input);
        var srcExt = OperationRegistry.Normalize(FileTypeDetector.Ext(input));
        using var tmp = new SafePaths.TempWorkspace();
        progress?.Report(0.1);

        if (op.Id == "doc.word-count")
        {
            var text = await File.ReadAllTextAsync(input, ct);
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var o = tmp.File("txt");
            await File.WriteAllTextAsync(o, $"Words: {words}\nCharacters: {text.Length}\nCharacters (no spaces): {text.Count(c => !char.IsWhiteSpace(c))}\nLines: {text.Split('\n').Length}\n", ct);
            return Done(o, "txt", "Stats");
        }

        // Plain text has no Pandoc reader. Wrap it as escaped HTML paragraphs so nothing in it is interpreted.
        string source = input, reader = Reader(srcExt);
        if (srcExt == "txt")
        {
            source = tmp.File("html");
            var raw = await File.ReadAllTextAsync(input, ct);
            var sb = new StringBuilder("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>")
                .Append(WebUtility.HtmlEncode(stem)).Append("</title></head><body>\n");
            foreach (var para in raw.Replace("\r\n", "\n").Split("\n\n"))
                sb.Append("<p>").Append(WebUtility.HtmlEncode(para).Replace("\n", "<br>")).Append("</p>\n");
            await File.WriteAllTextAsync(source, sb.Append("</body></html>").ToString(), new UTF8Encoding(false), ct);
            reader = "html";
        }

        if (op.Id is "doc.to.pdf" or "ppt.to.pdf")
        {
            // LibreOffice reads docx/odt/rtf/html directly; markdown/txt/epub go through DOCX first.
            if (srcExt is "md" or "txt" or "epub")
            {
                var docx = tmp.File("docx");
                await Pandoc(source, reader, docx, "docx", ct);
                source = docx;
            }
            var outDir = tmp.Dir("lo");
            var profile = new Uri(tmp.Dir("profile")).AbsoluteUri;
            var res = await ProcessRunner.RunAsync(tools.Require("libreoffice"),
                [$"-env:UserInstallation={profile}", "--headless", "--norestore", "--nolockcheck", "--nodefault", "--convert-to", "pdf", "--outdir", outDir, source],
                ct, timeout: TimeSpan.FromMinutes(10));
            var produced = Directory.EnumerateFiles(outDir, "*.pdf").FirstOrDefault();
            if (res.ExitCode != 0 || produced is null)
                throw new OrangeException("LibreOffice couldn't convert this document to PDF.", ProcessRunner.Tail(res.StdErr + res.StdOut));
            PdfProvider.ValidatePdf(produced);
            return Done(produced, "pdf");
        }

        var target = Targets.First(t => $"doc.to.{t.Ext}" == op.Id);
        if (srcExt == "rtf" && !await PandocCanRead("rtf", ct))
            throw new OrangeException("This Pandoc version can't read RTF. Update Pandoc to 3.1.9 or later.");
        var outFile = tmp.File(target.Ext);
        await Pandoc(source, reader, outFile, target.Writer, ct);
        return Done(outFile, target.Ext);

        OperationResult Done(string produced, string ext, string? suffix = null)
        {
            var final = SafePaths.PlanOutput(input, r.OutputDirectory, stem, suffix, ext, r.Overwrite, r.Inputs);
            progress?.Report(1);
            return OperationResult.Ok(SafePaths.Commit(produced, final, r.Overwrite));
        }
    }

    private static string Reader(string ext) => ext switch
    {
        "docx" => "docx", "odt" => "odt", "rtf" => "rtf", "md" => "gfm", "html" => "html", "epub" => "epub", _ => "html",
    };

    private async Task Pandoc(string input, string reader, string output, string writer, CancellationToken ct)
    {
        // --sandbox: Pandoc may read only the input and write only the output: no network, no other files.
        // Some Linux distro builds of Pandoc don't embed their data files and can't write DOCX/ODT/EPUB in sandbox
        // mode. Only on non-Windows dev/CI machines, and only when ORANGE_ALLOW_UNSANDBOXED_PANDOC=1, Orange
        // falls back to an unsandboxed run with network-free inputs. Windows always uses the sandbox.
        bool sandbox = OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ORANGE_ALLOW_UNSANDBOXED_PANDOC") != "1" || !_dataMissing;
        var args = new List<string> { "-f", reader, "-t", writer, "-o", output };
        if (sandbox) args.Insert(0, "--sandbox");
        if (writer is "html5" or "epub3" or "docx" or "odt" or "rtf") args.Add("--standalone");
        if (writer == "html5") args.Add("--embed-resources");
        if (writer == "plain") args.AddRange(["--wrap", "none"]);
        args.Add("--");                      // nothing after this is an option, even a file named "-o"
        args.Add(input);
        var res = await ProcessRunner.RunAsync(tools.Require("pandoc"), args, ct, timeout: TimeSpan.FromMinutes(5));
        if (res.ExitCode != 0 && sandbox && res.StdErr.Contains("Could not find data file") && !OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable("ORANGE_ALLOW_UNSANDBOXED_PANDOC") == "1")
        {
            _dataMissing = true;
            await Pandoc(input, reader, output, writer, ct);
            return;
        }
        if (res.ExitCode != 0)
            throw new OrangeException(res.StdErr.Contains("Could not find data file")
                ? "This Pandoc build can't write that format in safe mode. Install the official Pandoc release."
                : "Pandoc couldn't convert this document.", ProcessRunner.Tail(res.StdErr));
    }
    private bool _dataMissing;

    private bool? _rtf;
    private async Task<bool> PandocCanRead(string fmt, CancellationToken ct)
    {
        if (_rtf is { } b) return b;
        var res = await ProcessRunner.RunAsync(tools.Require("pandoc"), ["--list-input-formats"], ct, timeout: TimeSpan.FromSeconds(20));
        _rtf = res.StdOut.Split('\n').Any(l => l.Trim() == fmt);
        return _rtf.Value;
    }
}
