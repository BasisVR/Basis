using System;
using System.Globalization;

namespace Basis.BasisUI
{
    public class LibraryProviderStrUtil
    {
        public static string TitleToCase(string givenText)
        {
            TextInfo textInfo = CultureInfo.CurrentCulture.TextInfo;
            return textInfo.ToTitleCase(givenText);
        }

        public static string TimeAgoUtc(DateTime then)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan diff = now - then;

            // Years (approximate: 365 days)
            if (diff.TotalDays >= 365)
            {
                int years = (int)Math.Floor(diff.TotalDays / 365);
                return $"{years}y";
            }

            // Days
            if (diff.TotalDays >= 1)
            {
                int days = (int)Math.Floor(diff.TotalDays);
                return $"{days}d";
            }

            // Hours
            if (diff.TotalHours >= 1)
            {
                int hours = (int)Math.Floor(diff.TotalHours);
                return $"{hours}h";
            }

            // Minutes
            if (diff.TotalMinutes >= 1)
            {
                int minutes = (int)Math.Floor(diff.TotalMinutes);
                return $"{minutes}m";
            }

            // Seconds
            int seconds = (int)Math.Floor(diff.TotalSeconds);
            return $"{seconds}s";
        }


    }

}