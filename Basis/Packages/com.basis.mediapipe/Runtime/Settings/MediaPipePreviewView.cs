using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.UI;
namespace Basis.MediaPipe
{
    public sealed class MediaPipePreviewView : MonoBehaviour
    {
        public const float DefaultHeight = 240f, StatusInterval = 0.5f;
        public event System.Action<string> StatusChanged;
        private static readonly Color Blank = new Color(0.08f, 0.08f, 0.08f, 1f);
        private RawImage image;
        private MediaPipePreviewOverlay overlay;
        private float statusTimer;
        private string lastStatus;
        public static MediaPipePreviewView Create(RectTransform parent, float height = DefaultHeight)
        {
            GameObject hosting = new GameObject("Webcam Preview", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage), typeof(LayoutElement));
            hosting.layer = parent != null ? parent.gameObject.layer : hosting.layer;
            RectTransform rect = (RectTransform)hosting.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(parent != null ? parent.rect.width : 0f, height);
            LayoutElement layout = hosting.GetComponent<LayoutElement>();
            layout.preferredHeight = height;
            layout.minHeight = height;
            layout.flexibleWidth = 1f;
            RawImage raw = hosting.GetComponent<RawImage>();
            raw.raycastTarget = false;
            raw.color = Blank;
            GameObject marks = new GameObject("Landmarks", typeof(RectTransform), typeof(CanvasRenderer), typeof(MediaPipePreviewOverlay));
            marks.layer = hosting.layer;
            RectTransform marksRect = (RectTransform)marks.transform;
            marksRect.SetParent(rect, false);
            marksRect.anchorMin = Vector2.zero;
            marksRect.anchorMax = Vector2.one;
            marksRect.offsetMin = Vector2.zero;
            marksRect.offsetMax = Vector2.zero;
            MediaPipePreviewView view = hosting.AddComponent<MediaPipePreviewView>();
            view.image = raw;
            view.overlay = marks.GetComponent<MediaPipePreviewOverlay>();
            view.overlay.raycastTarget = false;
            return view;
        }
        private void OnEnable()
        {
            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += Tick;
            statusTimer = StatusInterval;
            lastStatus = null;
            Tick();
        }
        private void OnDisable()
        {
            BasisFrameClock.OnTick -= Tick;
            BasisFrameClock.RemoveRequest();
            if (image != null) image.texture = null;
        }
        private void Tick()
        {
            if (image == null || overlay == null) return;
            BasisMediaPipeManagement manager = BasisMediaPipeManagement.Instance;
            WebCamTexture texture = manager != null && manager.IsRunning ? manager.Camera.Texture : null;
            bool live = texture != null && texture.width > 16;
            Texture shown = live ? texture : null;
            if (image.texture != shown) image.texture = shown;
            image.color = live ? Color.white : Blank;
            bool mirror = manager != null && manager.MirrorPreview, flipVertical = live && texture.videoVerticallyMirrored;
            image.uvRect = new Rect(mirror ? 1f : 0f, flipVertical ? 1f : 0f, mirror ? -1f : 1f, flipVertical ? -1f : 1f);
            overlay.Show(manager, mirror, flipVertical);
            statusTimer += Time.unscaledDeltaTime;
            if (statusTimer < StatusInterval) return;
            statusTimer = 0f;
            string status = manager != null ? manager.StatusKey : "settings.mediapipe.status.off";
            if (status == lastStatus) return;
            lastStatus = status;
            StatusChanged?.Invoke(status);
        }
    }
    public sealed class MediaPipePreviewOverlay : MaskableGraphic
    {
        private static readonly int[] PoseBones = { 11, 12, 11, 13, 13, 15, 12, 14, 14, 16, 11, 23, 12, 24, 23, 24, 15, 19, 15, 17, 16, 20, 16, 18, 7, 8 };
        private static readonly int[] HandBones = { 0, 1, 1, 2, 2, 3, 3, 4, 0, 5, 5, 6, 6, 7, 7, 8, 5, 9, 9, 10, 10, 11, 11, 12, 9, 13, 13, 14, 14, 15, 15, 16, 13, 17, 17, 18, 18, 19, 19, 20, 0, 17 };
        private static readonly Color PoseColor = new Color(0.35f, 1f, 0.45f, 0.9f), LeftHandColor = new Color(0.4f, 0.8f, 1f, 0.95f), RightHandColor = new Color(1f, 0.65f, 0.3f, 0.95f), FaceColor = new Color(1f, 0.9f, 0.3f, 0.9f), GazeColor = new Color(1f, 0.3f, 0.5f, 0.95f);
        private readonly Vector3[] pose = new Vector3[MediaPipeSpace.PoseCount], leftHand = new Vector3[MediaPipeSpace.HandCount], rightHand = new Vector3[MediaPipeSpace.HandCount];
        private readonly float[] visibility = new float[MediaPipeSpace.PoseCount];
        private bool hasPose, hasVisibility, hasLeft, hasRight, hasFace, hasGaze, mirror, flipVertical;
        private Vector2 face, gaze;
        private float faceSize;
        public void Show(BasisMediaPipeManagement manager, bool mirrored, bool flippedVertically)
        {
            mirror = mirrored;
            flipVertical = flippedVertically;
            hasPose = hasLeft = hasRight = hasFace = hasGaze = false;
            if (manager != null && manager.HasResult)
            {
                BasisMediaPipeResult result = manager.LatestResult;
                hasPose = result.HasPose && Copy(result.PoseLandmarks, pose, MediaPipeSpace.PoseCount);
                hasVisibility = hasPose && result.PoseVisibility != null && result.PoseVisibility.Length >= MediaPipeSpace.PoseCount;
                if (hasVisibility) System.Array.Copy(result.PoseVisibility, visibility, MediaPipeSpace.PoseCount);
                hasLeft = result.HasLeftHand && Copy(result.LeftHandLandmarks, leftHand, MediaPipeSpace.HandCount);
                hasRight = result.HasRightHand && Copy(result.RightHandLandmarks, rightHand, MediaPipeSpace.HandCount);
                hasFace = result.HasFace && result.FaceImageSize > 0f;
                face = result.HeadImagePosition;
                faceSize = result.FaceImageSize;
                hasGaze = hasFace && (result.HasLeftGaze || result.HasRightGaze);
                gaze = result.HasLeftGaze && result.HasRightGaze ? (result.LeftEyeGaze + result.RightEyeGaze) * 0.5f : result.HasLeftGaze ? result.LeftEyeGaze : result.RightEyeGaze;
            }
            SetVerticesDirty();
        }
        private static bool Copy(Vector3[] source, Vector3[] target, int count)
        {
            if (source == null || source.Length < count) return false;
            System.Array.Copy(source, target, count);
            return true;
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            float thin = Mathf.Max(1.5f, r.height * 0.006f), dot = thin * 2f;
            if (hasPose)
            {
                for (int i = 0; i < PoseBones.Length; i += 2)
                {
                    int a = PoseBones[i], b = PoseBones[i + 1];
                    if (!Visible(a) || !Visible(b)) continue;
                    Line(vh, Map(pose[a], r), Map(pose[b], r), thin, PoseColor);
                }
                for (int i = 0; i < MediaPipeSpace.PoseCount; i++)
                {
                    if (Visible(i)) Dot(vh, Map(pose[i], r), dot, PoseColor);
                }
            }
            if (hasLeft) Hand(vh, leftHand, r, thin, LeftHandColor);
            if (hasRight) Hand(vh, rightHand, r, thin, RightHandColor);
            if (hasFace)
            {
                Vector2 c = Map(face, r);
                float half = faceSize * r.height * 0.7f;
                Box(vh, c, half, thin, FaceColor);
                if (hasGaze) Line(vh, c, c + new Vector2(gaze.x, gaze.y) * half, thin * 1.5f, GazeColor);
            }
        }
        private bool Visible(int index) => !hasVisibility || visibility[index] > 0.5f;
        private Vector2 Map(Vector3 p, Rect r)
        {
            float x = mirror ? 1f - p.x : p.x, y = flipVertical ? 1f - p.y : p.y;
            return new Vector2(r.xMin + Mathf.Clamp01(x) * r.width, r.yMin + Mathf.Clamp01(y) * r.height);
        }
        private void Hand(VertexHelper vh, Vector3[] hand, Rect r, float thin, Color color)
        {
            for (int i = 0; i < HandBones.Length; i += 2)
            {
                Line(vh, Map(hand[HandBones[i]], r), Map(hand[HandBones[i + 1]], r), thin, color);
            }
        }
        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, float thickness, Color color)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (thickness * 0.5f);
            Quad(vh, a - n, a + n, b + n, b - n, color);
        }
        private static void Dot(VertexHelper vh, Vector2 c, float size, Color color)
        {
            float h = size * 0.5f;
            Quad(vh, c + new Vector2(-h, -h), c + new Vector2(-h, h), c + new Vector2(h, h), c + new Vector2(h, -h), color);
        }
        private static void Box(VertexHelper vh, Vector2 c, float half, float thickness, Color color)
        {
            Vector2 a = c + new Vector2(-half, -half), b = c + new Vector2(-half, half), d = c + new Vector2(half, half), e = c + new Vector2(half, -half);
            Line(vh, a, b, thickness, color);
            Line(vh, b, d, thickness, color);
            Line(vh, d, e, thickness, color);
            Line(vh, e, a, thickness, color);
        }
        private static void Quad(VertexHelper vh, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(p0, color, Vector2.zero);
            vh.AddVert(p1, color, Vector2.zero);
            vh.AddVert(p2, color, Vector2.zero);
            vh.AddVert(p3, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i + 2, i + 3, i);
        }
    }
}
