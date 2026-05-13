using System.Threading;

namespace WebSnapshots;

public sealed class PauseController
{
    private readonly ManualResetEventSlim _gate = new(true);

    public static PauseController Noop { get; } = new PauseController(alwaysRunning: true);

    private readonly bool _alwaysRunning;

    public PauseController()
    {
        _alwaysRunning = false;
    }

    private PauseController(bool alwaysRunning)
    {
        _alwaysRunning = alwaysRunning;
    }

    public bool IsPaused => !_alwaysRunning && !_gate.IsSet;

    public void Pause()
    {
        if (_alwaysRunning) return;
        _gate.Reset();
    }

    public void Resume()
    {
        if (_alwaysRunning) return;
        _gate.Set();
    }

    public void WaitIfPaused(CancellationToken ct)
    {
        if (_alwaysRunning) return;
        _gate.Wait(ct);
    }
}
