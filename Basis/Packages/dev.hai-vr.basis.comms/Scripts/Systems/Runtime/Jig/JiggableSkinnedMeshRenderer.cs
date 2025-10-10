using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Basis.Comms.Jigging
{
    public class Jigs
    {
        private readonly Dictionary<Object, IJiggable> _jiggables = new();

        public JiggableSkinnedMeshRenderer GetOrCreateJiggable(SkinnedMeshRenderer renderer)
        {
            if (_jiggables.TryGetValue(renderer, out var existing)) return existing as JiggableSkinnedMeshRenderer;

            var result = new JiggableSkinnedMeshRenderer(renderer);
            AddJig(renderer, result);

            return result;
        }

        public void AddJig(Object source, IJiggable jiggable)
        {
            _jiggables.Add(source, jiggable);
        }
    }

    public interface IJiggable
    {
    }

    public class JiggableSkinnedMeshRenderer : IJiggable
    {
        private readonly SkinnedMeshRenderer _renderer;
        private readonly List<JigBlendShape> _blendShapes;

        public JiggableSkinnedMeshRenderer(SkinnedMeshRenderer renderer)
        {
            _renderer = renderer;
            if (renderer.sharedMesh is { } mesh)
            {
                _blendShapes = new List<JigBlendShape>(mesh.blendShapeCount);
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    _blendShapes.Add(new JigBlendShape(i, mesh.GetBlendShapeName(i), renderer.GetBlendShapeWeight(i), renderer));
                }
            }
            else
            {
                _blendShapes = new List<JigBlendShape>();
            }
        }
    }

    public class JigBlendShape
    {
        public int Index { get; }
        public string Name { get; }
        public float DefaultValue { get; }

        private readonly SkinnedMeshRenderer _renderer;
        private SeizedFloat _seizedNullable;

        public JigBlendShape(int index, string name, float defaultValue, SkinnedMeshRenderer renderer)
        {
            _renderer = renderer;
            Index = index;
            Name = name;
            DefaultValue = defaultValue;
        }

        public SeizedFloat Seize()
        {
            if (_seizedNullable != null)
            {
                _seizedNullable.Break();
                _seizedNullable = null;
            }

            _seizedNullable = new SeizedFloat(
                value => _renderer.SetBlendShapeWeight(Index, value),
                () => _renderer.SetBlendShapeWeight(Index, DefaultValue)
            );

            return _seizedNullable;
        }
    }

    public class SeizedFloat
    {
        private readonly Action<float> _setValue;
        private readonly Action _reset;
        private bool _broken;

        public SeizedFloat(Action<float> setValue, Action reset)
        {
            _setValue = setValue;
            _reset = reset;
        }

        public void SetValue(float value)
        {
            if (_broken) return;

            _setValue(value);
        }

        public void ResetToDefault()
        {
            if (_broken) return;

            _reset();
        }

        public void Break()
        {
            _broken = true;
        }
    }
}
