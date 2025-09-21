/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;

namespace Meta.XR.MultiplayerBlocks.Colocation
{
    /// <summary>
    ///     A network agnostic struct that holds the data that needs to be sent for a person to join another persons colocated group
    /// </summary>
    [Serializable]
    internal struct ShareAndLocalizeParams
    {
        public ulong requestingPlayerId;
        public ulong requestingPlayerOculusId;
        public Guid anchorUUID;
        public bool anchorFlowSucceeded;

        public ShareAndLocalizeParams(ulong requestingPlayerId, ulong requestingPlayerOculusId, Guid anchorUUID)
        {
            this.requestingPlayerId = requestingPlayerId;
            this.requestingPlayerOculusId = requestingPlayerOculusId;
            this.anchorUUID = anchorUUID;
            anchorFlowSucceeded = true;
        }

        public ShareAndLocalizeParams(ulong requestingPlayerId, ulong requestingPlayerOculusId, Guid anchorUUID,
            bool anchorFlowSucceeded)
        {
            this.requestingPlayerId = requestingPlayerId;
            this.requestingPlayerOculusId = requestingPlayerOculusId;
            this.anchorUUID = anchorUUID;
            this.anchorFlowSucceeded = anchorFlowSucceeded;
        }

        public override string ToString()
        {
            return
                $"{nameof(requestingPlayerId)}: {requestingPlayerId}, {nameof(requestingPlayerOculusId)}: {requestingPlayerOculusId}, {nameof(anchorUUID)}: {anchorUUID}, {nameof(anchorFlowSucceeded)}: {anchorFlowSucceeded}";
        }
    }
}
