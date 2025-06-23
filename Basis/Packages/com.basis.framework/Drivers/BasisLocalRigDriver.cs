using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

[SerializeField]
public class BasisLocalRigDriver : MonoBehaviour
{
    [SerializeField] private Dictionary<string, Rig> rigs;
    [SerializeField] private Dictionary<string, RigLayer> rigLayers;

    public RigBuilder Builder;

	public void Initialize(BasisLocalPlayer player)
	{
		Builder = BasisHelpers.GetOrAddComponent<RigBuilder>(player.BasisAvatar.Animator.gameObject);
		Builder.enabled = false;
	}

	public GameObject CreateOrGetRig(string Role, bool Enabled, out Rig Rig, out RigLayer RigLayer)
	{
		foreach (RigLayer Layer in Builder.layers)
		{
			if (Layer.rig.name == $"Rig {Role}")
			{
				RigLayer = Layer;
				Rig = Layer.rig;
				return Layer.rig.gameObject;
			}
		}
		GameObject RigGameobject = BasisAnimationRiggingHelper.CreateAndSetParent(Player.BasisAvatar.Animator.transform, $"Rig {Role}");
		Rig = BasisHelpers.GetOrAddComponent<Rig>(RigGameobject);
		Rigs.Add(Rig);
		RigLayer = new RigLayer(Rig, Enabled);
		Builder.layers.Add(RigLayer);
		return RigGameobject;
	}

	public void Cleanup()
    {
        foreach(var rig in rigs)
        {
            Destroy(rig.Value.gameObject);
        }
    }
}
