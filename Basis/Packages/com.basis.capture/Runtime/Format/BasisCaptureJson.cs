using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
namespace Basis.Capture
{
    public sealed class BasisCaptureJson
    {
        private readonly StringBuilder sb = new StringBuilder(1 << 16);
        private readonly Stack<bool> first = new Stack<bool>();
        private bool pendingName;
        public override string ToString() => sb.ToString();
        public BasisCaptureJson BeginObject() { Value(); sb.Append('{'); first.Push(true); return this; }
        public BasisCaptureJson EndObject() { first.Pop(); sb.Append('}'); return this; }
        public BasisCaptureJson BeginArray() { Value(); sb.Append('['); first.Push(true); return this; }
        public BasisCaptureJson EndArray() { first.Pop(); sb.Append(']'); return this; }
        public BasisCaptureJson Name(string name) { Separator(); Escape(name); sb.Append(':'); pendingName = true; return this; }
        public BasisCaptureJson BeginObject(string name) => Name(name).BeginObject();
        public BasisCaptureJson BeginArray(string name) => Name(name).BeginArray();
        public BasisCaptureJson Str(string value) { Value(); if (value == null) sb.Append("null"); else Escape(value); return this; }
        public BasisCaptureJson Num(double value) { Value(); AppendNumber(value); return this; }
        public BasisCaptureJson Num(float value) { Value(); if (float.IsNaN(value) || float.IsInfinity(value)) sb.Append('0'); else sb.Append(value.ToString("R", CultureInfo.InvariantCulture)); return this; }
        public BasisCaptureJson Num(long value) { Value(); sb.Append(value.ToString(CultureInfo.InvariantCulture)); return this; }
        public BasisCaptureJson Bool(bool value) { Value(); sb.Append(value ? "true" : "false"); return this; }
        public BasisCaptureJson Null() { Value(); sb.Append("null"); return this; }
        public BasisCaptureJson Prop(string name, string value) => Name(name).Str(value);
        public BasisCaptureJson Prop(string name, double value) => Name(name).Num(value);
        public BasisCaptureJson Prop(string name, float value) => Name(name).Num(value);
        public BasisCaptureJson Prop(string name, long value) => Name(name).Num(value);
        public BasisCaptureJson Prop(string name, int value) => Name(name).Num((long)value);
        public BasisCaptureJson Prop(string name, bool value) => Name(name).Bool(value);
        public BasisCaptureJson Prop(string name, Vector2 v) => Name(name).Floats(v.x, v.y);
        public BasisCaptureJson Prop(string name, Vector3 v) => Name(name).Floats(v.x, v.y, v.z);
        public BasisCaptureJson Prop(string name, Vector4 v) => Name(name).Floats(v.x, v.y, v.z, v.w);
        public BasisCaptureJson Prop(string name, Quaternion q) => Name(name).Floats(q.x, q.y, q.z, q.w);
        public BasisCaptureJson Prop(string name, Color c) => Name(name).Floats(c.r, c.g, c.b, c.a);
        public BasisCaptureJson Prop(string name, Matrix4x4 m)
        {
            Name(name).BeginArray();
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) Num(m[r, c]);
            return EndArray();
        }
        public BasisCaptureJson Floats(params float[] values)
        {
            BeginArray();
            for (int i = 0; i < values.Length; i++) Num(values[i]);
            return EndArray();
        }
        public BasisCaptureJson Ints(IList<int> values)
        {
            BeginArray();
            for (int i = 0; i < values.Count; i++) Num((long)values[i]);
            return EndArray();
        }
        public BasisCaptureJson Strings(IList<string> values)
        {
            BeginArray();
            for (int i = 0; i < values.Count; i++) Str(values[i]);
            return EndArray();
        }
        private void Value()
        {
            if (pendingName) { pendingName = false; return; }
            Separator();
        }
        private void Separator()
        {
            if (first.Count == 0) return;
            if (first.Peek()) { first.Pop(); first.Push(false); } else sb.Append(',');
        }
        private void AppendNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) { sb.Append('0'); return; }
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
        private void Escape(string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
