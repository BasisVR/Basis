using UnityEngine;
using System;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using UnityEngine;


public static class AnUtils
{


    public static void OnHand(
        Transform transform,
        float scaleDirection,
        float minScale,
        float maxScale,
        Renderer rend,
        MaterialPropertyBlock mpb,
        Color color,
        string name
        )
    {

        Vector3 vector = Vector3.one;
        vector *= scaleDirection / 200f;

        Vector3 newScale = transform.localScale;
        newScale += vector;
        newScale.x = Mathf.Clamp(newScale.x, minScale, maxScale);
        newScale.y = Mathf.Clamp(newScale.y, minScale, maxScale);
        newScale.z = Mathf.Clamp(newScale.z, minScale, maxScale);


        transform.localScale = newScale;
        float t = Mathf.InverseLerp(minScale, maxScale, newScale.y);
        Color darkened = Color.Lerp(color, Color.black, t);
        AnUtils.SetColorWithPropertyBlock(rend, mpb, darkened, name);

    }



    // NOTE: Code below this point is entirely AI written and I have no idea what it does -An

    // Sets color via MaterialPropertyBlock. If `mpb` is null a local one is created.
    // Falls back to instancing material when no known color property exists.
    public static void SetColorWithPropertyBlock(Renderer rend, MaterialPropertyBlock mpb, Color c, string objName = null)
    {
        if (rend == null)
        {
            Debug.LogWarning($"SetColorWithPropertyBlock: no Renderer on {objName ?? "unknown object"}");
            return;
        }

        var localMpb = mpb ?? new MaterialPropertyBlock();

        // determine shader color property
        string prop = null;
        var mat = rend.sharedMaterial;
        if (mat != null)
        {
            if (mat.HasProperty("_BaseColor")) prop = "_BaseColor";
            else if (mat.HasProperty("_Color")) prop = "_Color";
        }

        if (prop == null)
        {
            Debug.LogWarning($"No known color property on material for {objName ?? rend.name}. Falling back to instancing material.");
            rend.material.color = c; // fallback (instantiates material)
            return;
        }

        localMpb.Clear();
        localMpb.SetColor(prop, c);
        rend.SetPropertyBlock(localMpb);
    }

    public static void ClearColor(Renderer rend, MaterialPropertyBlock mpb = null, string objName = null)
    {
        if (rend == null)
        {
            Debug.LogWarning($"ClearColor: no Renderer on {objName ?? "unknown object"}");
            return;
        }

        string prop = null;
        var mat = rend.sharedMaterial;
        if (mat != null)
        {
            if (mat.HasProperty("_BaseColor")) prop = "_BaseColor";
            else if (mat.HasProperty("_Color")) prop = "_Color";
        }

        if (prop == null)
        {
            Debug.LogWarning($"No known color property on material for {objName ?? rend.name}. Falling back to material reset.");
            if (mat != null)
            {
                // Reset instance material color to shared material color (will instantiate material)
                rend.material.color = mat.color;
            }
            return;
        }

        // No API to remove a single property from a MaterialPropertyBlock, so clear the block
        // and reapply an empty block to remove overrides (this clears all property overrides).
        var localMpb = mpb ?? new MaterialPropertyBlock();
        localMpb.Clear();
        rend.SetPropertyBlock(localMpb);
    }






}
