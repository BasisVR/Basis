using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Executes a list of parameter operations when the state is entered.
/// </summary>
public class BasisParameterDriver : StateMachineBehaviour
{
    [Tooltip("When true, operations only run on the local instance (recommended for Add and Random).")]
    public bool localOnly = true;

    [Tooltip("Optional label shown in the editor for identification purposes.")]
    public string debugString;

    public Operation[] operations = Array.Empty<Operation>();

    // ------------------------------------------------------------------
    // Data model
    // ------------------------------------------------------------------

    [Serializable]
    public class Operation
    {
        public enum OperationType { Set, Add, Random, Copy }

        public OperationType type = OperationType.Set;

        [Tooltip("Animator parameter to write to.")]
        public string destination;

        // ---- Set / Add ----
        public float value;

        // ---- Random (float / int) ----
        public float minValue;
        public float maxValue = 1f;

        // ---- Random (bool only) ----
        [Range(0f, 1f), Tooltip("Probability that the bool is set to true.")]
        public float chance = 0.5f;

        // ---- Random (int only) ----
        [Tooltip("Prevents the same integer value from being chosen twice in a row.")]
        public bool preventRepeats;

        // ---- Copy ----
        [Tooltip("Animator parameter to read from.")]
        public string source;

        [Tooltip("Remap the source range to a different destination range.")]
        public bool remapRange;
        public float sourceMin = 0f;
        public float sourceMax = 1f;
        public float destMin   = 0f;
        public float destMax   = 1f;
    }

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------

    // Tracks previous int values per parameter for preventRepeats.
    private readonly Dictionary<string, int> _lastIntValues = new Dictionary<string, int>();

    // ------------------------------------------------------------------
    // StateMachineBehaviour callbacks
    // ------------------------------------------------------------------

    public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        foreach (Operation op in operations)
            Execute(animator, op);
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    private void Execute(Animator animator, Operation op)
    {
        if (string.IsNullOrEmpty(op.destination))
            return;

        AnimatorControllerParameterType destType = GetParamType(animator, op.destination);
        if (destType == 0)
        {
            Debug.LogWarning($"[RNGParamDriver] Destination parameter '{op.destination}' not found on animator.", animator);
            return;
        }

        switch (op.type)
        {
            case Operation.OperationType.Set:
                WriteParam(animator, op.destination, destType, op.value);
                break;

            case Operation.OperationType.Add:
            {
                float current = ReadParamAsFloat(animator, op.destination, destType);
                WriteParam(animator, op.destination, destType, current + op.value);
                break;
            }

            case Operation.OperationType.Random:
                ExecuteRandom(animator, op, destType);
                break;

            case Operation.OperationType.Copy:
                ExecuteCopy(animator, op, destType);
                break;
        }
    }

    private void ExecuteRandom(Animator animator, Operation op, AnimatorControllerParameterType destType)
    {
        switch (destType)
        {
            case AnimatorControllerParameterType.Bool:
                animator.SetBool(op.destination, UnityEngine.Random.value < op.chance);
                break;

            case AnimatorControllerParameterType.Int:
            {
                int min = Mathf.RoundToInt(op.minValue);
                int max = Mathf.RoundToInt(op.maxValue);
                int range = max - min + 1;
                int result = UnityEngine.Random.Range(min, max + 1);

                if (op.preventRepeats && range > 1 &&
                    _lastIntValues.TryGetValue(op.destination, out int last) && last == result)
                {
                    result = min + ((result - min + 1) % range);
                }

                _lastIntValues[op.destination] = result;
                animator.SetInteger(op.destination, result);
                break;
            }

            default: // Float
                animator.SetFloat(op.destination, UnityEngine.Random.Range(op.minValue, op.maxValue));
                break;
        }
    }

    private void ExecuteCopy(Animator animator, Operation op, AnimatorControllerParameterType destType)
    {
        if (string.IsNullOrEmpty(op.source))
            return;

        AnimatorControllerParameterType srcType = GetParamType(animator, op.source);
        if (srcType == 0)
        {
            Debug.LogWarning($"[RNGParamDriver] Source parameter '{op.source}' not found on animator.", animator);
            return;
        }

        float srcVal = ReadParamAsFloat(animator, op.source, srcType);

        if (op.remapRange)
            srcVal = Remap(srcVal, op.sourceMin, op.sourceMax, op.destMin, op.destMax);

        WriteParam(animator, op.destination, destType, srcVal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static float ReadParamAsFloat(Animator animator, string name, AnimatorControllerParameterType type)
    {
        switch (type)
        {
            case AnimatorControllerParameterType.Float: return animator.GetFloat(name);
            case AnimatorControllerParameterType.Int:   return animator.GetInteger(name);
            case AnimatorControllerParameterType.Bool:  return animator.GetBool(name) ? 1f : 0f;
            default: return 0f;
        }
    }

    private static void WriteParam(Animator animator, string name, AnimatorControllerParameterType type, float value)
    {
        switch (type)
        {
            case AnimatorControllerParameterType.Float:
                animator.SetFloat(name, value);
                break;
            case AnimatorControllerParameterType.Int:
                animator.SetInteger(name, Mathf.RoundToInt(value));
                break;
            case AnimatorControllerParameterType.Bool:
                animator.SetBool(name, value >= 0.5f);
                break;
        }
    }

    private static float Remap(float value, float inMin, float inMax, float outMin, float outMax)
    {
        if (Mathf.Approximately(inMin, inMax)) return outMin;
        return Mathf.Lerp(outMin, outMax, Mathf.InverseLerp(inMin, inMax, value));
    }

    private static AnimatorControllerParameterType GetParamType(Animator animator, string name)
    {
        foreach (AnimatorControllerParameter p in animator.parameters)
            if (p.name == name) return p.type;
        return (AnimatorControllerParameterType)0;
    }
}
