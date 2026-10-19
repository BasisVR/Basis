/*

MIT License

Copyright (c) 2026 Raliv and Gator Dragon Games

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using Basis.IK;
using Unity.Collections;
using UnityEngine;
public class RalivIKSpine
{

    // Spine data struct requiring all transforms to represent bones of nonzero length from the previous
    public struct SpineData
    {
        public Vector3 hipTargetPosition;
        public Quaternion hipTargetRotation;
        public Vector3 chestTargetPosition;
        public Quaternion chestTargetRotation;
        public Vector3 headTargetPosition;
        public Quaternion headTargetRotation;
        public FixedList512Bytes<Vector3> positions;
        public FixedList512Bytes<Quaternion> rotations;
        public FixedList512Bytes<Vector3> restPositions;
        public FixedList512Bytes<Quaternion> restRotations;
        public float length;
        public FixedList512Bytes<float> t;
        public Vector3 chestForward;
        public float chestHintWeight;
        public int chestIndex;
        public bool clampHipDistance;
        public float headFollow; // How much the bones between hips and head take on the head's rotated rest shape
        public FixedList512Bytes<Vector3> targetSpinePositions;
        public FixedList512Bytes<Vector3> hipTargetAlignedSpinePositions;
        public FixedList512Bytes<Vector3> headTargetAlignedSpinePositions;
    }
    public const int iterations = 10;
    public static void SolveSpine(ref SpineData spineData)
    {

        // Skipped when the virtual spine has pinned the hips below the head; they're already at rest distance.
        if (spineData.clampHipDistance)
        {
            float hipHeadRestDistance = Vector3.Magnitude(spineData.restPositions[^1] - spineData.restPositions[0]);
            Vector3 headToHip = spineData.hipTargetPosition - spineData.headTargetPosition;
            headToHip = headToHip.normalized * hipHeadRestDistance;
            spineData.hipTargetPosition = Vector3.Lerp(spineData.hipTargetPosition, spineData.headTargetPosition + headToHip, 0.5f);
        }

        Quaternion hipRotationOffset = spineData.hipTargetRotation * Quaternion.Inverse(spineData.restRotations[0]);
        Quaternion headRotationOffset = spineData.headTargetRotation * Quaternion.Inverse(spineData.restRotations[^1]);

        for (int index = 0; index < spineData.headTargetAlignedSpinePositions.Length; index++)
        {
            if (index == spineData.headTargetAlignedSpinePositions.Length - 1)
            {
                spineData.headTargetAlignedSpinePositions[index] = spineData.headTargetPosition;
                continue;
            }
            var headSpacePosition = spineData.restPositions[index] - spineData.restPositions[^1];
            headSpacePosition = Quaternion.Inverse(spineData.restRotations[^1]) * headSpacePosition;
            spineData.headTargetAlignedSpinePositions[index] = spineData.headTargetRotation * headSpacePosition + spineData.headTargetPosition;
        }

        for (int index = 0; index < spineData.hipTargetAlignedSpinePositions.Length; index++)
        {
            if (index == 0)
            {
                spineData.hipTargetAlignedSpinePositions[index] = spineData.hipTargetPosition;
                continue;
            }
            var hipSpacePosition = spineData.restPositions[index] - spineData.restPositions[0];
            hipSpacePosition = Quaternion.Inverse(spineData.restRotations[0]) * hipSpacePosition;
            spineData.hipTargetAlignedSpinePositions[index] = spineData.hipTargetRotation * hipSpacePosition + spineData.hipTargetPosition;
            //  Debug.DrawLine(hipTargetAlignedSpinePositions[index - 1], hipTargetAlignedSpinePositions[index], Color.white);
            // Debug.DrawLine(headTargetAlignedSpinePositions[index - 1], headTargetAlignedSpinePositions[index], Color.cyan);
        }


        for (int index = 0; index < spineData.targetSpinePositions.Length; index++)
        {
            float follow = index > 0 && index < spineData.targetSpinePositions.Length - 1 ? spineData.headFollow : 1f;
            spineData.targetSpinePositions[index] = Vector3.Lerp(spineData.hipTargetAlignedSpinePositions[index], spineData.headTargetAlignedSpinePositions[index], spineData.t[index] * follow);
        }

        // SOFT FABRIK
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var lengthConstraint = (float)iteration / iterations;
            var curveConstraint = 1f - lengthConstraint;
            for (int index = 0; index < spineData.positions.Length; index++)
            {
                spineData.positions[index] = Vector3.Lerp(spineData.positions[index], spineData.targetSpinePositions[index], curveConstraint);
            }
            var spineOffset = spineData.chestTargetPosition - spineData.positions[2];
            spineOffset *= spineData.chestHintWeight * curveConstraint;
            spineData.positions[2] += spineOffset;
            spineData.positions[3] += spineOffset * 0.5f;
            for (int index = spineData.positions.Length - 2; index >= 0; index--)
            {
                var targetIndex = index + 1;
                var targetOffset = spineData.positions[index] - spineData.positions[targetIndex];
                var boneLength = Vector3.Magnitude(spineData.restPositions[targetIndex] - spineData.restPositions[index]);
                targetOffset = targetOffset.normalized * boneLength;
                var targetPosition = spineData.positions[targetIndex] + targetOffset;
                spineData.positions[index] = Vector3.Lerp(spineData.positions[index], targetPosition, lengthConstraint);
            }
            for (int index = 1; index < spineData.positions.Length; index++)
            {
                var targetIndex = index - 1;
                var targetOffset = spineData.positions[index] - spineData.positions[targetIndex];
                var boneLength = Vector3.Magnitude(spineData.restPositions[targetIndex] - spineData.restPositions[index]);
                targetOffset = targetOffset.normalized * boneLength;
                var targetPosition = spineData.positions[targetIndex] + targetOffset;
                spineData.positions[index] = Vector3.Lerp(spineData.positions[index], targetPosition, lengthConstraint);
            }
            // Project head onto target hip to head line to reduce lateral motion artifacts
            //var hipToHead = spineData.positions[^1] - spineData.positions[0];
            //var targetHipToHead = spineData.headTargetPosition - spineData.hipTargetPosition;
            //var projectedHipToHead = Vector3.Project(hipToHead, targetHipToHead);
            //spineData.positions[^1] = spineData.positions[0] + projectedHipToHead;
        }

        // for (int index = 0; index < spineData.positions.Length - 1; index++) {
        //  Debug.DrawLine(spineData.positions[index], spineData.positions[index + 1], Color.green);
        // }

        for (var index = 0; index < spineData.positions.Length - 1; index++)
        {
            var preRotation = Quaternion.Slerp(hipRotationOffset, headRotationOffset, spineData.t[index]);
            var restParentOffset = spineData.restPositions[index + 1] - spineData.restPositions[index];
            restParentOffset = preRotation * restParentOffset;
            var parentOffset = spineData.positions[index + 1] - spineData.positions[index];
            var fromTo = SafeFromToRotation(restParentOffset, parentOffset);
            spineData.rotations[index] = fromTo * preRotation * spineData.restRotations[index];
        }
        spineData.rotations[^1] = spineData.headTargetRotation;

        var chestRotate = 0f;
        if (spineData.chestHintWeight > 0f)
        {
            var worldSpaceChestTargetForwardVector = spineData.chestTargetRotation * spineData.chestForward;
            var worldSpaceChestForwardVector = spineData.rotations[2] * spineData.chestForward;
            var worldSpaceChestDownBoneVector = spineData.positions[3] - spineData.positions[2];
            worldSpaceChestTargetForwardVector = Vector3.ProjectOnPlane(worldSpaceChestTargetForwardVector, worldSpaceChestDownBoneVector.normalized);
            worldSpaceChestForwardVector = Vector3.ProjectOnPlane(worldSpaceChestForwardVector, worldSpaceChestDownBoneVector.normalized);
            // Debug.DrawLine(spineData.chestTargetPosition,
            // spineData.chestTargetPosition + worldSpaceChestTargetForwardVector, Color.red);
            //   Debug.DrawLine(spineData.positions[2], spineData.positions[2] + worldSpaceChestForwardVector, Color.blue);
            var chestFromTo = SafeFromToRotation(worldSpaceChestForwardVector.normalized, worldSpaceChestTargetForwardVector.normalized);
            chestFromTo.ToAngleAxis(out var chestFromToAngle, out var chestFromToAxis);
            if (Vector3.Dot(chestFromToAxis.normalized, worldSpaceChestDownBoneVector.normalized) < 0f)
            {
                chestFromToAngle = -chestFromToAngle;
            }

            if (chestFromToAngle > 180f)
            {
                chestFromToAngle -= 360f;
            }

            if (chestFromToAngle < -180f)
            {
                chestFromToAngle += 360f;
            }

            chestRotate = chestFromToAngle;
        }

        if (spineData.chestHintWeight > 0f)
        {
            for (var index = 0; index < spineData.positions.Length - 1; index++)
            {
                if (index < spineData.positions.Length - 1)
                {
                    var w = 0f;
                    if (index == 1)
                    {
                        w = spineData.chestHintWeight * 0.5f;
                    }
                    if (index == 2)
                    {
                        w = spineData.chestHintWeight * 1f;
                    }
                    if (index == 3)
                    {
                        w = spineData.chestHintWeight * 0.5f;
                    }
                    var downBone = spineData.positions[index + 1] - spineData.positions[index];
                    spineData.rotations[index] = Quaternion.AngleAxis(chestRotate * w, downBone.normalized) * spineData.rotations[index];
                }
            }
        }

        var headPinCorrection = spineData.headTargetPosition - spineData.positions[^1];
        for (var index = 0; index < spineData.positions.Length; index++)
        {
            spineData.positions[index] += headPinCorrection;
        }

        static Quaternion SafeFromToRotation(Vector3 from, Vector3 to)
        {
            return from.sqrMagnitude > BasisEerieMovement.sqrEpsilon && to.sqrMagnitude > BasisEerieMovement.sqrEpsilon ? BasisQuaternionExt.FromToRotation(from, to) : Quaternion.identity;
        }
    }
}
