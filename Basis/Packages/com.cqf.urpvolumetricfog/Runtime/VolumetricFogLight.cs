using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Light))]
public sealed class VolumetricFogLight : MonoBehaviour
{
    [Tooltip("How strongly this light lights the volumetric fog. 0 keeps the light out of the fog while it still lights surfaces.")]
    [Min(0.0f)]
    public float Multiplier = 1.0f;

    private static readonly Dictionary<EntityId, VolumetricFogLight> Registered = new();

    private Light target;
    private EntityId lightEntityId;

    public static bool AnyActive
    {
        get
        {
            foreach (VolumetricFogLight fogLight in Registered.Values)
            {
                if (fogLight.target != null && fogLight.target.isActiveAndEnabled) return true;
            }
            return false;
        }
    }

    public static bool TryGetMultiplier(Light light, out float multiplier)
    {
        multiplier = 1.0f;
        if (light == null || !Registered.TryGetValue(light.GetEntityId(), out VolumetricFogLight fogLight)) return false;

        multiplier = Mathf.Max(0.0f, fogLight.Multiplier);
        return true;
    }

    private void OnEnable()
    {
        if (!TryGetComponent(out target)) return;

        lightEntityId = target.GetEntityId();
        Registered[lightEntityId] = this;
    }

    private void OnDisable()
    {
        if (lightEntityId == EntityId.None) return;

        Registered.Remove(lightEntityId);
        lightEntityId = EntityId.None;
    }
}
