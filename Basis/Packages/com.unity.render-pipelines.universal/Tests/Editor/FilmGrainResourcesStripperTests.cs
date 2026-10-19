using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityEditor.Rendering.Universal.Test
{
    class FilmGrainResourcesStripperTests
    {
        [Test]
        public void AllRendererDataLackPostProcessData_AllNull_ReturnsTrue()
        {
            var rendererDataList = new List<ScriptableRendererData>
            {
                ScriptableObject.CreateInstance<UniversalRendererData>(),
                ScriptableObject.CreateInstance<UniversalRendererData>()
            };

            foreach (var rd in rendererDataList)
                ((UniversalRendererData)rd).postProcessData = null;

            Assert.IsTrue(FilmGrainResourcesStripper.PostProcessDisabledInAllRenderers(rendererDataList));

            foreach (var rd in rendererDataList)
                Object.DestroyImmediate(rd);
        }

        [Test]
        public void AllRendererDataLackPostProcessData_OneHasPostProcessData_ReturnsFalse()
        {
            var ppData = ScriptableObject.CreateInstance<PostProcessData>();
            var withPP = ScriptableObject.CreateInstance<UniversalRendererData>();
            var withoutPP = ScriptableObject.CreateInstance<UniversalRendererData>();

            withPP.postProcessData = ppData;
            withoutPP.postProcessData = null;

            var rendererDataList = new List<ScriptableRendererData> { withPP, withoutPP };
            Assert.IsFalse(FilmGrainResourcesStripper.PostProcessDisabledInAllRenderers(rendererDataList));

            Object.DestroyImmediate(ppData);
            Object.DestroyImmediate(withPP);
            Object.DestroyImmediate(withoutPP);
        }

        [Test]
        public void AllRendererDataLackPostProcessData_EmptyList_ReturnsTrue()
        {
            Assert.IsTrue(FilmGrainResourcesStripper.PostProcessDisabledInAllRenderers(new List<ScriptableRendererData>()));
        }

        [Test]
        public void AllRendererDataLackPostProcessData_Renderer2DWithNull_ReturnsTrue()
        {
            var renderer2D = ScriptableObject.CreateInstance<Renderer2DData>();
            renderer2D.postProcessData = null;

            var rendererDataList = new List<ScriptableRendererData> { renderer2D };
            Assert.IsTrue(FilmGrainResourcesStripper.PostProcessDisabledInAllRenderers(rendererDataList));

            Object.DestroyImmediate(renderer2D);
        }
    }
}
