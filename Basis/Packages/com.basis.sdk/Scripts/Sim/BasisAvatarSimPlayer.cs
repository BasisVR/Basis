#if !BASIS_FRAMEWORK_EXISTS
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Players
{
    // Runtime tick host for the SDK's editor preview. Attached to the
    // preview root after the avatar is loaded so blink + visemes have somewhere
    // to live in play mode.
    public sealed class BasisAvatarSimPlayer : BasisPlayer
    {
        public BasisLocalFacialBlinkDriver Blink = new BasisLocalFacialBlinkDriver();
        public BasisAudioAndVisemeDriver Viseme = new BasisAudioAndVisemeDriver();

        public void Bootstrap(BasisAvatar avatar)
        {
            IsLocal = true;
            BasisAvatar = avatar;
            FaceIsVisible = true;

            if (avatar.FaceVisemeMesh != null)
            {
                FaceRenderer = BasisHelpers.GetOrAddComponent<BasisMeshRendererCheck>(avatar.FaceVisemeMesh.gameObject);
            }

            // Zero animator params for an idle-pose preview. The locomotion controller defaults
            // CurrentSpeed to 1, which puts the avatar mid-walk before the user touches anything.
            // The foldout's sliders still let the user drive these afterwards.
            if (avatar.Animator != null && avatar.Animator.runtimeAnimatorController != null)
            {
                foreach (var p in avatar.Animator.parameters)
                {
                    if (avatar.Animator.IsParameterControlledByCurve(p.nameHash)) continue;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float: avatar.Animator.SetFloat(p.nameHash, 0f); break;
                        case AnimatorControllerParameterType.Bool: avatar.Animator.SetBool(p.nameHash, false); break;
                        case AnimatorControllerParameterType.Int: avatar.Animator.SetInteger(p.nameHash, 0); break;
                    }
                }
            }

            Blink.Initialize(this, avatar);
            Viseme.TryInitialize(this);
        }

        private void Update()
        {
            Blink.Simulate(Time.timeAsDouble);
            Viseme.Simulate(Time.deltaTime);
            BasisOpenLipSyncContext.ProcessAllPending();
            Viseme.Apply();
        }

        private void OnDestroy()
        {
            Blink.OnDestroy();
            Viseme.OnDestroy();
        }
    }
}
#endif
