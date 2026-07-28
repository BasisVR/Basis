using System.Collections.Generic;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Basis.Framework.IK.Tests
{
    /// <summary>
    /// Re-derives BasisLocomotionGraph's tables from the stock controller asset. If someone edits
    /// BasisLocomotion.controller — a state, a transition, a blend position — this fails until the
    /// tables (and therefore the job-driven locomotion pose path) are consciously updated to match.
    /// It also guards the supported subset: the stepper assumes a single layer with no masks,
    /// sub-machines, any-state transitions, behaviours, or mirroring.
    /// </summary>
    public class BasisLocomotionGraphParityTests
    {
        const string ControllerPath = "Packages/com.basis.sdk/Animator/BasisLocomotion.controller";
        const float Tolerance = 1e-4f;

        static AnimatorController Load()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            Assert.IsNotNull(controller, $"Stock controller not found at {ControllerPath}");
            return controller;
        }

        static AnimatorState FindState(AnimatorController controller, string name)
        {
            foreach (ChildAnimatorState child in controller.layers[0].stateMachine.states)
            {
                if (child.state.name == name)
                {
                    return child.state;
                }
            }
            Assert.Fail($"State '{name}' not found");
            return null;
        }

        static string StateName(BasisLocoState state) => state.ToString();

        static void AssertCondition(BasisLocoCondition condition, AnimatorCondition actual)
        {
            (AnimatorConditionMode mode, string parameter) expected = condition switch
            {
                BasisLocoCondition.IsJumpingTrue => (AnimatorConditionMode.If, "IsJumping"),
                BasisLocoCondition.CrouchedTrue => (AnimatorConditionMode.If, "CrouchedState"),
                BasisLocoCondition.CrouchedFalse => (AnimatorConditionMode.IfNot, "CrouchedState"),
                BasisLocoCondition.IsFallingTrue => (AnimatorConditionMode.If, "IsFalling"),
                BasisLocoCondition.IsFallingFalse => (AnimatorConditionMode.IfNot, "IsFalling"),
                _ => (AnimatorConditionMode.If, "IsLanding"),
            };
            Assert.AreEqual(expected.mode, actual.mode);
            Assert.AreEqual(expected.parameter, actual.parameter);
        }

        [Test]
        public void SupportedSubsetHolds()
        {
            AnimatorController controller = Load();
            Assert.AreEqual(1, controller.layers.Length, "stepper assumes a single layer");
            AnimatorControllerLayer layer = controller.layers[0];
            Assert.IsNull(layer.avatarMask, "stepper assumes no avatar mask");
            AnimatorStateMachine machine = layer.stateMachine;
            Assert.AreEqual(0, machine.stateMachines.Length, "stepper assumes no sub-state machines");
            Assert.AreEqual(0, machine.anyStateTransitions.Length, "stepper assumes no any-state transitions");
            Assert.AreEqual(0, machine.behaviours.Length, "stepper assumes no state machine behaviours");
            foreach (ChildAnimatorState child in machine.states)
            {
                Assert.IsFalse(child.state.mirror, $"{child.state.name}: stepper assumes no mirroring");
                Assert.AreEqual(0, child.state.behaviours.Length, $"{child.state.name}: stepper assumes no behaviours");
                Assert.AreEqual(1f, child.state.speed, Tolerance, $"{child.state.name}: stepper assumes state speed 1");
                Assert.AreEqual(0f, child.state.cycleOffset, Tolerance, $"{child.state.name}: stepper assumes zero cycle offset");
                Assert.IsFalse(child.state.speedParameterActive && child.state.speedParameter != "CurrentSpeed",
                    $"{child.state.name}: stepper only models CurrentSpeed as a speed parameter");
                foreach (AnimatorStateTransition transition in child.state.transitions)
                {
                    Assert.IsTrue(transition.hasFixedDuration, $"{child.state.name}: stepper assumes fixed-duration transitions");
                    Assert.AreEqual(TransitionInterruptionSource.None, transition.interruptionSource,
                        $"{child.state.name}: stepper assumes uninterruptible transitions");
                    Assert.AreEqual(0f, transition.offset, Tolerance, $"{child.state.name}: stepper assumes zero transition offset");
                    Assert.AreEqual(1, transition.conditions.Length, $"{child.state.name}: stepper assumes single-condition transitions");
                }
            }
        }

        [Test]
        public void StatesAndTransitionsMatchTables()
        {
            AnimatorController controller = Load();
            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            Assert.AreEqual(5, machine.states.Length);
            Assert.AreEqual("Walking", machine.defaultState.name);
            Assert.AreEqual(BasisLocoState.Walking, BasisLocomotionGraph.DefaultSimState.Current);

            for (int stateIndex = 0; stateIndex < BasisLocomotionGraph.Transitions.Length; stateIndex++)
            {
                var state = (BasisLocoState)stateIndex;
                AnimatorState animatorState = FindState(controller, StateName(state));
                BasisLocoTransition[] expected = BasisLocomotionGraph.Transitions[stateIndex];
                Assert.AreEqual(expected.Length, animatorState.transitions.Length, $"{state}: transition count");
                for (int i = 0; i < expected.Length; i++)
                {
                    AnimatorStateTransition actual = animatorState.transitions[i];
                    Assert.IsNotNull(actual.destinationState, $"{state}[{i}]: destination");
                    Assert.AreEqual(StateName(expected[i].To), actual.destinationState.name, $"{state}[{i}]: destination");
                    Assert.AreEqual(expected[i].DurationSeconds, actual.duration, Tolerance, $"{state}[{i}]: duration");
                    Assert.AreEqual(expected[i].HasExitTime, actual.hasExitTime, $"{state}[{i}]: hasExitTime");
                    if (expected[i].HasExitTime)
                    {
                        Assert.AreEqual(expected[i].ExitTime, actual.exitTime, Tolerance, $"{state}[{i}]: exitTime");
                    }
                    AssertCondition(expected[i].Condition, actual.conditions[0]);
                }
            }

            Assert.IsTrue(FindState(controller, "Walking").speedParameterActive);
            Assert.AreEqual("CurrentSpeed", FindState(controller, "Walking").speedParameter);
            Assert.IsTrue(FindState(controller, "Cawling").speedParameterActive);
            Assert.AreEqual("CurrentSpeed", FindState(controller, "Cawling").speedParameter);
            Assert.IsFalse(FindState(controller, "Jump").speedParameterActive);
            Assert.IsFalse(FindState(controller, "Falling").speedParameterActive);
            Assert.IsFalse(FindState(controller, "Landing").speedParameterActive);
        }

        static void AssertTree(BlendTree tree, int clipStart, int childCount, float2[] positions, float[] timeScales)
        {
            Assert.AreEqual(BlendTreeType.FreeformCartesian2D, tree.blendType);
            Assert.AreEqual("VelocityX", tree.blendParameter);
            Assert.AreEqual("VelocityZ", tree.blendParameterY);
            ChildMotion[] children = tree.children;
            Assert.AreEqual(childCount, children.Length);
            for (int i = 0; i < childCount; i++)
            {
                var clip = children[i].motion as AnimationClip;
                Assert.IsNotNull(clip, $"child {i}: motion is not a clip");
                Assert.AreEqual(BasisLocomotionGraph.ClipNames[clipStart + i], clip.name, $"child {i}: clip");
                Assert.AreEqual(positions[i].x, children[i].position.x, Tolerance, $"child {i}: position.x");
                Assert.AreEqual(positions[i].y, children[i].position.y, Tolerance, $"child {i}: position.y");
                Assert.AreEqual(timeScales[i], children[i].timeScale, Tolerance, $"child {i}: timeScale");
                Assert.AreEqual(0f, children[i].cycleOffset, Tolerance, $"child {i}: cycleOffset");
                Assert.IsFalse(children[i].mirror, $"child {i}: mirror");
            }
        }

        [Test]
        public void BlendTreesMatchTables()
        {
            AnimatorController controller = Load();
            var walking = FindState(controller, "Walking").motion as BlendTree;
            Assert.IsNotNull(walking, "Walking should hold a blend tree");
            AssertTree(walking, BasisLocomotionGraph.WalkingClipStart, BasisLocomotionGraph.WalkingChildCount,
                BasisLocomotionGraph.WalkingChildPositions, BasisLocomotionGraph.WalkingChildTimeScales);

            var cawling = FindState(controller, "Cawling").motion as BlendTree;
            Assert.IsNotNull(cawling, "Cawling should hold a blend tree");
            AssertTree(cawling, BasisLocomotionGraph.CawlingClipStart, BasisLocomotionGraph.CawlingChildCount,
                BasisLocomotionGraph.CawlingChildPositions, BasisLocomotionGraph.CawlingChildTimeScales);
        }

        [Test]
        public void SingleClipStatesMatchTables()
        {
            AnimatorController controller = Load();
            var jump = FindState(controller, "Jump").motion as AnimationClip;
            var falling = FindState(controller, "Falling").motion as AnimationClip;
            var landing = FindState(controller, "Landing").motion as AnimationClip;
            Assert.IsNotNull(jump);
            Assert.IsNotNull(falling);
            Assert.IsNotNull(landing);
            Assert.AreEqual(BasisLocomotionGraph.ClipNames[BasisLocomotionGraph.JumpClip], jump.name);
            Assert.AreEqual(BasisLocomotionGraph.ClipNames[BasisLocomotionGraph.FallingClip], falling.name);
            Assert.AreEqual(BasisLocomotionGraph.ClipNames[BasisLocomotionGraph.LandingClip], landing.name);
        }

        [Test]
        public void EveryTableClipExistsOnTheController()
        {
            AnimatorController controller = Load();
            var names = new HashSet<string>();
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip != null)
                {
                    names.Add(clip.name);
                }
            }
            for (int i = 0; i < BasisLocomotionGraph.ClipCount; i++)
            {
                Assert.IsTrue(names.Contains(BasisLocomotionGraph.ClipNames[i]),
                    $"controller.animationClips is missing '{BasisLocomotionGraph.ClipNames[i]}' — the runtime bake resolves clips by name");
            }
        }
    }
}
