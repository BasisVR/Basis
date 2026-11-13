using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk.Cilbox;
using UnityEngine;

namespace Cilbox
{
	/// <summary>
	/// Whitelist entries packages declare with [BasisCilboxAllow] / [BasisCilboxBlock], read once from
	/// every loaded assembly that references BasisSDK. Content cannot add to this: scripts are
	/// interpreted, so only assemblies built into the client carry attributes.
	/// </summary>
	internal static class CilboxPackageAccess
	{
		private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		private static readonly object gate = new object();
		private static Dictionary<string, BasisCilboxBoxes> types;
		private static Dictionary<string, BasisCilboxBoxes> allFields;
		private static Dictionary<string, BasisCilboxBoxes> fields;
		private static Dictionary<Type, Dictionary<string, BasisCilboxBoxes>> blocks;

		internal static BasisCilboxBoxes KindOf(CilboxBasisCommon box) => box is CilboxSceneBasis ? BasisCilboxBoxes.World : box is CilboxPropBasis ? BasisCilboxBoxes.Prop : box is CilboxAvatarBasis ? BasisCilboxBoxes.Avatar : BasisCilboxBoxes.None;

		internal static bool AllowsType(string sType, BasisCilboxBoxes kind)
		{
			Load();
			return kind != BasisCilboxBoxes.None && types.TryGetValue(sType, out BasisCilboxBoxes boxes) && (boxes & kind) != 0;
		}

		internal static bool AllowsField(string sType, string sField, BasisCilboxBoxes kind)
		{
			Load();
			if (kind == BasisCilboxBoxes.None) return false;
			if (allFields.TryGetValue(sType, out BasisCilboxBoxes boxes) && (boxes & kind) != 0) return true;
			return fields.TryGetValue(sType + "." + sField, out boxes) && (boxes & kind) != 0;
		}

		internal static bool Blocks(Type declaringType, string name, BasisCilboxBoxes kind)
		{
			Load();
			return declaringType != null && kind != BasisCilboxBoxes.None && blocks.TryGetValue(declaringType, out Dictionary<string, BasisCilboxBoxes> names) && names.TryGetValue(name, out BasisCilboxBoxes boxes) && (boxes & kind) != 0;
		}

		private static void Load()
		{
			if (types != null) return;
			lock (gate)
			{
				if (types != null) return;
				var foundTypes = new Dictionary<string, BasisCilboxBoxes>(StringComparer.Ordinal);
				var foundAllFields = new Dictionary<string, BasisCilboxBoxes>(StringComparer.Ordinal);
				var foundFields = new Dictionary<string, BasisCilboxBoxes>(StringComparer.Ordinal);
				var foundBlocks = new Dictionary<Type, Dictionary<string, BasisCilboxBoxes>>();
				string sdk = typeof(BasisCilboxAllowAttribute).Assembly.GetName().Name;
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					if (assembly.IsDynamic || !References(assembly, sdk)) continue;
					Type[] defined;
					try
					{
						defined = assembly.GetTypes();
					}
					catch (ReflectionTypeLoadException e)
					{
						defined = e.Types;
					}
					catch (Exception)
					{
						continue;
					}
					foreach (Type type in defined)
					{
						if (type == null || type.IsNested) continue;
						try
						{
							Scan(type, BasisCilboxBoxes.None, foundTypes, foundAllFields, foundFields, foundBlocks);
						}
						catch (Exception e)
						{
							Debug.LogWarning($"[Cilbox] Could not read the script whitelist attributes on {type.FullName}: {e.Message}");
						}
					}
				}
				allFields = foundAllFields;
				fields = foundFields;
				blocks = foundBlocks;
				types = foundTypes;
			}
		}

		private static bool References(Assembly assembly, string sdk)
		{
			if (assembly.GetName().Name == sdk) return true;
			try
			{
				foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
				{
					if (reference.Name == sdk) return true;
				}
			}
			catch (Exception)
			{
			}
			return false;
		}

		private static void Scan(Type type, BasisCilboxBoxes inherited, Dictionary<string, BasisCilboxBoxes> foundTypes, Dictionary<string, BasisCilboxBoxes> foundAllFields, Dictionary<string, BasisCilboxBoxes> foundFields, Dictionary<Type, Dictionary<string, BasisCilboxBoxes>> foundBlocks)
		{
			BasisCilboxAllowAttribute allow = type.GetCustomAttribute<BasisCilboxAllowAttribute>(false);
			BasisCilboxBoxes boxes = inherited | (allow != null ? allow.Boxes : BasisCilboxBoxes.None);
			if (boxes != BasisCilboxBoxes.None && type.FullName != null)
			{
				string name = Cilbox.GetSanitizedTypeName(type);
				Merge(foundTypes, name, boxes);
				if (allow != null && allow.AllFields) Merge(foundAllFields, name, allow.Boxes);
				foreach (FieldInfo field in type.GetFields(Members))
				{
					BasisCilboxAllowAttribute fieldAllow = field.GetCustomAttribute<BasisCilboxAllowAttribute>(false);
					if (fieldAllow != null) Merge(foundFields, name + "." + field.Name, fieldAllow.Boxes);
				}
				foreach (MemberInfo member in type.GetMembers(Members))
				{
					BasisCilboxBlockAttribute block = member.GetCustomAttribute<BasisCilboxBlockAttribute>(false);
					if (block == null) continue;
					if (!foundBlocks.TryGetValue(type, out Dictionary<string, BasisCilboxBoxes> names))
					{
						names = new Dictionary<string, BasisCilboxBoxes>(StringComparer.Ordinal);
						foundBlocks[type] = names;
					}
					if (member is PropertyInfo property)
					{
						Merge(names, "get_" + property.Name, block.Boxes);
						Merge(names, "set_" + property.Name, block.Boxes);
					}
					else Merge(names, member.Name, block.Boxes);
				}
			}
			BasisCilboxBoxes passDown = allow != null && allow.Nested ? boxes : inherited;
			foreach (Type nested in type.GetNestedTypes(Members)) Scan(nested, passDown, foundTypes, foundAllFields, foundFields, foundBlocks);
		}

		private static void Merge<TKey>(Dictionary<TKey, BasisCilboxBoxes> into, TKey key, BasisCilboxBoxes boxes)
		{
			into.TryGetValue(key, out BasisCilboxBoxes had);
			into[key] = had | boxes;
		}
	}
}
