using UnityEngine;

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
    public static GameObject ContentControl(GameObject SearchAndDestroy, ChecksRequired ChecksRequired, Vector3 Position, Quaternion Rotation,bool ModifyScale,Vector3 Scale, BundledContentHolder.Selector Selector, Transform Parent = null)
    {
        if (ChecksRequired.UseContentRemoval)
        {
            GameObject newGameObject = new GameObject("Temp");
            newGameObject.SetActive(false);

            SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation, newGameObject.transform);
            if(ModifyScale)
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
                    if (component is Animator animator)
                    {
                        if (ChecksRequired.DisableAnimatorEvents)
                        {
                            animator.fireEvents = false;
                        }
                    }
                    else
                    {
                        if (component is Collider collider)
                        {
                            if (ChecksRequired.RemoveColliders)
                            {
                                GameObject.Destroy(collider);
                            }
                        }
                    }
                        // Check if the component is a MonoBehaviour and not in the approved list
                        if (component is UnityEngine.Component monoBehaviour)
                    {
                        string monoTypeName = monoBehaviour.GetType().FullName;
                        if (!PoliceCheck.selectedTypes.Contains(monoTypeName))
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
            GameObject.DestroyImmediate(newGameObject);

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
}
/// <summary>
/// Defines the checks required for content control.
/// </summary>
public struct ChecksRequired
{
    public bool UseContentRemoval;
    public bool DisableAnimatorEvents;
    public bool RemoveColliders;
}
