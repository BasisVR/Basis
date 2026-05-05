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

    // Tracks previous int values per parameter hash for preventRepeats.
    private readonly Dictionary<int, int> _lastIntValues = new Dictionary<int, int>();

    // Pre-computed parameter name hashes and types (populated once on first enter).
    private int[] _destHashes;
    private int[] _srcHashes;
    private AnimatorControllerParameterType[] _destTypes;
    private AnimatorControllerParameterType[] _srcTypes;

    // ------------------------------------------------------------------
    // StateMachineBehaviour callbacks
    // ------------------------------------------------------------------

    public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        if (_destHashes == null)
            Bake(animator);

        for (int i = 0; i < operations.Length; i++)
        {
            Execute(animator, operations[i], _destHashes[i], _srcHashes[i], _destTypes[i], _srcTypes[i]);
        }
    }

    private void Bake(Animator animator)
    {
        _destHashes = new int[operations.Length];
        _srcHashes  = new int[operations.Length];
        _destTypes  = new AnimatorControllerParameterType[operations.Length];
        _srcTypes   = new AnimatorControllerParameterType[operations.Length];

        // Snapshot the animator's parameters once — `animator.parameters` allocates a fresh
        // array on every access, so avoid calling it in the hot path.
        AnimatorControllerParameter[] parameters = animator.parameters;
        var map = new Dictionary<int, AnimatorControllerParameterType>(parameters.Length);
        for (int i = 0; i < parameters.Length; i++)
            map[parameters[i].nameHash] = parameters[i].type;

        for (int i = 0; i < operations.Length; i++)
        {
            Operation op = operations[i];

            int destHash = string.IsNullOrEmpty(op.destination) ? 0 : Animator.StringToHash(op.destination);
            int srcHash  = string.IsNullOrEmpty(op.source)      ? 0 : Animator.StringToHash(op.source);

            _destHashes[i] = destHash;
            _srcHashes[i]  = srcHash;
            _destTypes[i]  = (destHash != 0 && map.TryGetValue(destHash, out var dt)) ? dt : (AnimatorControllerParameterType)0;
            _srcTypes[i]   = (srcHash  != 0 && map.TryGetValue(srcHash,  out var st)) ? st : (AnimatorControllerParameterType)0;
        }
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    private void Execute(Animator animator, Operation op, int destHash, int srcHash, AnimatorControllerParameterType destType, AnimatorControllerParameterType srcType)
    {
        // 0 == unset; StringToHash never returns 0 for non-empty strings
        if (destHash == 0)
            return;

        if (destType == 0)
        {
            BasisDebug.LogWarning($"[BasisParameterDriver] Destination parameter '{op.destination}' not found on animator.");
            return;
        }

        switch (op.type)
        {
            case Operation.OperationType.Set:
                WriteParam(animator, destHash, destType, op.value);
                break;

            case Operation.OperationType.Add:
            {
                float current = ReadParamAsFloat(animator, destHash, destType);
                WriteParam(animator, destHash, destType, current + op.value);
                break;
            }

            case Operation.OperationType.Random:
                ExecuteRandom(animator, op, destHash, destType);
                break;

            case Operation.OperationType.Copy:
                ExecuteCopy(animator, op, destHash, srcHash, destType, srcType);
                break;
        }
    }

    private void ExecuteRandom(Animator animator, Operation op, int destHash, AnimatorControllerParameterType destType)
    {
        switch (destType)
        {
            case AnimatorControllerParameterType.Bool:
                animator.SetBool(destHash, UnityEngine.Random.value < op.chance);
                break;

            case AnimatorControllerParameterType.Int:
            {
                int min = Mathf.RoundToInt(op.minValue);
                int max = Mathf.RoundToInt(op.maxValue);
                int range = max - min + 1;
                int result = UnityEngine.Random.Range(min, max + 1);

                if (op.preventRepeats && range > 1 &&
                    _lastIntValues.TryGetValue(destHash, out int last) && last == result)
                {
                    result = min + ((result - min + 1) % range);
                }

                _lastIntValues[destHash] = result;
                animator.SetInteger(destHash, result);
                break;
            }

            default: // Float
                animator.SetFloat(destHash, UnityEngine.Random.Range(op.minValue, op.maxValue));
                break;
        }
    }

    private void ExecuteCopy(Animator animator, Operation op, int destHash, int srcHash, AnimatorControllerParameterType destType, AnimatorControllerParameterType srcType)
    {
        if (srcHash == 0)
            return;

        if (srcType == 0)
        {
            Debug.LogWarning($"[BasisParameterDriver] Source parameter '{op.source}' not found on animator.", animator);
            return;
        }

        float srcVal = ReadParamAsFloat(animator, srcHash, srcType);

        if (op.remapRange)
            srcVal = Remap(srcVal, op.sourceMin, op.sourceMax, op.destMin, op.destMax);

        WriteParam(animator, destHash, destType, srcVal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static float ReadParamAsFloat(Animator animator, int hash, AnimatorControllerParameterType type)
    {
        switch (type)
        {
            case AnimatorControllerParameterType.Float: return animator.GetFloat(hash);
            case AnimatorControllerParameterType.Int:   return animator.GetInteger(hash);
            case AnimatorControllerParameterType.Bool:  return animator.GetBool(hash) ? 1f : 0f;
            default: return 0f;
        }
    }

    private static void WriteParam(Animator animator, int hash, AnimatorControllerParameterType type, float value)
    {
        switch (type)
        {
            case AnimatorControllerParameterType.Float:
                animator.SetFloat(hash, value);
                break;
            case AnimatorControllerParameterType.Int:
                animator.SetInteger(hash, Mathf.RoundToInt(value));
                break;
            case AnimatorControllerParameterType.Bool:
                animator.SetBool(hash, value >= 0.5f);
                break;
        }
    }

    private static float Remap(float value, float inMin, float inMax, float outMin, float outMax)
    {
        if (Mathf.Approximately(inMin, inMax)) return outMin;
        return Mathf.Lerp(outMin, outMax, Mathf.InverseLerp(inMin, inMax, value));
    }
}
