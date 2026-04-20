using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Sprites;

namespace Basis.BasisUI
{
    /// <summary>
    /// When "Preserve Aspect" is enabled, the sprite will be fit and culled to expand into the full Image's space.
    /// Useful for background images.
    /// While heavily customized, the layout math is admittedly ChatGPT's work.
    /// </summary>
    public class BasisImageBackground : Image
    {
        protected override void OnPopulateMesh(VertexHelper helper)
        {
            Sprite spr = overrideSprite;
            if (!spr)
            {
                // Don't create a colored background without a sprite.
                /*
                base.OnPopulateMesh(helper);
                return;
                */
                helper.Clear();
                return;
            }

            // Only handle Simple + preserveAspect here; otherwise behave like normal Image.
            if (type != Type.Simple || !preserveAspect)
            {
                base.OnPopulateMesh(helper);
                return;
            }

            Rect rect = GetPixelAdjustedRect();
            if (rect.width <= 0f || rect.height <= 0f)
            {
                helper.Clear();
                return;
            }

            Vector2 spriteSize = spr.rect.size;
            if (spriteSize.sqrMagnitude <= 0f)
            {
                base.OnPopulateMesh(helper);
                return;
            }

            float scaleX = rect.width / spriteSize.x;
            float scaleY = rect.height / spriteSize.y;
            float scale = Mathf.Max(scaleX, scaleY);

            float fullWidth = spriteSize.x * scale;
            float fullHeight = spriteSize.y * scale;

            // Position the scaled sprite rect so its pivot matches the Image's rect pivot.
            Vector2 pivot = rectTransform.pivot;

            float spriteX = rect.xMin + (rect.width - fullWidth) * pivot.x;
            float spriteY = rect.yMin + (rect.height - fullHeight) * pivot.y;

            // Corners of the Image's own rect (what we actually draw).
            float x0 = rect.xMin;
            float y0 = rect.yMin;
            float x1 = rect.xMax;
            float y1 = rect.yMax;

            // Normalized coordinates within the *scaled sprite rect*.
            float tx0 = (x0 - spriteX) / fullWidth;
            float tx1 = (x1 - spriteX) / fullWidth;
            float ty0 = (y0 - spriteY) / fullHeight;
            float ty1 = (y1 - spriteY) / fullHeight;

            // Map those normalized coords into sprite UVs.
            Vector4 uv = DataUtility.GetOuterUV(spr);

            float u0 = Mathf.Lerp(uv.x, uv.z, tx0);
            float u1 = Mathf.Lerp(uv.x, uv.z, tx1);
            float v0 = Mathf.Lerp(uv.y, uv.w, ty0);
            float v1 = Mathf.Lerp(uv.y, uv.w, ty1);

            Color32 col32 = color;

            helper.Clear();
            // Bottom-left
            helper.AddVert(new Vector3(x0, y0), col32, new Vector2(u0, v0));
            // Top-left
            helper.AddVert(new Vector3(x0, y1), col32, new Vector2(u0, v1));
            // Top-right
            helper.AddVert(new Vector3(x1, y1), col32, new Vector2(u1, v1));
            // Bottom-right
            helper.AddVert(new Vector3(x1, y0), col32, new Vector2(u1, v0));

            helper.AddTriangle(0, 1, 2);
            helper.AddTriangle(2, 3, 0);
        }
    }
}
