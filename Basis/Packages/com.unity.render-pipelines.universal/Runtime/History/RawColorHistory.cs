using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Raw render color history. The color snapshot is taken before post processing. Raw rendered geometry. No UI overlay.
    /// A matching depth is RawDepthHistory.
    /// Color space is linear RGB.
    /// No mips.
    /// MSAA is not supported and is resolved for the history.
    /// XR is supported.
    /// </summary>
    public sealed class RawColorHistory : ColorHistory
    {
        /// <inheritdoc />
        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            m_Names[0] = "RawColorHistory0";
            m_Names[1] = "RawColorHistory1";
            base.OnCreate(owner, typeId);
        }
    }
}
