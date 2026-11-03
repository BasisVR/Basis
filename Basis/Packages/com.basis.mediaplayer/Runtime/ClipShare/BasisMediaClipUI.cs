public static class BasisMediaClipUI
{
    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d) seconds = 0d;
        long whole = (long)seconds;
        long hours = whole / 3600, minutes = whole / 60 % 60, secs = whole % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }
}
