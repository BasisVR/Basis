using UnityEngine;
using Basis.Scripts.BasisSdk.Players;

namespace Basis.Scripts.UI
{
	/// <summary>
	/// Parents the parent UI to the LocalPlayer in case the distance between player and panel goes beyond a given threshold in metres. 
	/// This allows using the UI even if perpetually falling or just generally while moving.
	/// Only setup required is attaching the script to the root of the UIs GameObject.
	/// </summary>
	public class BasisUIReparenter : MonoBehaviour
	{
		public float maxDistance = 4;
		private Vector3 initialUIPos;
		private Vector3 initialOffset;
		private float currentDistance;
		private bool reparented;

		private void Start()
		{
			Vector3 _initialPlayerPos = BasisLocalPlayer.Instance.transform.position;
			initialUIPos = gameObject.transform.position;
			initialOffset = initialUIPos - _initialPlayerPos;
		}
		private void LateUpdate()
		{
			if(reparented)
				return;
			currentDistance = (initialUIPos - BasisLocalPlayer.Instance.transform.position).magnitude;
			if(currentDistance > maxDistance)
				Reparent();
		}
		private void Reparent()
		{
			reparented = true;
			gameObject.transform.SetParent(BasisLocalPlayer.Instance.transform,false);
			gameObject.transform.position = BasisLocalPlayer.Instance.transform.position + initialOffset;
		}
	}
}