namespace HomeLink.Utils;

/// <summary>
/// Time formatting utility methods
/// </summary>
public static class TimeUtils
{
    /// <summary>
    /// Formats milliseconds to MM:SS format
    /// </summary>
    public static string FormatTime(long milliseconds)
    {
        long totalSeconds = milliseconds / 1000;
        long minutes = totalSeconds / 60;
        long seconds = totalSeconds % 60;
        return $"{minutes:D2}:{seconds:D2}";
    }
}

