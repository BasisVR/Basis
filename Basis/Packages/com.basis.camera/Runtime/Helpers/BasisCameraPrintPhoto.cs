using Basis.BasisUI;
using Basis.ImagePickup;
using System;
using System.IO;
using UnityEngine;
public static class BasisCameraPrintPhoto
{
    public static BasisCameraPrintResize.PrintCopy BuildCopy(byte[] pixels, int width, int height, long encodedLength)
    {
        try
        {
            return BasisCameraPrintResize.Build(pixels, width, height, encodedLength);
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning($"Print Photo could not resize the shot to fit the image pickup limits: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
            return default;
        }
    }
    public static void Spawn(string path, BasisCameraPrintResize.PrintCopy printCopy, ref Vector2Int lastNoticeFor)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            BasisDebug.Log("Print Photo skipped: only PNG photos can become image pickups.", BasisDebug.LogTag.Camera);
            return;
        }

        if (!printCopy.Exists)
        {
            BasisImagePickupManager.SpawnFromFile(path);
            return;
        }

        if (!BasisImagePickupManager.SpawnFromImageData(printCopy.Png, Path.GetFileName(path))) return;

        BasisDebug.Log($"Print Photo resized {printCopy.SourceWidth}x{printCopy.SourceHeight} to " + $"{printCopy.Width}x{printCopy.Height} to fit the image pickup limits.", BasisDebug.LogTag.Camera);
        ShowResizedNotice(printCopy, ref lastNoticeFor);
    }
    private static void ShowResizedNotice(BasisCameraPrintResize.PrintCopy printCopy, ref Vector2Int lastNoticeFor)
    {
        Vector2Int shotAt = new Vector2Int(printCopy.SourceWidth, printCopy.SourceHeight);
        if (lastNoticeFor == shotAt) return;
        lastNoticeFor = shotAt;

        string title = BasisLocalization.Get("camera.printPhoto.resized.title");
        string body = BasisLocalization.Get("camera.printPhoto.resized.description", printCopy.SourceWidth, printCopy.SourceHeight, printCopy.Width, printCopy.Height);
        string accept = BasisLocalization.Get("ui.ok");

        if (!BasisNotificationCenter.RouteToNotifications && !BasisMainMenu.Instance) BasisMainMenu.Open();

        BasisMenuDialoguePanel.CreateNew(title, body, accept, (Action<bool>)null, true, BasisPanelSeverity.Calm, BasisNotificationCategory.Content);
    }
}
