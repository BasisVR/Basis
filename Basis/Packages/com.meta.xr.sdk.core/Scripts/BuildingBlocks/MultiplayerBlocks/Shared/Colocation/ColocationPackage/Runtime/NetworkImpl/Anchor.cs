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
    ///     A network agnostic class that holds the Anchor data that is shared between all players
    /// </summary>
    [Serializable]
    internal struct Anchor : IEquatable<Anchor>
    {
        public bool isAutomaticAnchor;
        public bool isAlignmentAnchor;
        public ulong ownerOculusId;
        public uint colocationGroupId;
        public Guid automaticAnchorUuid;

        public Anchor(
            bool isAutomaticAnchor,
            bool isAlignmentAnchor,
            ulong ownerOculusId,
            uint colocationGroupId,
            Guid automaticAnchorUuid
        )
        {
            this.isAutomaticAnchor = isAutomaticAnchor;
            this.isAlignmentAnchor = isAlignmentAnchor;
            this.ownerOculusId = ownerOculusId;
            this.colocationGroupId = colocationGroupId;

            this.automaticAnchorUuid = automaticAnchorUuid;
        }

        public bool Equals(Anchor other)
        {
            return isAutomaticAnchor == other.isAutomaticAnchor
                   && isAlignmentAnchor == other.isAlignmentAnchor
                   && ownerOculusId == other.ownerOculusId
                   && colocationGroupId == other.colocationGroupId
                   && automaticAnchorUuid == other.automaticAnchorUuid;
        }
    }
}
