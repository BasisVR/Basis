using System;
using UnityEngine.Scripting;

namespace Basis.Scripts.BasisSdk.Cilbox
{
	/// <summary>Which Cilbox boxes an entry applies to. World is the scene box.</summary>
	[Flags]
	public enum BasisCilboxBoxes
	{
		None = 0,
		World = 1,
		Prop = 2,
		Avatar = 4,
		All = World | Prop | Avatar,
	}

	/// <summary>
	/// Whitelists a type for scripts in the given boxes, so a package can expose its own shim without
	/// editing the box whitelists. Every public method of the type becomes callable (methods are
	/// default-allow once the type is); hold one back with <see cref="BasisCilboxBlockAttribute"/>.
	/// Fields stay default-deny: put this attribute on a field to allow it, or set AllFields.
	/// Nested types need their own attribute unless Nested is set. Hard-denied types (IntPtr, Marshal,
	/// network and settings internals) stay denied whatever this says. Derives from Preserve so
	/// IL2CPP keeps the type, but members only reached from scripts still need a link.xml entry.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface | AttributeTargets.Delegate | AttributeTargets.Field, Inherited = false)]
	public sealed class BasisCilboxAllowAttribute : PreserveAttribute
	{
		public BasisCilboxAllowAttribute(BasisCilboxBoxes boxes) { Boxes = boxes; }
		public BasisCilboxBoxes Boxes { get; }
		public bool AllFields { get; set; }
		public bool Nested { get; set; }
	}

	/// <summary>
	/// Holds back a method, property or constructor of an allowed type in the given boxes. The box
	/// whitelists match by name, so this blocks every overload with that name; on a property it
	/// blocks both accessors.
	/// </summary>
	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor, Inherited = false)]
	public sealed class BasisCilboxBlockAttribute : Attribute
	{
		public BasisCilboxBlockAttribute(BasisCilboxBoxes boxes) { Boxes = boxes; }
		public BasisCilboxBoxes Boxes { get; }
	}
}
