using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.Scripts.BasisSdk.Constraints;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisConstraintCilboxExposureTests
    {
        static readonly Type[] Components =
        {
            typeof(BasisConstraintBase),
            typeof(BasisPositionConstraint),
            typeof(BasisRotationConstraint),
            typeof(BasisScaleConstraint),
            typeof(BasisParentConstraint),
            typeof(BasisAimConstraint),
            typeof(BasisLookAtConstraint),
            typeof(BasisBlendConstraint),
            typeof(BasisOverrideTransform),
            typeof(BasisDampedTransform),
            typeof(BasisTwistCorrection),
            typeof(BasisTwoBoneIK),
            typeof(BasisChainIK),
            typeof(BasisTwistChain),
            typeof(BasisMultiReferential),
        };

        static readonly Type[] Values =
        {
            typeof(BasisConstraintSourceEntry),
            typeof(BasisConstraintAxes),
            typeof(BasisConstraintWorldUp),
            typeof(BasisConstraintType),
            typeof(BasisOverrideTransform.Space),
            typeof(BasisTwistCorrection.TwistAxis),
        };

        static readonly HashSet<(Type, string)> HeldBackFields = new HashSet<(Type, string)>
        {
            (typeof(BasisConstraintBase), nameof(BasisConstraintBase.authoredOrder)),
            (typeof(BasisTwoBoneIK), nameof(BasisTwoBoneIK.mid)),
            (typeof(BasisTwoBoneIK), nameof(BasisTwoBoneIK.root)),
            (typeof(BasisChainIK), nameof(BasisChainIK.root)),
            (typeof(BasisMultiReferential), nameof(BasisMultiReferential.members)),
        };

        static readonly HashSet<(Type, string)> HeldBackMethods = new HashSet<(Type, string)>
        {
            (typeof(BasisConstraintBase), "add_" + nameof(BasisConstraintBase.ActiveStateChanged)),
            (typeof(BasisConstraintBase), "remove_" + nameof(BasisConstraintBase.ActiveStateChanged)),
            (typeof(BasisConstraintBase), "add_" + nameof(BasisConstraintBase.StructureChanged)),
            (typeof(BasisConstraintBase), "remove_" + nameof(BasisConstraintBase.StructureChanged)),
            (typeof(BasisConstraintBase), nameof(BasisConstraintBase.OnInitialize)),
            (typeof(BasisConstraintBase), nameof(BasisConstraintBase.OnRemove)),
            (typeof(BasisConstraintBase), nameof(BasisConstraintBase.SetDirty)),
            (typeof(BasisConstraintBase), "get_" + nameof(BasisConstraintBase.Sources)),
            (typeof(BasisMultiReferential), "get_" + nameof(BasisMultiReferential.BindPositions)),
            (typeof(BasisMultiReferential), "get_" + nameof(BasisMultiReferential.BindRotations)),
        };

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<CilboxBasisCommon> boxes = new List<CilboxBasisCommon>();

        [SetUp]
        public void SetUp()
        {
            AddBox<CilboxSceneBasis>();
            AddBox<CilboxAvatarBasis>();
            AddBox<CilboxPropBasis>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in hosts)
            {
                if (host != null)
                    UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
            boxes.Clear();
        }

        void AddBox<T>() where T : CilboxBasisCommon
        {
            var host = new GameObject(typeof(T).Name + " constraint exposure test");
            hosts.Add(host);
            T box = host.AddComponent<T>();
            box.classes = new Dictionary<string, int>();
            boxes.Add(box);
        }

        static SerializedTypeDescriptor Descriptor(Type type)
        {
            return SerializedTypeDescriptorBuilder.FromNativeType(type);
        }

        static MethodBase Resolve(CilboxBasisCommon box, MethodInfo method)
        {
            SerializedTypeDescriptor[] parameters = method.GetParameters().Select(p => Descriptor(p.ParameterType)).ToArray();
            return new CilboxUsage(box).GetNativeMethodFromTypeAndName(method.DeclaringType, method.Name, parameters, Array.Empty<SerializedTypeDescriptor>(), method.ToString());
        }

        static bool Allowed(CilboxBasisCommon box, Type declaringType, string name)
        {
            return box.CheckMethodAllowed(out _, declaringType, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name);
        }

        static bool Reachable(CilboxBasisCommon box, FieldInfo field)
        {
            var usage = new CilboxUsage(box);
            string declaringName = usage.GetNativeTypeNameFromDescriptor(Descriptor(field.DeclaringType));
            if (declaringName == null || !box.CheckFieldAllowed(declaringName, field.Name))
                return false;
            FieldInfo resolved = usage.GetNativeTypeFromDescriptor(Descriptor(field.DeclaringType))?.GetField(field.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.Instance);
            return resolved != null && usage.CheckTypeSecurityRecursive(resolved.FieldType);
        }

        [Test]
        public void TheIssueScript_LoadsInEveryBox()
        {
            MethodInfo setEnabled = typeof(Behaviour).GetProperty(nameof(Behaviour.enabled)).GetSetMethod();
            MethodInfo getSource = typeof(BasisConstraintBase).GetMethod(nameof(BasisConstraintBase.GetSource));
            FieldInfo sourceTransform = typeof(BasisConstraintSourceEntry).GetField(nameof(BasisConstraintSourceEntry.sourceTransform));

            foreach (CilboxBasisCommon box in boxes)
            {
                string name = box.GetType().Name;
                foreach (Type component in new[] { typeof(BasisRotationConstraint), typeof(BasisPositionConstraint) })
                {
                    var token = new CilMetadataTokenInfo(MetaTokenType.mtMethod);
                    bool rewritten = new CilboxUsage(box).OptionallyOverride(nameof(Component.GetComponent), Descriptor(typeof(Component)), "T GetComponent[T]()", false, new[] { Descriptor(component) }, ref token);
                    Assert.IsTrue(rewritten && token.opaque as Type == component,
                        $"{name}: GetComponent<{component.Name}>() fails to load, the TYPE FAILED CHECK reported in issue #1081.");
                }
                Assert.IsNotNull(Resolve(box, setEnabled), $"{name}: a script cannot switch a constraint on or off through enabled.");
                Assert.IsNotNull(Resolve(box, getSource), $"{name}: a script cannot read a constraint's sources.");
                Assert.IsTrue(Reachable(box, sourceTransform), $"{name}: a script cannot read which transform drives a source.");
            }
        }

        [Test]
        public void EveryConstraintType_IsVisibleToEveryBox()
        {
            foreach (CilboxBasisCommon box in boxes)
            {
                foreach (Type type in Components.Concat(Values))
                {
                    Assert.IsTrue(box.CheckTypeAllowed(type.FullName),
                        $"{box.GetType().Name} rejects {type.FullName}, so any script naming it fails to load.");
                }
            }
        }

        [Test]
        public void EveryConstraintComponent_HasAMethodPin()
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField("commonMethodWhitelist", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "CilboxBasisCommon no longer has a commonMethodWhitelist field; this test cannot see what it restricts.");
            var pins = (Dictionary<Type, HashSet<string>>)field.GetValue(null);

            foreach (Type type in Components)
            {
                Assert.IsTrue(pins.ContainsKey(type),
                    $"{type.Name} is type-whitelisted with no method-whitelist entry. The gate is default-allow, so its whole "
                    + "surface, including anything added to it later, becomes callable from every box.");
            }
        }

        [Test]
        public void TheAuthoringFields_AreReachable_AndTheStructuralOnesAreNot()
        {
            foreach (CilboxBasisCommon box in boxes)
            {
                foreach (Type type in Components.Append(typeof(BasisConstraintSourceEntry)))
                {
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (HeldBackFields.Contains((type, field.Name)))
                        {
                            Assert.IsFalse(Reachable(box, field),
                                $"{box.GetType().Name}: {type.Name}.{field.Name} became reachable from a script. The solver only acts on "
                                + "it when it rebuilds, and nothing marks the constraint dirty when a script changes it.");
                        }
                        else
                        {
                            Assert.IsTrue(Reachable(box, field),
                                $"{box.GetType().Name}: {type.Name}.{field.Name} is not reachable from a script. Whitelist it in "
                                + $"CilboxBasisCommon, or list it in {nameof(HeldBackFields)} if scripts must not touch it.");
                        }
                    }
                }
            }
        }

        [Test]
        public void TheSourceApi_IsCallable_AndTheSolverPlumbingIsNot()
        {
            foreach (CilboxBasisCommon box in boxes)
            {
                foreach (Type type in Components)
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (method.GetBaseDefinition().DeclaringType != type)
                            continue;
                        if (HeldBackMethods.Contains((type, method.Name)))
                        {
                            Assert.IsFalse(Allowed(box, type, method.Name),
                                $"{box.GetType().Name}: {type.Name}.{method.Name} became callable from a script. It is solver plumbing "
                                + "(registration, a rebuild trigger or a live internal collection), not part of the authoring surface.");
                        }
                        else
                        {
                            Assert.IsNotNull(Resolve(box, method),
                                $"{box.GetType().Name}: {type.Name}.{method.Name} does not load in a script. Pin it in "
                                + $"CilboxBasisCommon, or list it in {nameof(HeldBackMethods)} if scripts must not call it.");
                        }
                    }

                    foreach (MethodInfo method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        Assert.IsFalse(Allowed(box, type, method.Name),
                            $"{box.GetType().Name}: {type.Name}.{method.Name} is not public, yet a bundle that names it passes the method gate.");
                    }
                }
            }
        }

        [Test]
        public void TheConversionHelper_StaysOutOfEveryBox()
        {
            foreach (CilboxBasisCommon box in boxes)
            {
                Assert.IsFalse(box.CheckTypeAllowed(typeof(BasisConstraintConversion).FullName),
                    $"{box.GetType().Name} can reach {nameof(BasisConstraintConversion)}, which adds and destroys components on whatever GameObject it is handed.");
                Assert.IsFalse(box.CheckTypeAllowed(typeof(BasisConstraintConversion.Report).FullName),
                    $"{box.GetType().Name} can reach {nameof(BasisConstraintConversion)}.{nameof(BasisConstraintConversion.Report)}.");
            }
        }
    }
}
