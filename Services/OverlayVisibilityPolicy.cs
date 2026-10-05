namespace SpeedBar.Services;

/// <summary>Hides immediately, but restores only after an uninterrupted safe interval.</summary>
public sealed class OverlayVisibilityPolicy
{
    public const long RestoreDelayMilliseconds = 800;

    private long _clearSinceMilliseconds;
    private bool _hasClearSample;

    /// <param name="nowMilliseconds">Elapsed time from a monotonic clock.</param>
    public bool ShouldShow(bool blocked, long nowMilliseconds)
    {
        if (blocked)
        {
            Reset();
            return false;
        }

        if (!_hasClearSample || nowMilliseconds < _clearSinceMilliseconds)
        {
            _clearSinceMilliseconds = nowMilliseconds;
            _hasClearSample = true;
            return false;
        }

        return nowMilliseconds - _clearSinceMilliseconds >= RestoreDelayMilliseconds;
    }

    public void Reset()
    {
        _hasClearSample = false;
        _clearSinceMilliseconds = 0;
    }
}
