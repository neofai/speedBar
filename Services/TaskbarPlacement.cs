namespace SpeedBar.Services;

public static class TaskbarPlacement
{
    public static int? FindFreePosition(int left, int right, IEnumerable<HorizontalRange> occupied,
        int width, int margin, bool dockLeft)
    {
        if (right <= left || width <= 0) return null;
        margin = Math.Max(0, margin);
        var ranges = occupied
            .Where(r => double.IsFinite(r.Left) && double.IsFinite(r.Right))
            .Select(r => new HorizontalRange(Math.Clamp(r.Left, left, right), Math.Clamp(r.Right, left, right)))
            .Where(r => r.Right > r.Left).OrderBy(r => r.Left);
        double cursor = left;
        int? candidate = null;
        foreach (var range in ranges.Append(new HorizontalRange(right, right)))
        {
            var firstPixel = Math.Ceiling(cursor + margin);
            var lastPixel = Math.Floor(range.Left - margin - width);
            if (firstPixel <= lastPixel)
            {
                if (dockLeft) return (int)firstPixel;
                candidate = (int)lastPixel;
            }
            cursor = Math.Max(cursor, range.Right);
        }
        return candidate;
    }
}
