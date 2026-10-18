namespace Orange.Core;

public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>One unit of work in a batch: either one input file, or all inputs for a combining operation.</summary>
public sealed class Job
{
    public required OperationRequest Request { get; init; }
    public JobState State { get; internal set; } = JobState.Queued;
    public double Progress { get; internal set; }
    public OperationResult? Result { get; internal set; }
    public string DisplayName => Request.Operation.CombinesInputs
        ? $"{Request.Inputs.Count} files → {Request.Operation.Title}"
        : $"{Path.GetFileName(Request.Inputs[0])} → {Request.Operation.Title}";
}

public sealed record BatchProgress(int Done, int Total, double Fraction, Job Current);

/// <summary>Validates, splits a selection into jobs, and runs them with bounded parallelism.</summary>
public sealed class JobRunner(OperationRegistry registry)
{
    public IReadOnlyList<Job> Plan(OperationDescriptor op, IReadOnlyList<string> inputs, IReadOnlyDictionary<string, string>? options = null,
                                   string? outputDir = null, OverwritePolicy overwrite = OverwritePolicy.KeepBoth)
    {
        var validated = inputs.Select(SafePaths.ValidateInput).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (validated.Count < op.MinInputs) throw new OrangeException($"{op.Title} needs at least {op.MinInputs} files.");
        foreach (var f in validated)
        {
            var kind = FileTypeDetector.Detect(f);
            if (kind != op.Category)
                throw new OrangeException($"\"{Path.GetFileName(f)}\" doesn't look like a valid {op.Category.ToString().ToLowerInvariant()} file.",
                    kind == FileKind.Unknown ? "Its contents don't match its extension, so Orange won't open it." : $"It looks like {kind}.");
        }
        if (!registry.IsAvailable(op))
            throw op.RequiresTool is { } tool && !ToolLocator.Default.Has(tool)
                ? new OrangeException($"{op.Category} → {op.Title} needs {ToolNames.Friendly(tool)}, which isn't installed.", $"Install {ToolNames.Friendly(tool)} or set its path in Settings → Converters.")
                : new OrangeException($"Converting to {op.Title} isn't supported on this PC.", "The image or media library on this machine can't write that format.");
        var opts = options ?? new Dictionary<string, string>();
        var probe = new OperationRequest { Operation = op, Inputs = validated, Options = opts };
        OptionValidator.Validate(probe);

        if (op.CombinesInputs)
            return [new Job { Request = new OperationRequest { Operation = op, Inputs = validated, Options = opts, OutputDirectory = outputDir, Overwrite = overwrite } }];
        return validated.Select(f => new Job { Request = new OperationRequest { Operation = op, Inputs = [f], Options = opts, OutputDirectory = outputDir, Overwrite = overwrite } }).ToList();
    }

    public async Task<IReadOnlyList<Job>> RunAsync(IReadOnlyList<Job> jobs, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        SafePaths.TempWorkspace.CleanupStale();
        int done = 0;
        using var gate = new SemaphoreSlim(EngineLimits.Current.MaxParallelJobs);
        void Report(Job j) => progress?.Report(new BatchProgress(done, jobs.Count, (done + jobs.Where(x => x.State == JobState.Running).Sum(x => x.Progress)) / jobs.Count, j));

        var tasks = jobs.Select(async job =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) { job.State = JobState.Cancelled; return; }
                job.State = JobState.Running; Report(job);
                var p = new Progress<double>(v => { job.Progress = Math.Clamp(v, 0, 1); Report(job); });
                try
                {
                    job.Result = await registry.ProviderFor(job.Request.Operation).ExecuteAsync(job.Request, p, ct).ConfigureAwait(false);
                    job.State = job.Result.Success ? JobState.Succeeded : JobState.Failed;
                }
                catch (OperationCanceledException) { job.State = JobState.Cancelled; job.Result = OperationResult.Fail("Cancelled."); }
                catch (OrangeException e) { job.State = JobState.Failed; job.Result = OperationResult.Fail(e.Message, e.Detail); }
                catch (Exception e) { job.State = JobState.Failed; job.Result = OperationResult.Fail("Orange couldn't process this file.", e.Message); }
                job.Progress = 1;
                Interlocked.Increment(ref done);
                Report(job);
            }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { foreach (var j in jobs.Where(j => j.State == JobState.Queued)) j.State = JobState.Cancelled; }
        return jobs;
    }
}
