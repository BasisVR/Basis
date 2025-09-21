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
using Meta.XR.ImmersiveDebugger.Hierarchy;

namespace Meta.XR.ImmersiveDebugger.Manager
{
    internal struct Category : IEquatable<Category>
    {
        private const string DefaultCategoryName = "Uncategorized";

        public static Category Default = new();

        /// <summary>
        /// A unique identifier used to identify this category
        /// </summary>
        public string Id;

        /// <summary>
        /// A Category could be attached to a Hierarchy.Item
        /// </summary>
        public Item Item;

        /// <summary>
        /// The label that will get displayed
        /// </summary>
        public string Label => Item?.Label ?? (string.IsNullOrEmpty(Id) ? DefaultCategoryName : Id);

        private string Uid => (Item?.Id.ToString() ?? Id) ?? string.Empty;

        public bool Equals(Category other) => Uid == other.Uid;
        public override bool Equals(object obj) => obj is Category other && Equals(other);
        public override int GetHashCode() => Uid.GetHashCode();
    }
}

