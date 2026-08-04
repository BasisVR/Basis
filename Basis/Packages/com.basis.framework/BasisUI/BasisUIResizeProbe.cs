using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Counts <c>OnRectTransformDimensionsChange</c> on the rect it sits on. Attach to a settled
    /// hierarchy and rebuild it: any count above zero means something re-wrote a size without a
    /// real content change — the exact pattern that re-dirties the canvas every frame and can
    /// self-sustain through DelayedSetDirty. Used by the UI layout test suite; costs nothing
    /// unless attached.
    /// </summary>
    [ExecuteAlways]
    public class BasisUIResizeProbe : MonoBehaviour
    {
        public int Resizes;

        private void OnRectTransformDimensionsChange()
        {
            Resizes++;
        }

        public static int AttachToTree(Transform root)
        {
            int count = 0;
            Attach(root, ref count);
            return count;
        }

        private static void Attach(Transform node, ref int count)
        {
            if (node is RectTransform && !node.TryGetComponent(out BasisUIResizeProbe _))
            {
                node.gameObject.AddComponent<BasisUIResizeProbe>();
                count++;
            }
            for (int i = 0; i < node.childCount; i++)
            {
                Attach(node.GetChild(i), ref count);
            }
        }

        public static int TotalResizes(Transform root)
        {
            int total = 0;
            Sum(root, ref total);
            return total;
        }

        private static void Sum(Transform node, ref int total)
        {
            if (node.TryGetComponent(out BasisUIResizeProbe probe))
            {
                total += probe.Resizes;
            }
            for (int i = 0; i < node.childCount; i++)
            {
                Sum(node.GetChild(i), ref total);
            }
        }

        public static void ResetTree(Transform root)
        {
            if (root.TryGetComponent(out BasisUIResizeProbe probe))
            {
                probe.Resizes = 0;
            }
            for (int i = 0; i < root.childCount; i++)
            {
                ResetTree(root.GetChild(i));
            }
        }
    }
}
