using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {

public class JiggleTreeSegment {

    public Transform transform { get; private set; }
    public JiggleTree jiggleTree { get; private set; }
    public JiggleTreeSegment parent { get; private set; }
    // Direct child segments, maintained in SetParent (the only place parent changes). Lets
    // RemoveJiggleTreeSegment re-parent children in O(children) instead of scanning every
    // registered segment. Lazy (null until the first child) so childless segments cost nothing.
    private List<JiggleTreeSegment> children;
    public List<JiggleTreeSegment> GetChildren() => children;
    private IJiggleParameterProvider jiggleProvider;
    public JiggleRigData jiggleRigData => jiggleProvider.GetJiggleRigData();
    
    private static List<JigglePointParameters> parametersCache;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Initialize() {
        parametersCache = new();
    }

    public void SetParent(JiggleTreeSegment jiggleTree) {
        parent?.SetDirty();
        parent?.children?.Remove(this);
        parent = jiggleTree;
        if (parent != null) {
            parent.children ??= new List<JiggleTreeSegment>();
            parent.children.Add(this);
        }
        parent?.SetDirty();
        JigglePhysics.SetGlobalDirty();
    }

    public JiggleTreeSegment(IJiggleParameterProvider jiggleProvider) {
        this.jiggleProvider = jiggleProvider;
        var rig = jiggleProvider.GetJiggleRigData();
        transform = rig.rootBone;
        animatedParameters = jiggleProvider.HasAnimatedParameters;
        JigglePhysics.SetGlobalDirty();
    }

    /// <summary>
    /// Mirror of the provider's HasAnimatedParameters, so the per-frame prepare never pays an
    /// interface call per root. Seeded in the constructor; a provider that flips the flag at
    /// runtime must push the change through <see cref="SetAnimatedParameters"/> (JiggleRig does)
    /// or the parameter push for this rig stops following the flag.
    /// </summary>
    public bool animatedParameters { get; private set; }

    public void SetAnimatedParameters(bool value) {
        if (animatedParameters == value) return;
        animatedParameters = value;
        JigglePhysics.MarkAnimatedRootsDirty();
    }

    private System.Action<JiggleTree> _onDirty;
    private void OnDirty(JiggleTree obj) {
        SetDirty();
    }

    public bool GetHasAnimatedParameters() {
        return jiggleTree != null && jiggleProvider.HasAnimatedParameters;
    }

    public void UpdateParametersIfNeeded() {
        if (jiggleTree != null && jiggleProvider.HasAnimatedParameters) {
            jiggleRigData.UpdateParameters(jiggleTree, parametersCache);
        }
    }
    
    public void UpdateParameters() {
        if (jiggleTree != null) {
            jiggleRigData.UpdateParameters(jiggleTree, parametersCache);
        }
    }
    

    public void RegenerateJiggleTreeIfNeeded() {
        _onDirty ??= OnDirty;
        if (jiggleTree == null) {
            jiggleTree = JigglePhysics.CreateJiggleTree(jiggleRigData, jiggleTree);
            jiggleTree.dirtied += _onDirty;
            return;
        }
        if (jiggleTree.dirty) {
            jiggleTree.dirtied -= _onDirty;
            jiggleTree = JigglePhysics.CreateJiggleTree(jiggleRigData, jiggleTree);
            jiggleTree.dirtied += _onDirty;
        }
    }

    public void SetDirty() {
        if (jiggleTree is { dirty: false }) {
            JigglePhysics.ScheduleRemoveJiggleTree(jiggleTree);
            jiggleTree.SetDirty();
        }
        parent?.SetDirty();
        JigglePhysics.SetGlobalDirty();
    }

    public void Teleport(float3 deltaPosition) {
        if (jiggleTree == null) return;
        JigglePhysics.Teleport(jiggleTree, deltaPosition);
    }

    public void Teleport(quaternion deltaRotation, float3 pivot, float3 deltaPosition) {
        if (jiggleTree == null) return;
        JigglePhysics.Teleport(jiggleTree, deltaRotation, pivot, deltaPosition);
    }

}

}
