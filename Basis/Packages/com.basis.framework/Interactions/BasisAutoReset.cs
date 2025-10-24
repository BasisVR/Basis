// File: `MoveAfterDelay.cs`
using UnityEngine;
using System.Collections;

public class MoveAfterDelay : MonoBehaviour
{
    [Tooltip("Target world position to move to.")]
    public Vector3 targetPosition;

    [Tooltip("Delay in seconds before moving.")]
    public float delay = 3f;

    [Tooltip("If > 0, the object will interpolate to the target over this duration; if 0, it will jump instantly.")]
    public float duration = 0f;

    [Tooltip("Use localPosition instead of world position.")]
    public bool useLocalPosition = false;

    void Start()
    {
        StartCoroutine(MoveAfterDelayCoroutine());
    }

    IEnumerator MoveAfterDelayCoroutine()
    {
        yield return new WaitForSeconds(delay);

        if (duration <= 0f)
        {
            if (useLocalPosition) transform.localPosition = targetPosition;
            else transform.position = targetPosition;
            yield break;
        }

        float elapsed = 0f;
        Vector3 start = useLocalPosition ? transform.localPosition : transform.position;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            Vector3 pos = Vector3.Lerp(start, targetPosition, t);
            if (useLocalPosition) transform.localPosition = pos;
            else transform.position = pos;
            yield return null;
        }

        // Ensure final position exactly
        if (useLocalPosition) transform.localPosition = targetPosition;
        else transform.position = targetPosition;
    }
}
