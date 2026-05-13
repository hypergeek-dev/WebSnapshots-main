// RunManifest.cs
using System;
using System.Collections.Generic;

namespace WebSnapshots;

public sealed class RunManifest
{
    public string RunId { get; set; } = "";
    public string RunFolderName { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public DateTimeOffset GeneratedLocal { get; set; }
    public int Sites { get; set; }
    public List<RunSiteItem> Results { get; set; } = new();
}

public sealed class RunSiteItem
{
    public string Host { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ViewerRel { get; set; } = "";
    public string Status { get; set; } = "";
    public int PagesDone { get; set; }
}
