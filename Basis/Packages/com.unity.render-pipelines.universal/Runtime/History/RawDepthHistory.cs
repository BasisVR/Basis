using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Raw render depth history. The depth snapshot is taken before post processing.
    /// A matching color is RawColorHistory.
    /// Format is the camera depth format or R32Float on platforms with limitations.
    /// If TemporalAA is enabled the depth is jittered.
    /// No mips. No depth pyramid.
    /// MSAA is not supported and is resolved for the history.
    /// XR is supported.
    /// </summary>
    public sealed class RawDepthHistory : DepthHistory
    {
        /// <inheritdoc />
        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            m_Names[0] = "RawDepthHistory0";
            m_Names[1] = "RawDepthHistory1";
            base.OnCreate(owner, typeId);
        }
    }
}
