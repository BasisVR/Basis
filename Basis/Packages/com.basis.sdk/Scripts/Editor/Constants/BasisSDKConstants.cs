using System.Collections.Generic;
using UnityEditor;

public class BasisSDKConstants
{
    // Common base paths
    private const string BasePath = "Packages/com.basis.sdk/Scripts/Editor/StyleSheets/";
    private const string AvatarFile = "AvatarSDK.uxml";
    private const string PropFile = "PropSDK.uxml";
    private const string SceneFile = "SceneSDK.uxml";
    #region Avatar
    public static readonly string AvataruxmlPath = $"{BasePath}{AvatarFile}";

    public static readonly string avatarEyePositionButton = "AvatarEyePositionButton";
    public static readonly string avatarMouthPositionButton = "AvatarMouthPositionButton";
    public static readonly string AvatarBundleButton = "AvatarBundleButton";
    public static readonly string AvatarAutomaticVisemeDetection = "AvatarAutomaticVisemeDetection";
    public static readonly string AvatarAutomaticBlinkDetection = "AvatarAutomaticBlinkDetection";
    public static readonly string avatarEyePositionField = "AvatarEyePositionField";
    public static readonly string avatarMouthPositionField = "AvatarMouthPositionField";
    public static readonly string AvatarBuildBundle = "AvatarBuildBundle";

    public static readonly string animatorField = "AnimatorField";
    public static readonly string FaceBlinkMeshField = "FaceBlinkMeshField";
    public static readonly string FaceVisemeMeshField = "FaceVisemeMeshField";

    public static readonly string AvatarName = "avatarnameinput";
    public static readonly string AvatarDescription = "avatardescriptioninput";
    public static readonly string AvatarIcon = "avataricon";
    public static readonly string Avatarpassword = "avatarpassword";
    public static readonly string AvatarDocumentationURL = "https://docs.basisvr.org/en/docs/avatar";
    public static readonly string AvatarTestInEditor = "TestInEditor";
    public static readonly string AvatarAnimatorControllerPath = "Packages/com.basis.sdk/Animator/BasisLocomotion.controller";

    public static readonly string AvatarDoNotAutoRenameBonesField = "AvatarDoNotAutoRenameBonesField";
    public static readonly string AvatarAutomaticallyRemoveBlendshapesField = "AvatarAutomaticallyRemoveBlendshapesField";

    public static readonly string NetworkBehavioursFoldout = "NetworkBehavioursFoldout";
    public static readonly string NetworkBehavioursAttached = "NetworkBehavioursAttached";
    public static readonly string NetworkBehavioursAvailable = "NetworkBehavioursAvailable";
    #endregion
    #region Prop
    public static readonly string PropuxmlPath = $"{BasePath}{PropFile}";
    public static readonly string PropIcon = "propicon";
    public static readonly string PropName = "propnameinput";
    public static readonly string PropDescription = "propdescriptioninput";
    public static readonly string PropDocumentationURL = "https://docs.basisvr.org/en/docs/prop";
    #endregion

    #region Scene
    public static readonly string SceneuxmlPath = $"{BasePath}{SceneFile}";
    public static readonly string SceneIcon = "sceneicon";
    public static readonly string SceneName = "scenenameinput";
    public static readonly string SceneDescription = "scenedescriptioninput";
    public static readonly string SceneDocumentationURL = "https://docs.basisvr.org/en/docs/world";
    public static readonly string SpawnPointField = "spawnpointfield";
    public static readonly string MainCameraField = "maincamerafield";
    public static readonly string AudioMixerGroupField = "audiomixergroupfield";
    public static readonly string IsReadyField = "isreadyfield";
    public static readonly string RespawnHeightField = "respawnheightfield";
    public static readonly string RespawnCheckTimerField = "respawnchecktimer";
    public static readonly string SpawnPointGizmoButton = "SpawnPointGizmoButton";
    #endregion
    #region Shared
    public static readonly string ErrorMessage = "ErrorMessage";
    public static readonly string BuildButton = "BuildButton";
    #endregion
    public static List<BuildTarget> allowedTargets = new List<BuildTarget>
    {
        BuildTarget.StandaloneWindows64,
        BuildTarget.StandaloneOSX,
        BuildTarget.StandaloneLinux64,
        BuildTarget.Android,
        BuildTarget.iOS,
    };

    public static Dictionary<BuildTarget, string> targetDisplayNames = new Dictionary<BuildTarget, string>
    {
        { BuildTarget.StandaloneWindows64, "Windows" },
        { BuildTarget.StandaloneOSX, "Mac" },
        { BuildTarget.StandaloneLinux64, "Linux" },
        { BuildTarget.Android, "Android" },
        { BuildTarget.iOS, "iOS" },
    };
    public static List<BuildTarget> OcclusionCullingTargets = new List<BuildTarget>
    {
        BuildTarget.WebGL,
        BuildTarget.Android,
        BuildTarget.iOS,
    };
}
