using UnityEngine;
using UnityEngine.SceneManagement;

public static class ContentPoliceControl
{
    /// <summary>
    /// Creates a copy of a GameObject, removes any unapproved MonoBehaviours, and returns the cleaned copy through instantiation. 
    /// </summary>
    /// <param name="SearchAndDestroy">The original GameObject to copy and clean.</param>
    /// <param name="ChecksRequired">Whether to remove unapproved MonoBehaviours or not.</param>
    /// <param name="Position">The position to instantiate the cleaned copy.</param>
    /// <param name="Rotation">The rotation to instantiate the cleaned copy.</param>
    /// <param name="Parent">The parent transform for the instantiated copy. Defaults to null.</param>
    /// <returns>A copy of the GameObject with unapproved scripts removed.</returns>
    public static GameObject ContentControl(GameObject DisabledGameobject, GameObject SearchAndDestroy, ChecksRequired ChecksRequired, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, BundledContentHolder.Selector Selector, Transform Parent = null,int colliderlayer = -1)
    {
        if (ChecksRequired.UseContentRemoval)
        {
            SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation, DisabledGameobject.transform);
            if (ModifyScale)
            {
                BasisDebug.Log($"Overriding Default scale is now {Scale} for Game object {SearchAndDestroy.name}");
                SearchAndDestroy.transform.localScale = Scale;
            }
            // Create a list to hold all components in the original GameObject
            UnityEngine.Component[] components = SearchAndDestroy.GetComponentsInChildren<UnityEngine.Component>(true);

            int count = components.Length;

            if (BundledContentHolder.Instance.GetSelector(Selector, out ContentPoliceSelector PoliceCheck))
            {
                for (int Index = 0; Index < count; Index++)
                {
                    Component component = components[Index];
                    //do this first before we nuke stuff
                    switch (component)
                    {
                        case Animator animator:
                            if (ChecksRequired.DisableAnimatorEvents)
                            {
                                animator.fireEvents = false;
                            }
                            break;
                        case Collider collider:

                            if (ChecksRequired.RemoveColliders)
                            {
                                BasisDebug.Log("Remove Collider ", BasisDebug.LogTag.Avatar);
                                GameObject.Destroy(collider);
                            }
                            else
                            {
                                if (ChecksRequired.ChangeCollidersToCorrectLayer)
                                {
                                    BasisDebug.Log("Changing Collider To Correct Layer", BasisDebug.LogTag.Avatar);
                                    collider.gameObject.layer = colliderlayer;
                                }
                            }
                            break;
                        case AudioSource source:
                            source.outputAudioMixerGroup = PoliceCheck.AudioMixer;
                            break;
                    }
                    // Check if the component is a MonoBehaviour and not in the approved list
                    if (component is UnityEngine.Component monoBehaviour)
                    {
                        string monoTypeName = monoBehaviour.GetType().FullName;
                        if (!PoliceCheck.ApprovedTypeNames.Contains(monoTypeName))
                        {
                            Debug.LogError($"MonoBehaviour {monoTypeName} is not approved and will be removed.");
                            GameObject.DestroyImmediate(monoBehaviour); // Destroy the unapproved MonoBehaviour immediately
                        }
                    }
                }
                // Instantiate the cleaned GameObject copy
                if (Parent == null)
                {
                    SearchAndDestroy.transform.parent = null;
                    SearchAndDestroy.SetActive(true);
                }
                else
                {
                    SearchAndDestroy.transform.parent = Parent;
                    SearchAndDestroy.SetActive(true);
                }
            }
            else
            {
                BasisDebug.LogError("cant find Police check for " + Selector, BasisDebug.LogTag.Event);
            }
        }
        else
        {
            if (Parent == null)
            {
                SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation);
            }
            else
            {
                SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation, Parent);
            }
        }
        return SearchAndDestroy;
    }
    /// <summary>
    /// Scrubs a scene by removing any unapproved MonoBehaviours and applying optional safety checks.
    /// </summary>
    public static void ContentControl(ChecksRequired checks, BundledContentHolder.Selector selector, Scene targetScene, bool includeInactive = true)
    {
        if (!checks.UseContentRemoval)
        {
            return;
        }

        if (!BundledContentHolder.Instance.GetSelector(selector, out ContentPoliceSelector policeCheck))
        {
            BasisDebug.LogError("Can't find Police check for " + selector, BasisDebug.LogTag.Event);
            return;
        }
        if (!targetScene.IsValid() || !targetScene.isLoaded)
        {
            Debug.LogError("Target scene is not valid or not loaded.");
            return;
        }

        GameObject[] roots = targetScene.GetRootGameObjects();
        for (int RootIndex = 0; RootIndex < roots.Length; RootIndex++)
        {
            // Get ALL components in this subtree
            Component[] components = roots[RootIndex].transform.GetComponentsInChildren<Component>(includeInactive);
            // Check if the component is a MonoBehaviour and not in the approved list
            for (int ComponentIndex = 0; ComponentIndex < components.Length; ComponentIndex++)
            {
                Component component = components[ComponentIndex];
                //do this first before we nuke stuff
                // Check if the component is a MonoBehaviour and not in the approved list
                if (component is UnityEngine.Component monoBehaviour)
                {
                    string monoTypeName = monoBehaviour.GetType().FullName;
                    if (!policeCheck.ApprovedTypeNames.Contains(monoTypeName))
                    {
                        Debug.LogError($"MonoBehaviour {monoTypeName} is not approved and will be removed.");
                        GameObject.DestroyImmediate(monoBehaviour); // Destroy the unapproved MonoBehaviour immediately
                    }
                }
            }
        }
    }
}
/// <summary>
/// Defines the checks required for content control.
/// </summary>
public struct ChecksRequired
{
    public bool UseContentRemoval;
    public bool DisableAnimatorEvents;
    public bool RemoveColliders;
    public bool ChangeCollidersToCorrectLayer;
    public ChecksRequired(bool useContentRemoval, bool disableAnimatorEvents, bool removeColliders,bool changeColidersToCorrectLayer)
    {
        UseContentRemoval = useContentRemoval;
        DisableAnimatorEvents = disableAnimatorEvents;
        RemoveColliders = removeColliders;
        ChangeCollidersToCorrectLayer = changeColidersToCorrectLayer;
    }
}
