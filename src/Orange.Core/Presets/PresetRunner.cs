namespace Orange.Core.Presets;

public enum PresetFileState { Done, Skipped, Failed, Cancelled }

public sealed record PresetFileResult(string Input, PresetFileState State, string? Output, string? Error = null, string? Detail = null);

/// <summary>
/// Runs a preset: the operation writes into a private temp folder, metadata is stripped when asked, then the result
/// is named by the preset's pattern and moved beside the original (or into its subfolder) under the conflict policy.
/// The original is never the destination, whatever the pattern says.
/// </summary>
public sealed class PresetRunner(OperationRegistry registry)
{
    public async Task<IReadOnlyList<PresetFileResult>> RunAsync(Preset preset, IReadOnlyList<string> inputs,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        PresetJson.Validate(preset, registry);
        var op = registry.Get(preset.OperationId);
        var results = new List<PresetFileResult>();
        var runner = new JobRunner(registry);
        for (int i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (ct.IsCancellationRequested) { results.Add(new(input, PresetFileState.Cancelled, null)); continue; }
            using var tmp = new SafePaths.TempWorkspace();
            try
            {
                var full = SafePaths.ValidateInput(input);
                var jobs = runner.Plan(op, [full], preset.Options, tmp.Dir("op"), OverwritePolicy.KeepBoth);
                var sub = new Progress<double>(v => progress?.Report((i + v) / inputs.Count));
                await runner.RunAsync(jobs, new Progress<BatchProgress>(b => ((IProgress<double>)sub).Report(b.Fraction)), ct).ConfigureAwait(false);
                var job = jobs[0];
                if (job.State == JobState.Cancelled) { results.Add(new(input, PresetFileState.Cancelled, null)); continue; }
                if (job.Result is not { Success: true } r || r.Outputs.Count == 0)
                { results.Add(new(input, PresetFileState.Failed, null, job.Result?.Error ?? "Failed.", job.Result?.Detail)); continue; }

                var produced = r.Outputs[0];
                if (preset.Metadata == MetadataPolicy.Strip && !op.Id.EndsWith(".strip-metadata", StringComparison.Ordinal))
                    produced = await StripAsync(runner, op.Category, produced, tmp, ct).ConfigureAwait(false) ?? produced;

                var destDir = Path.GetDirectoryName(full)!;
                if (preset.Output.Location == OutputLocation.Subfolder)
                {
                    destDir = Path.GetFullPath(Path.Combine(destDir, SafePaths.SanitizeFileName(preset.Output.Subfolder)));
                    if (!SafePaths.IsInside(Path.GetDirectoryName(full)!, destDir)) throw new OrangeException("The preset's subfolder is outside the file's folder.");
                    Directory.CreateDirectory(destDir);
                }
                var ext = FileTypeDetector.Ext(produced);
                var target = Path.Combine(destDir, preset.FileNameFor(full, ext, op.Title));
                bool exists = File.Exists(target) || Directory.Exists(target);
                bool isInput = string.Equals(Path.GetFullPath(target), full, StringComparison.OrdinalIgnoreCase);
                if (exists && !isInput && preset.Output.Conflict == ConflictPolicy.Skip)
                { results.Add(new(input, PresetFileState.Skipped, target, "A file with that name already exists.")); continue; }
                if (isInput || exists && preset.Output.Conflict == ConflictPolicy.KeepBoth)
                    target = SafePaths.PlanOutput(full, destDir, Path.GetFileNameWithoutExtension(target), null, ext, OverwritePolicy.KeepBoth, [full]);
                File.Move(produced, target, overwrite: preset.Output.Conflict == ConflictPolicy.Replace && !isInput);
                results.Add(new(input, PresetFileState.Done, target));
            }
            catch (OperationCanceledException) { results.Add(new(input, PresetFileState.Cancelled, null)); }
            catch (OrangeException e) { results.Add(new(input, PresetFileState.Failed, null, e.Message, e.Detail)); }
            catch (Exception e) { results.Add(new(input, PresetFileState.Failed, null, "Orange couldn't process this file.", e.Message)); }
        }
        progress?.Report(1);
        return results;
    }

    private async Task<string?> StripAsync(JobRunner runner, FileKind kind, string file, SafePaths.TempWorkspace tmp, CancellationToken ct)
    {
        var stripId = $"{kind.ToString().ToLowerInvariant()}.strip-metadata";
        var strip = registry.All.FirstOrDefault(o => o.Id == stripId);
        if (strip is null || !registry.IsAvailable(strip)) return null;
        var jobs = runner.Plan(strip, [file], null, tmp.Dir("strip"), OverwritePolicy.KeepBoth);
        await runner.RunAsync(jobs, null, ct).ConfigureAwait(false);
        return jobs[0].Result is { Success: true, Outputs.Count: > 0 } r ? r.Outputs[0] : null;
    }

    /// <summary>Presets that apply to every file in a selection (all one kind, operation available).</summary>
    public IReadOnlyList<Preset> ForFiles(IEnumerable<Preset> presets, IReadOnlyList<string> files)
    {
        var convert = registry.ForFiles(files, OperationGroup.Convert).Select(o => o.Id);
        var tools = registry.ForFiles(files, OperationGroup.Tool).Select(o => o.Id);
        var ok = convert.Concat(tools).ToHashSet();
        return presets.Where(p => ok.Contains(p.OperationId)).ToList();
    }
}
