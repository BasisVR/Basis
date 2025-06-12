#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AudioLink.Editor
{
    [InitializeOnLoad]
    public class AudioLinkDefineManager
    {
        static AudioLinkDefineManager()
        {
            Shader.EnableKeyword("AUDIOLINK_IMPORTED");
        }
    }

}
#endif
