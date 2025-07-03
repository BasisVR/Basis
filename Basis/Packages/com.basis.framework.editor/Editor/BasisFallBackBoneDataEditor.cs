using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(BasisFallBackBoneData))]
public class BasisFallBackBoneDataEditor : Editor
{
    public GameObject BoneInformation;
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Display default inspector property fields
        DrawDefaultInspector();

        // Access the scriptable object
        BasisFallBackBoneData fallBackBoneData = (BasisFallBackBoneData)target;
        BoneInformation = (GameObject)EditorGUILayout.ObjectField(BoneInformation, typeof(GameObject), true);

        // Add a button to do custom functionality
        if (GUILayout.Button("Load FallBack Bone Data"))
        {
            fallBackBoneData.FallBackPercentage.Clear();
            fallBackBoneData.BoneTrackedRoles.Clear();
            ComputeEyeBone(fallBackBoneData);
            LoadFallBackData(fallBackBoneData);
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void LoadFallBackData(BasisFallBackBoneData fallBackBoneData)
    {
        if (BoneInformation != null)
        {
            if (BoneInformation.TryGetComponent(out Animator Anim))
            {
                Bounds bounds = GetBounds(BoneInformation.transform);

                for (int index = 0; index < 55; index++)
                {
                    HumanBodyBones bone = (HumanBodyBones)index;
                    if (BasisAvatarDriver.TryConvertToBoneTrackingRole(bone, out BasisBoneTrackedRole role))
                    {
                        Transform boneTransform = Anim.GetBoneTransform(bone);
                        BasisFallBone fallbackedBone = CreateFallbackBone(bone, role, bounds, boneTransform);

                        fallBackBoneData.FallBackPercentage.Add(fallbackedBone);
                        fallBackBoneData.BoneTrackedRoles.Add(role);
                    }
                }
            }
        }
    }
    private void ComputeEyeBone(BasisFallBackBoneData fallBackBoneData)
    {
        BasisFallBone EyeBone = new BasisFallBone
        {
            HumanBone =  HumanBodyBones.LeftEye,
            Role = BasisBoneTrackedRole.CenterEye
        };
        EyeBone.Position = new Vector3(0,1.6f,0);
        EyeBone.PositionPercentage = CalculatePercentage(Vector3.zero,new Vector3(0, BasisLocalPlayer.FallbackSize, 0), EyeBone.Position);
        fallBackBoneData.BoneTrackedRoles.Add( BasisBoneTrackedRole.CenterEye);
        fallBackBoneData.FallBackPercentage.Add(EyeBone);
    }
    private BasisFallBone CreateFallbackBone(HumanBodyBones bone, BasisBoneTrackedRole role, Bounds bounds, Transform boneTransform)
    {
        BasisFallBone fallbackedBone = new BasisFallBone
        {
            HumanBone = bone,
            Role = role
        };

        if (boneTransform != null)
        {
            fallbackedBone.Position = boneTransform.position;
            fallbackedBone.PositionPercentage = CalculatePercentage(bounds.min, bounds.max, boneTransform.position);
        }
        else
        {
            BasisDebug.Log("we found HumanBone But not Transform : " + bone);
        }

        return fallbackedBone;
    }
    public static Vector3 CalculatePercentage(Vector3 Min, Vector3 Max, Vector3 CurrentHeight)
    {
        BasisDebug.Log("Finding Percentage " + Min + " " + Max + " current height " + CurrentHeight);
        float X = CalculatePercentage(Min.x, Max.x, CurrentHeight.x);
        float Y = CalculatePercentage(Min.y, Max.y, CurrentHeight.y);
        float Z = CalculatePercentage(Min.z, Max.z, CurrentHeight.z);
        return new Vector3(X, Y, Z);
    }
    public static float CalculatePercentage(float Min, float Max, float CurrentHeight)
    {
        return Mathf.Clamp01((CurrentHeight - Min) / (Max - Min));
    }
    public Bounds GetBounds(Transform Animatorparent)
    {
        // Get all renderers in the parent GameObject
        Renderer[] renderers = Animatorparent.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return new Bounds(Vector3.zero, new Vector3(0.3f, BasisLocalPlayer.FallbackSize, 0.3f));
        }
        Bounds bounds = renderers[0].bounds;
        for (int Index = 1; Index < renderers.Length; Index++)
        {
            bounds.Encapsulate(renderers[Index].bounds);
        }
        return bounds;
    }
}
