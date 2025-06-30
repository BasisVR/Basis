using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using UnityEngine;

[Serializable]
public enum InteractInputState
{
    /// <summary>
    /// Input in scene/initialized, BasisInput & Bone Control is null
    /// </summary>
    NotAdded,
    /// <summary>
    /// Input in scene, not affecting this interactable
    /// </summary>
    Ignored,
    /// <summary>
    /// Input is hovering
    /// </summary>
    Hovering,
    /// <summary>
    /// Input is interacting
    /// </summary>
    Interacting,
}

[Serializable]
public struct BasisInputWrapper
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="source"></param>
    /// <param name="state"></param>
    /// <param name="wrapper"></param>
    /// <returns>true: added and tracking, false: not added or tracking (default struct)</returns>
    public static bool TryNewTracking(BasisInput source, InteractInputState state, out BasisInputWrapper wrapper)
    {
        wrapper = default;
        if (state == InteractInputState.NotAdded)
            return false;
        // UnityEngine.Debug.Log($"New Input: {source != null}, {source.TryGetRole(out BasisBoneTrackedRole r)}, {BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl c, r)}, {r}", source.gameObject);
        if (source != null &&
            source.TryGetRole(out BasisBoneTrackedRole role) &&
            BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl control, role)
        )
        {
            wrapper.Source = source;
            wrapper.BoneControl = control;
            wrapper.State = state;
            wrapper.Role = role;
            return true;
        }
        return false;
    }

    public BasisInput Source;

    public BasisLocalBoneControl BoneControl { get; set; }
    public BasisBoneTrackedRole Role { get; set; }

    [SerializeField]
    // TODO: this should not be editable in editor, useful for debugging for now tho
    private InteractInputState State;

    public InteractInputState GetState()
    {
        return State;
    }

    public bool TrySetState(InteractInputState newState)
    {
        if (State == InteractInputState.NotAdded)
            return false;
        State = newState;
        return true;
    }

    public readonly bool IsInput(BasisInput input)
    {
        if (input == null)
            return false;
        return State != InteractInputState.NotAdded && Source.UniqueDeviceIdentifier == input.UniqueDeviceIdentifier;
    }
}
