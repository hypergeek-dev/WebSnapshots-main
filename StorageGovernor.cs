// StorageGovernor.cs - Optimized with incremental tracking
namespace WebSnapshots;

public sealed class StorageGovernor
{
    private readonly long _capBytes;
    private readonly object _lock = new();
    private long _estimatedBytes = 0;
    private long _lastFullCheckBytes = 0;
    private DateTime _lastFullCheck = DateTime.MinValue;

    // Recalibrate every 5 minutes to correct drift
    private static readonly TimeSpan RecalibrationInterval = TimeSpan.FromMinutes(5);

    public StorageGovernor(long capBytes)
    {
        _capBytes = capBytes;
    }

    public long CapBytes => _capBytes;

    /// <summary>
    /// Register a file write. This updates the estimated size immediately.
    /// </summary>
    public void RegisterWrite(long bytesWritten)
    {
        if (bytesWritten <= 0) return;

        lock (_lock)
        {
            _estimatedBytes += bytesWritten;

            if (_estimatedBytes >= _capBytes)
            {
                throw new StorageCapReachedException(
                    $"Storage cap reached: ~{_estimatedBytes:N0} bytes >= {_capBytes:N0} bytes");
            }
        }
    }

    /// <summary>
    /// Quick check using estimated size (no disk I/O)
    /// </summary>
    public void ThrowIfOverCapEstimated()
    {
        lock (_lock)
        {
            if (_estimatedBytes >= _capBytes)
            {
                throw new StorageCapReachedException(
                    $"Storage cap reached: ~{_estimatedBytes:N0} bytes >= {_capBytes:N0} bytes");
            }
        }
    }

    /// <summary>
    /// Full disk check - use sparingly (expensive I/O operation)
    /// </summary>
    public void ThrowIfOverCap(long totalBytesOnDisk)
    {
        lock (_lock)
        {
            _lastFullCheckBytes = totalBytesOnDisk;
            _lastFullCheck = DateTime.UtcNow;

            // Update estimate to match reality
            _estimatedBytes = totalBytesOnDisk;

            if (totalBytesOnDisk >= _capBytes)
            {
                throw new StorageCapReachedException(
                    $"Storage cap reached: {totalBytesOnDisk:N0} bytes >= {_capBytes:N0} bytes");
            }
        }
    }

    /// <summary>
    /// Recalibrate if needed (checks interval), otherwise uses estimate
    /// </summary>
    public void CheckCapSmart(string outputDir)
    {
        lock (_lock)
        {
            var elapsed = DateTime.UtcNow - _lastFullCheck;

            if (elapsed >= RecalibrationInterval)
            {
                // Time for full check
                var actual = Utils.GetDirectorySizeBytes(outputDir);
                ThrowIfOverCap(actual);
            }
            else
            {
                // Use estimate
                ThrowIfOverCapEstimated();
            }
        }
    }

    /// <summary>
    /// Safe wrapper used by NavCrawler. Returns false instead of throwing.
    /// </summary>
    public bool CanContinue(string outputDir)
    {
        try
        {
            CheckCapSmart(outputDir);
            return true;
        }
        catch (StorageCapReachedException)
        {
            return false;
        }
    }

    public long GetEstimatedBytes()
    {
        lock (_lock)
        {
            return _estimatedBytes;
        }
    }

    public long GetLastCheckedBytes()
    {
        lock (_lock)
        {
            return _lastFullCheckBytes;
        }
    }

    /// <summary>
    /// Force immediate recalibration from disk
    /// </summary>
    public void Recalibrate(string outputDir)
    {
        var actual = Utils.GetDirectorySizeBytes(outputDir);
        lock (_lock)
        {
            _estimatedBytes = actual;
            _lastFullCheckBytes = actual;
            _lastFullCheck = DateTime.UtcNow;
        }
    }
}

public sealed class StorageCapReachedException : Exception
{
    public StorageCapReachedException(string message) : base(message) { }
}