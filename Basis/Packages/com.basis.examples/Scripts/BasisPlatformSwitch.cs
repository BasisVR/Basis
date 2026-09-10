using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using UnityEngine;

[Serializable]
public class BasisPlatformSwitchRule
{
    public BasisPlatformCondition When = BasisPlatformCondition.MobileGpu;
    public bool Invert;
    public GameObject[] Enable = Array.Empty<GameObject>(), Disable = Array.Empty<GameObject>();
    public Component[] EnableComponents = Array.Empty<Component>(), DisableComponents = Array.Empty<Component>();
    public bool Destroy;
    [NonSerialized] public bool Detected, Applied;
}

public class BasisPlatformSwitch : MonoBehaviour
{
    public List<BasisPlatformSwitchRule> Rules = new List<BasisPlatformSwitchRule>();

    private void OnEnable()
    {
        BasisPlatformDetection.OnChanged -= Apply;
        BasisPlatformDetection.OnChanged += Apply;
        Apply();
    }

    private void OnDisable()
    {
        BasisPlatformDetection.OnChanged -= Apply;
    }

    public void Apply()
    {
        for (int i = 0; i < Rules.Count; i++)
        {
            BasisPlatformSwitchRule rule = Rules[i];
            if (rule == null) continue;
            bool detected = BasisPlatformDetection.IsDetected(rule.When) != rule.Invert;
            if (rule.Applied && rule.Detected == detected) continue;
            rule.Applied = true;
            rule.Detected = detected;
            bool destroy = detected && rule.Destroy && BasisPlatformDetection.IsStatic(rule.When);
            SetObjects(rule.Enable, detected, false);
            SetObjects(rule.Disable, !detected, destroy);
            SetComponents(rule.EnableComponents, detected, false);
            SetComponents(rule.DisableComponents, !detected, destroy);
        }
    }

    private static void SetObjects(GameObject[] objects, bool active, bool destroy)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Length; i++)
        {
            GameObject target = objects[i];
            if (target == null) continue;
            if (destroy) Destroy(target);
            else if (target.activeSelf != active) target.SetActive(active);
        }
    }

    private static void SetComponents(Component[] components, bool enabled, bool destroy)
    {
        if (components == null) return;
        for (int i = 0; i < components.Length; i++)
        {
            Component target = components[i];
            if (target == null) continue;
            if (destroy)
            {
                Destroy(target);
                continue;
            }
            switch (target)
            {
                case Behaviour behaviour: behaviour.enabled = enabled; break;
                case Renderer renderer: renderer.enabled = enabled; break;
                case Collider collider: collider.enabled = enabled; break;
                case LODGroup lodGroup: lodGroup.enabled = enabled; break;
                case Cloth cloth: cloth.enabled = enabled; break;
            }
        }
    }
}
