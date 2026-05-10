using UnityEditor;

namespace HVR.Basis.NDMF
{
    [InitializeOnLoad]
    public class BasisNDMFInitPatch
    {
        private const string SessionStateInitPatchKey = "dev.hai-vr.basis.ndmf.initpatch";

        static BasisNDMFInitPatch()
        {
            if (!SessionState.GetBool(SessionStateInitPatchKey, false))
            {
                // This is a patch to disable NDMF's "Apply On Play" setting in Basis.
                //
                // When a user is working on their avatar in the scene, and they enter Play Mode with the NDMF "Apply On Play" setting ON,
                // this causes a side effect with the avatar's JiggleRig components.
                // It's not entirely clear why, but this causes the simulated hair in both the work avatar and the real avatar to become longer
                // after the "Test In Editor" button is pressed. The JiggleRig rest state might be saved incorrectly when NDMF runs on Play.
                // This issue is similar to #351.
                //
                // Either way, in Basis, we don't want avatars in the scene to be automatically processed by NDMF when entering Play Mode.
                // NDMF should only be processed when the user explicitly clicks the "Test In Editor" button on the work avatar, which may happen
                // multiple times per Play Mode session. The ApplyOnPlay bool does not affect this.
                // This also ensures that the pre-processing and the post-processing from BasisAvatarSDKInspector.LoadAvatar(...) executes
                // in the correct order.
                nadena.dev.ndmf.config.Config.ApplyOnPlay = false;

                // SessionState is lost when the Editor closes. This ensures that if a developer for whatever reason wants to enable "Apply On Play"
                // for the rest of the session, they can still do so.
                SessionState.SetBool(SessionStateInitPatchKey, true);
            }
        }
    }
}
