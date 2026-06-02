// RunManifest.cs
using System;
using System.Collections.Generic;

namespace WebSnapshots;

public sealed class RunManifest
{
    public string RunId { get; set; } = "";
    public string RunFolderName { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public string Status { get; set; } = "PARTIAL";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset GeneratedLocal { get; set; }
    public int Sites { get; set; }
    public int SitesPlanned { get; set; }
    public Dictionary<string, object?> Settings { get; set; } = new();
    public List<RunSiteItem> Results { get; set; } = new();
}

public sealed class RunSiteItem
{
    public string Host { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ViewerRel { get; set; } = "";
    public string EntryRel { get; set; } = "";
    public string Status { get; set; } = "";
    public int PagesDone { get; set; }
    public int ScreenshotsDone { get; set; }
    public string Reason { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
