using System;
public static class BasisGenerateUniqueID
{
    public static string GenerateUniqueID()
    {
        var guid = Guid.NewGuid();
        var now = DateTime.UtcNow;

        return string.Create(40, (guid, now), static (span, state) =>
        {
            // Write GUID as 32 hex chars (no dashes)
            state.guid.TryFormat(span[..32], out _, "N");

            // Write yyyyMMdd manually (faster than DateTime.ToString)
            int year = state.now.Year;
            int month = state.now.Month;
            int day = state.now.Day;

            span[32] = (char)('0' + (year / 1000) % 10);
            span[33] = (char)('0' + (year / 100) % 10);
            span[34] = (char)('0' + (year / 10) % 10);
            span[35] = (char)('0' + year % 10);

            span[36] = (char)('0' + (month / 10));
            span[37] = (char)('0' + (month % 10));

            span[38] = (char)('0' + (day / 10));
            span[39] = (char)('0' + (day % 10));
        });
    }
}
