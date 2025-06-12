using System;

public static class BasisGenerateUniqueID
{
    /// <summary>
    /// Generates a unique ID using a GUID and the current UTC date (yyyyMMdd).
    /// </summary>
    /// <returns>A unique identifier combining a GUID and UTC date.</returns>
    public static string GenerateUniqueID()
    {
        Guid newGuid = Guid.NewGuid();  // Generate a new GUID
        string utcDate = DateTime.UtcNow.ToString("yyyyMMdd");  // Get the current UTC date (YYYYMMDD)
        string guid = newGuid.ToString("N"); // Remove dashes from GUID

        return $"{guid}{utcDate}";
    }
}
