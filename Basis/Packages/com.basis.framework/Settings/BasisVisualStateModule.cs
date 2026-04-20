using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using UnityEngine.AddressableAssets;

public class BasisVisualStateModule : BasisSettingsBase
{
    public override void Awake()
    {
        SMModuleDistanceBasedReductions.OnHearingRangeChanged += OnHearingRangeChanged;
        base.Awake();
    }

    private static void OnHearingRangeChanged(float Range)
    {
        if (BasisAdaptiveCircle != null)
        {
            BasisAdaptiveCircle.Apply(Mathf.Sqrt(Range));
        }
    }
    public static BasisAdaptiveCircle BasisAdaptiveCircle;
    /// <summary>
    /// Adaptive Circle Id
    /// </summary>
    public static string AdaptiveCirlceId = "Adaptive Circle Display.prefab";
    /// <summary>
    /// Spawned 
    /// </summary>
    public static GameObject AdaptiveCircleCreated;
    /// <summary>
    /// Addressable 
    /// </summary>
    public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> LocalHandle;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == BasisSettingsDefaults.VisualState.BindingKey)
        {
            switch (optionValue)
            {
                case "all visuals":
                    ShowAvatarDistance();
                    break;
                case "only avatar distance":
                    ShowAvatarDistance();
                    break;
                case "off":
                    DeleteAvatarDistance();
                    break;
            }
        }
    }
    public override void ChangedSettings()
    {
    }
    public static void ShowAvatarDistance()
    {
        if (AdaptiveCircleCreated == null)
        {
            //  BasisDebug.Log("ShowAvatarDistance");
            LocalHandle = Addressables.LoadAssetAsync<GameObject>(AdaptiveCirlceId);
            var InMemory = LocalHandle.WaitForCompletion();
            AdaptiveCircleCreated = GameObject.Instantiate(InMemory, BasisLocalPlayer.Instance.transform);
            AdaptiveCircleCreated.transform.localPosition = Vector3.zero;
        }
        if (AdaptiveCircleCreated.TryGetComponent(out BasisAdaptiveCircle))
        {
            BasisAdaptiveCircle.Apply(Mathf.Sqrt(SMModuleDistanceBasedReductions.HearingRange));
        }
    }
    public static void DeleteAvatarDistance()
    {
        BasisDebug.Log("DeleteAvatarDistance");
        if (LocalHandle.IsValid())
        {
            Addressables.Release(LocalHandle);
        }
        if (AdaptiveCircleCreated != null)
        {
            GameObject.Destroy(AdaptiveCircleCreated.gameObject);
        }
    }
}
