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
using System.Linq;
using Object = UnityEngine.Object;

namespace Meta.XR.BuildingBlocks.Editor
{
    internal class InstallationStepInfo
    {
        internal string Message => string.Format(_message, ProcessArg(LinkedProjectAsset));

        private readonly string _message;
        internal Object LinkedProjectAsset { get; }

        public InstallationStepInfo(Object linkedProjectAsset, string message)
        {
            _message = message;
            LinkedProjectAsset = linkedProjectAsset;
        }

        private string ProcessArg(object arg)
        {
            return arg switch
            {
                null => "",
                string stringArg => $"<b>{stringArg}</b>",
                Object gameObject => $"<b><color=#81b3ff>{gameObject.name}</color></b>",
                _ => arg.ToString()
            };
        }
    }
}
