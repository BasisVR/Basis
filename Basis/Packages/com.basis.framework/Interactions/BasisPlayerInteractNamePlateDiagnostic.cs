using Basis.BasisUI;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.UI;
using Basis.Scripts.UI.NamePlate;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    public partial class BasisPlayerInteract
    {
        private static float _nextNamePlateDiagnosisTime;
        private static readonly StringBuilder _namePlateDiagnosis = new StringBuilder(512);

        private static void DiagnoseNamePlateMiss(BasisInteractInput interactInput, BasisInteractableObject selected)
        {
            BasisInput input = interactInput.input;
            BasisPointRaycaster caster = input.BasisPointRaycaster;
            if (caster == null || caster.PhysicHits == null) return;

            BasisRemoteNamePlate plate = null;
            RaycastHit plateHit = default;
            int count = caster.PhysicHitCount;
            for (int i = 0; i < count; i++)
            {
                Collider c = caster.PhysicHits[i].collider;
                if (c != null && BasisInteractableObject.OwnerOfCollider(c) is BasisRemoteNamePlate p)
                {
                    if (plate == null || caster.PhysicHits[i].distance < plateHit.distance)
                    {
                        plate = p;
                        plateHit = caster.PhysicHits[i];
                    }
                }
            }
            if (plate == null || ReferenceEquals(selected, plate) || plate.IsHoveredBy(input) || plate.IsInteractingWith(input)) return;

            StringBuilder sb = _namePlateDiagnosis;
            sb.Clear();
            sb.Append("[NamePlateDiag] ray hit plate of ").Append(plate.BasisRemotePlayer != null ? plate.BasisRemotePlayer.DisplayName : "?");
            sb.Append(" at ").Append(plateHit.distance.ToString("0.00")).Append("m, selected=").Append(selected != null ? selected.name : "none");
            float maxDistance = AvatarScaledRange(raycastDistance);
            sb.Append(" | reach=").Append(plate.RayReach(maxDistance).ToString("0.00"));
            if (caster.FirstHit(out RaycastHit first, maxDistance) && first.collider != null)
            {
                sb.Append(" | firstHit=").Append(first.collider.name).Append(" layer=").Append(LayerMask.LayerToName(first.collider.gameObject.layer))
                  .Append(" d=").Append(first.distance.ToString("0.00")).Append(" blocker=").Append(IsRayBlocker(first.collider));
            }
            for (int i = 0; i < count; i++)
            {
                RaycastHit h = caster.PhysicHits[i];
                if (h.collider == null || h.distance >= plateHit.distance || BasisInteractableObject.OwnerOfCollider(h.collider) != null) continue;
                if (IsRayBlocker(h.collider))
                {
                    sb.Append(" | blockerBefore=").Append(h.collider.name).Append(" layer=").Append(LayerMask.LayerToName(h.collider.gameObject.layer)).Append(" d=").Append(h.distance.ToString("0.00"));
                }
            }
            sb.Append(" | menuOnly=").Append(BasisRemoteNamePlateDriver.NamePlateHoverMenuOnly).Append(" menuOpen=").Append(BasisMainMenu.Instance != null);
            sb.Append(" enabled=").Append(plate.InteractableEnabled);
            BasisUIRaycast ui = input.BasisUIRaycast;
            if (ui != null)
            {
                sb.Append(" uiTarget=").Append(ui.HadRaycastUITarget).Append(" uiSurface=").Append(ui.HadUISurface);
                if (ui.HadUISurface)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Collider c = caster.PhysicHits[i].collider;
                        if (c == null) continue;
                        Canvas canvas = c.GetComponentInParent<Canvas>(true);
                        if (canvas != null && canvas.isActiveAndEnabled)
                        {
                            sb.Append(" canvas=").Append(canvas.name).Append("(").Append(c.name).Append(", ").Append(LayerMask.LayerToName(c.gameObject.layer)).Append(", d=").Append(caster.PhysicHits[i].distance.ToString("0.00")).Append(")");
                        }
                    }
                }
            }
            sb.Append(" | role=").Append(input.TryGetRole(out var role) ? role.ToString() : "none");
            sb.Append(" inRange=").Append(plate.IsWithinRange(input.Control != null ? input.Control.OutgoingWorldData.position : caster.ray.origin, plate.InteractRange));
            sb.Append(" canHover=").Append(plate.CanHover(input)).Append(" influencable=").Append(plate.IsInfluencable(input));
            sb.Append(" lastTarget=").Append(interactInput.lastTarget != null ? interactInput.lastTarget.name : "none");

            if (Time.unscaledTime < _nextNamePlateDiagnosisTime) return;
            _nextNamePlateDiagnosisTime = Time.unscaledTime + 1f;
            BasisDebug.LogWarning(sb.ToString(), BasisDebug.LogTag.Input);
        }
    }
}
