using GatorDragonGames.JigglePhysics;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class BasisJigglePhysicsTestInEditorHook
{
    private const int JiggleResetFrames = 3;

    static BasisJigglePhysicsTestInEditorHook()
    {
        BasisAvatarSDKInspector.OnBeforeTestInEditorOriginalDeactivation -= GetRequiredSettleFrames;
        BasisAvatarSDKInspector.OnBeforeTestInEditorOriginalDeactivation += GetRequiredSettleFrames;
        BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize -= RebuildJiggleCaches;
        BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize += RebuildJiggleCaches;
        BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization -= RebuildJiggleCaches;
        BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization += RebuildJiggleCaches;
    }

    private static void GetRequiredSettleFrames(
        GameObject originalObject,
        BasisAvatarSDKInspector.TestInEditorOriginalDeactivationContext context)
    {
        JiggleRig[] jiggles = originalObject.GetComponentsInChildren<JiggleRig>(false);
        for (int i = 0; i < jiggles.Length; i++)
        {
            JiggleRig jiggle = jiggles[i];
            if (jiggle != null && jiggle.enabled)
            {
                Debug.Log("Enabled Jiggles were found when Test in Editor was entered. The avatar will remain disabled while the test clone is prepared so Jiggle transforms can reset.");
                context.RequestSettleFrames(JiggleResetFrames);
                return;
            }
        }
    }

    private static void RebuildJiggleCaches(GameObject root)
    {
        RebuildJiggleCaches(root, null);
    }

    private static void RebuildJiggleCaches(GameObject root, BasisAssetBundleObject settings)
    {
        if (root == null)
        {
            return;
        }

        JiggleRig[] jiggles = root.GetComponentsInChildren<JiggleRig>(true);
        for (int i = 0; i < jiggles.Length; i++)
        {
            JiggleRig jiggle = jiggles[i];
            if (jiggle != null)
            {
                jiggle.RebuildTransformCache();
            }
        }
    }
}
