namespace WebSnapshots;

public sealed class RunManifestStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RunManifest Manifest { get; }

    public RunManifestStore(string runDir, RunManifest manifest)
    {
        _path = Path.Combine(runDir, "run.json");
        Manifest = manifest;
    }

    public async Task InitializeAsync()
        => await WriteAsync();

    public async Task UpsertSiteAsync(RunSiteItem item)
    {
        await _gate.WaitAsync();
        try
        {
            var existing = Manifest.Results.FirstOrDefault(x =>
                x.Host.Equals(item.Host, StringComparison.OrdinalIgnoreCase)
                && x.StartedAt.Equals(item.StartedAt));

            if (existing == null)
                Manifest.Results.Add(item);
            else
                Copy(item, existing);

            Manifest.Sites = Manifest.Results.Count;
            Manifest.GeneratedLocal = DateTimeOffset.Now;
            await AtomicWrite.WriteJsonAtomicAsync(_path, Manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(string status, DateTimeOffset? finishedAt = null)
    {
        await _gate.WaitAsync();
        try
        {
            Manifest.Status = status;
            Manifest.FinishedAt = finishedAt ?? DateTimeOffset.Now;
            Manifest.GeneratedLocal = DateTimeOffset.Now;
            Manifest.Sites = Manifest.Results.Count;
            await AtomicWrite.WriteJsonAtomicAsync(_path, Manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public List<RunSiteItem> SnapshotResults()
        => Manifest.Results.ToList();

    private async Task WriteAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Manifest.GeneratedLocal = DateTimeOffset.Now;
            await AtomicWrite.WriteJsonAtomicAsync(_path, Manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Copy(RunSiteItem source, RunSiteItem target)
    {
        target.Host = source.Host;
        target.DisplayName = source.DisplayName;
        target.ViewerRel = source.ViewerRel;
        target.EntryRel = source.EntryRel;
        target.Status = source.Status;
        target.PagesDone = source.PagesDone;
        target.ScreenshotsDone = source.ScreenshotsDone;
        target.Reason = source.Reason;
        target.Note = source.Note;
        target.StartedAt = source.StartedAt;
        target.FinishedAt = source.FinishedAt;
    }
}
