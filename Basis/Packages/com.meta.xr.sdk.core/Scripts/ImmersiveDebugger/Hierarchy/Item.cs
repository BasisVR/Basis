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
using System.Collections.Generic;
using Meta.XR.ImmersiveDebugger.Manager;
using Meta.XR.ImmersiveDebugger.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Meta.XR.ImmersiveDebugger.Hierarchy
{
    internal abstract class Item
    {
        private Item _parent;
        private int _depth;
        protected InstanceHandle _handle;

        public Item Parent => _parent;
        public int Depth => _depth;
        public InstanceHandle Handle => _handle;
        public int Id => Handle.InstanceId;
        public virtual Category Category => new() { Item = this };
        public bool Dirty { get; set; }

        public void Clear()
        {
            ClearContent();
            ClearChildren();
            Unregister();
        }

        public virtual void Unregister()
        {
            Manager.Instance.UnprocessItem(this);

            _parent = null;
        }

        public virtual void Register(Item parent)
        {
            _parent = parent;
            _depth = (_parent?.Depth ?? -1) + 1;

            Manager.Instance.ProcessItem(this);
        }

        public abstract object Owner { get; }
        public abstract string Label { get; }
        public virtual int ComputeNumberOfChildren() => 0;
        public abstract bool Valid { get; }
        public virtual bool ComputeNeedsRefresh() => false;

        public virtual void BuildContent() { }
        public virtual void ClearContent() { }

        public virtual void BuildChildren() { }
        public virtual void ClearChildren() { }
    }

    internal abstract class Item<T> : Item
    {
        protected T _owner;
        public override object Owner => _owner;
        public T TypedOwner => _owner;

        public void SetOwner(T owner)
        {
            _owner = owner;
            _handle = BuildHandle();
        }

        protected abstract InstanceHandle BuildHandle();
    }

    internal abstract class ItemWithChildren<TargetType, ChildType, ChildTargetType> : Item<TargetType>
        where ChildType : Item<ChildTargetType>, new()
    {
        private readonly List<ChildType> _children = new();
        protected abstract bool CompareChildren(ChildTargetType lhs, ChildTargetType rhs);
        protected abstract ChildTargetType[] FetchExpectedChildren();
        public override int ComputeNumberOfChildren() => FetchExpectedChildren().Length;

        private void MarkChildrenDirty()
        {
            foreach (var child in _children)
            {
                child.Dirty = true;
            }
        }

        private void ClearDirtyChildren()
        {
            foreach (var child in _children)
            {
                if (child.Dirty)
                {
                    child.Clear();
                }
            }
        }

        private ChildType GetChild(ChildTargetType target)
        {
            foreach (var child in _children)
            {
                if (CompareChildren(child.TypedOwner, target))
                {
                    return child;
                }
            }

            return null;
        }

        public override void ClearChildren()
        {
            // Clear Children
            foreach (var child in _children)
            {
                child.Clear();
            }

            _children.Clear();

            base.ClearChildren();
        }

        public override void BuildChildren()
        {
            if (!Valid)
            {
                Clear();
                return;
            }

            MarkChildrenDirty();

            BuildChildrenInternal();

            ClearDirtyChildren();
        }

        private void BuildChildrenInternal()
        {
            // Build Children list
            // From the RootGameObjects of the scene
            foreach (var childTarget in FetchExpectedChildren())
            {
                var item = GetChild(childTarget);
                if (item != null)
                {
                    item.Dirty = false;
                    continue;
                }

                item = new ChildType();
                item.Dirty = false;
                item.SetOwner(childTarget);
                _children.Add(item);
                item.Register(this);
            }
        }

        public override bool ComputeNeedsRefresh()
        {
            foreach (var childTarget in FetchExpectedChildren())
            {
                if (GetChild(childTarget) == null) return true;
            }

            return false;
        }
    }

    internal class GameObjectItem : ItemWithChildren<GameObject, GameObjectItem, GameObject>
    {
        private readonly List<ComponentItem> _components = new();

        public override string Label => _owner.name;
        public override bool Valid => _owner != null;
        protected override InstanceHandle BuildHandle() => new(typeof(GameObject), _owner);

        protected override bool CompareChildren(GameObject lhs, GameObject rhs) => lhs == rhs;
        protected override GameObject[] FetchExpectedChildren()
        {
            var transform = _owner.transform;
            var childrenCount = transform.childCount;
            var children = new GameObject[childrenCount];
            for (var i = 0; i < childrenCount; i++)
            {
                children[i] = transform.GetChild(i).gameObject;
            }

            return children;
        }

        // Content Logic

        public override void BuildContent()
        {
            if (!Valid)
            {
                Clear();
                return;
            }

            BuildContentInternal();
        }

        private void BuildContentInternal()
        {
            // Build Components list
            // Content are the components on this gameObject
            foreach (var component in _owner.GetComponents<Component>())
            {
                var item = new ComponentItem();
                item.SetOwner(component);
                _components.Add(item);
                item.Register(this);
            }
        }

        public override void ClearContent()
        {
            // Clear Component
            foreach (var component in _components)
            {
                component.Clear();
            }

            _components.Clear();

            base.ClearContent();
        }
    }

    internal class ComponentItem : Item<Component>
    {
        public override string Label => Handle.Type.Name;
        public override bool Valid => _owner != null;

        // Category of a component is the one of its parent gameObject
        public override Category Category => new() { Item = Parent };
        protected override InstanceHandle BuildHandle() => new(_owner.GetType(), _owner);
    }

    internal class SceneItem : ItemWithChildren<Scene, GameObjectItem, GameObject>
    {
        protected override bool CompareChildren(GameObject lhs, GameObject rhs) => lhs == rhs;
        public override string Label => string.IsNullOrEmpty(_owner.name) ? "Untitled" : _owner.name;
        public override bool Valid => _owner.isLoaded;
        protected override InstanceHandle BuildHandle() => new(_owner);

        protected override GameObject[] FetchExpectedChildren() => _owner.isLoaded ? _owner.GetRootGameObjects() : Array.Empty<GameObject>();
    }

    internal class SceneRegistry : ItemWithChildren<object, SceneItem, Scene>
    {
        protected override InstanceHandle BuildHandle() => new();
        protected override bool CompareChildren(Scene lhs, Scene rhs) => lhs == rhs;
        public override void Unregister() { }
        public override void Register(Item parent) { }
        public override string Label => null;
        public override bool Valid => true;

        protected override Scene[] FetchExpectedChildren()
        {
            var scenes = new Scene[SceneManager.sceneCount];
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                scenes[i] = SceneManager.GetSceneAt(i);
            }

            return scenes;
        }
    }
}
