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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Meta.XR.Editor.Tags
{
    [Serializable]
    public class TagArray : IEnumerable<Tag>
    {
        [SerializeField] private Tag[] array = Array.Empty<Tag>();

        public IEnumerator<Tag> GetEnumerator() => array.AsEnumerable().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => array.GetEnumerator();

        internal void SetArray(IEnumerable<Tag> otherArray)
        {
            array = otherArray.ToArray();
        }

        public bool HasAnyTag(IEnumerable<Tag> tagArray) => this.Intersect(tagArray).Any();

        private void Add(Tag tag)
        {
            if (array.Contains(tag))
            {
                return;
            }

            var index = array.Length;
            Array.Resize(ref array, index + 1);
            array[index] = tag;

            _sortedTagsDirty = true;
        }

        public void Add(params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                Add(tag);
            }
        }

        public void Add(IEnumerable<Tag> tags)
        {
            foreach (var tag in tags)
            {
                Add(tag);
            }
        }

        public void Update(Tag tag)
        {
            if (array.Contains(tag)) Remove(tag);
            Add(tag);
        }

        public void Remove(Tag tag)
        {
            var index = Array.IndexOf(array, tag);
            if (index == -1) return;

            for (var i = index; i < array.Length - 1; i++)
            {
                array[i] = array[i + 1];
            }
            Array.Resize(ref array, array.Length - 1);

            _sortedTagsDirty = true;
        }

        public void Clear()
        {
            array = Array.Empty<Tag>();
            _sortedTagsDirty = true;
        }

        private List<Tag> _sortedTags;
        private bool _sortedTagsDirty = true;
        internal List<Tag> SortedTags
        {
            get
            {
                RefreshSortedTags();
                return _sortedTags;
            }
        }

        private void RefreshSortedTags()
        {
            _sortedTags ??= new List<Tag>();

            if (_sortedTagsDirty)

            {
                _sortedTags.Clear();
                _sortedTags.AddRange(array.AsEnumerable());
                _sortedTags.Sort(Tag.Sorter);
                _sortedTagsDirty = false;
            }
        }

        public void OnValidate()
        {
            for (var i = 0; i < array.Length; i++)
            {
                array[i].OnValidate();
            }

            _sortedTagsDirty = true;

            RefreshSortedTags();
        }
    }
}
