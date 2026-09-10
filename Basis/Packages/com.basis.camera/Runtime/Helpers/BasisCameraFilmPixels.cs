using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
public static class BasisCameraFilmPixels
{
    public static void BurnLeak(NativeArray<byte> pixels, int width, int height, int edge, int depth, float strength, Color32 fog)
    {
        int xMin = edge == 1 ? Mathf.Max(0, width - depth) : 0, xMax = edge == 0 ? Mathf.Min(width, depth) : width;
        int yMin = edge == 3 ? Mathf.Max(0, height - depth) : 0, yMax = edge == 2 ? Mathf.Min(height, depth) : height;

        for (int y = yMin; y < yMax; y++)
        {
            int row = y * width;
            for (int x = xMin; x < xMax; x++)
            {
                int distance;
                switch (edge)
                {
                    case 0: distance = x; break;
                    case 1: distance = width - 1 - x; break;
                    case 2: distance = y; break;
                    default: distance = height - 1 - y; break;
                }

                float amount = BasisCameraPrintFinish.LeakFalloff(distance, depth) * strength;
                if (amount <= 0f) continue;

                int offset = (row + x) * 4;
                pixels[offset] = AddExposure(pixels[offset], fog.r, amount);
                pixels[offset + 1] = AddExposure(pixels[offset + 1], fog.g, amount);
                pixels[offset + 2] = AddExposure(pixels[offset + 2], fog.b, amount);
            }
        }
    }
    public static void FillGlyphs(NativeArray<byte> pixels, int width, int height, List<RectInt> glyphs, Color32 ink)
    {
        for (int Index = 0; Index < glyphs.Count; Index++)
        {
            RectInt rect = glyphs[Index];
            int xMin = Mathf.Max(0, rect.xMin), xMax = Mathf.Min(width, rect.xMax);
            int yMin = Mathf.Max(0, rect.yMin), yMax = Mathf.Min(height, rect.yMax);

            for (int y = yMin; y < yMax; y++)
            {
                int row = y * width;
                for (int x = xMin; x < xMax; x++)
                {
                    int offset = (row + x) * 4;
                    pixels[offset] = ink.r;
                    pixels[offset + 1] = ink.g;
                    pixels[offset + 2] = ink.b;
                    pixels[offset + 3] = ink.a;
                }
            }
        }
    }
    public static void Mount(NativeArray<byte> source, NativeArray<byte> sheet, RectInt window, int printWidth, int printHeight, ref byte[] borderRow)
    {
        Color32 stock = BasisCameraPrintFinish.InstantBorderColour;
        int rowBytes = printWidth * 4;
        if (borderRow == null || borderRow.Length != rowBytes)
        {
            borderRow = new byte[rowBytes];
            for (int x = 0; x < printWidth; x++)
            {
                int offset = x * 4;
                borderRow[offset] = stock.r;
                borderRow[offset + 1] = stock.g;
                borderRow[offset + 2] = stock.b;
                borderRow[offset + 3] = 255;
            }
        }

        int leftBytes = window.xMin * 4, windowBytes = window.width * 4, rightStart = window.xMax * 4;
        int rightBytes = rowBytes - rightStart;

        for (int y = 0; y < printHeight; y++)
        {
            int sheetRow = y * rowBytes;

            if (y < window.yMin || y >= window.yMax)
            {
                NativeArray<byte>.Copy(borderRow, 0, sheet, sheetRow, rowBytes);
                continue;
            }

            if (leftBytes > 0) NativeArray<byte>.Copy(borderRow, 0, sheet, sheetRow, leftBytes);
            NativeArray<byte>.Copy(source, (y - window.yMin) * windowBytes, sheet, sheetRow + leftBytes, windowBytes);
            if (rightBytes > 0) NativeArray<byte>.Copy(borderRow, rightStart, sheet, sheetRow + rightStart, rightBytes);
        }
    }
    private static byte AddExposure(byte channel, byte light, float amount) => (byte)Mathf.Min(255, channel + Mathf.RoundToInt(light * amount));
}
