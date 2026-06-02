// AppRunner.cs
namespace WebSnapshots;

public static class AppRunner
{
    public static Task RunAsync(
        SnapshotConfig cfg,
        List<string> urls,
        Action<string> uiLog,
        CancellationToken ct,
        PauseController pause)
        => ProductionRunner.RunAsync(cfg, urls, uiLog, ct, pause);
}
