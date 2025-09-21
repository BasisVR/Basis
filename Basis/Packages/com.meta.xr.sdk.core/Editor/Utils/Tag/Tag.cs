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
using Meta.XR.Editor.Id;
using UnityEngine;

namespace Meta.XR.Editor.Tags
{
    [Serializable]
    public struct Tag : IEquatable<Tag>, IIdentified
    {
        internal enum TagListType
        {
            Overlays,
            Filters,
            Description,
        }

        internal static readonly TagArray Registry = new TagArray();

        [SerializeField] private string name;
        private TagBehavior _behavior;

        public Tag(string name)
        {
            this.name = name;
            _behavior = null;
            OnValidate();
        }

        public void OnValidate(bool update = false)
        {
            if (!Valid) return;
            _behavior = TagBehavior.GetBehavior(this);

            if (update)
            {
                Registry.Update(this);
                return;
            }

            Registry.Add(this);
        }

        public string Name => name;
        public string Id => Name;
        internal bool Valid => Name != null;

        internal TagBehavior Behavior => _behavior ??= TagBehavior.GetBehavior(this);


        public bool Equals(Tag other) => Name == other.Name;
        public override bool Equals(object obj) => obj is Tag other && Equals(other);
        public override int GetHashCode() => (Name != null ? Name.GetHashCode() : 0);
        public static implicit operator Tag(string s) => new Tag(s);
        public static implicit operator string(Tag tag) => tag.Name;

        internal static Comparison<Tag> Sorter => (lhs, rhs) =>
        {
            var orderComparison = lhs.Behavior.Order.CompareTo(rhs.Behavior.Order);
            return orderComparison != 0 ? orderComparison : string.Compare(lhs.Name, rhs.Name, StringComparison.Ordinal);
        };

        public void Draw(bool inline = false) => Behavior.DrawSimple(inline);
    }
}
