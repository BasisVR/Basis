using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    public sealed class BasisNamePlateVisual : IBasisNamePlateBakeTarget
    {
        private readonly string meshName;
        private Mesh generatedMesh;
        private string displayName;
        private bool needsMeshUpdate;
        private bool bakeQueued;

        public MeshFilter MeshFilter { get; private set; }
        public MeshRenderer MeshRenderer { get; private set; }
        public string DisplayName => displayName;
        public string MeshName => meshName;
        public bool IsBakeTargetValid => MeshFilter != null && MeshRenderer != null;

        public BasisNamePlateVisual(string meshName)
        {
            this.meshName = meshName;
        }

        public void Attach(MeshFilter filter, MeshRenderer renderer)
        {
            MeshFilter = filter;
            MeshRenderer = renderer;
            ConfigureRenderer(renderer);
        }

        public void SetDisplayName(string newName)
        {
            if (displayName == newName) return;
            displayName = newName;
            needsMeshUpdate = true;
        }

        public void QueueMeshRefreshIfNeeded()
        {
            if (!needsMeshUpdate || bakeQueued) return;
            bakeQueued = true;
            BasisNamePlateMeshBaker.QueueBake(this);
        }

        public void RefreshMeshNowIfNeeded()
        {
            if (!needsMeshUpdate) return;
            needsMeshUpdate = false;
            bakeQueued = false;
            Mesh mesh = BasisNamePlateMeshBaker.BakeNow(displayName, MeshFilter, MeshRenderer, meshName);
            OnBakeCompleted(mesh);
        }

        public void OnBakeCompleted(Mesh mesh)
        {
            bakeQueued = false;
            needsMeshUpdate = false;
            if (generatedMesh != null && generatedMesh != mesh)
            {
                Object.Destroy(generatedMesh);
            }
            generatedMesh = mesh;
        }

        public void ApplyMaterial()
        {
            if (MeshRenderer != null && BasisNamePlateAssets.SelectedMaterial != null)
            {
                MeshRenderer.sharedMaterial = BasisNamePlateAssets.SelectedMaterial;
            }
        }

        public void Dispose()
        {
            if (generatedMesh != null)
            {
                Object.Destroy(generatedMesh);
                generatedMesh = null;
            }
        }

        private static void ConfigureRenderer(MeshRenderer renderer)
        {
            if (renderer == null) return;

            BasisNamePlateAssets.Initialize();
            renderer.sharedMaterial = BasisNamePlateAssets.SelectedMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }
    }
}
