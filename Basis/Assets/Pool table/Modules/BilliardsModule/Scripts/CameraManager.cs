

using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;




public class CameraManager : MonoBehaviour
{
    [SerializeField] private GameObject[] staticCameras;
    [SerializeField] private GameObject[] dynamicCameras;

    private BilliardsModule table;

    private bool isEnabled;

    private GameObject[] cameras;
    private int currentCam;

    /*
     * Auto cam algorithm:
     *  - after local simulation ends, chance to switch to a different camera after 1-2 seconds
     *  - after player arms cue, chance to switch to dynamic camera
     *  - after ~15 seconds of no action, switch to random camera
     *  - no camera updates until 5 seconds of last update
     */
    private bool autoCam;

    private float autoCamLockedUntil;
    private float autoCamNextRandomUpdate;
    private int autoCamNextUpdateTarget;

    public void _Init(BilliardsModule table_)
    {
        table = table_;

        cameras = new GameObject[staticCameras.Length + dynamicCameras.Length];
        for (int i = 0; i < staticCameras.Length; i++)
        {
            cameras[i] = staticCameras[i];
        }
        for (int i = 0; i < dynamicCameras.Length; i++)
        {
            cameras[staticCameras.Length + i] = dynamicCameras[i];
        }

        _SetAutoCam(false);
        _SetEnabled(false);
    }

    public void _Tick()
    {
        if (currentCam == 3)
        {
            Transform dynamicCamera = cameras[currentCam].transform;

            CueController controller = table.activeCue;
            if (controller == null) return;

            BasisNetworkPlayer player = controller._GetHolder();

            Vector3 position;
            position = player.GetPosition();

            Vector3 playerRelPos = table.transform.InverseTransformPoint(position);
            Vector3 direction = -playerRelPos;

            direction = direction.normalized * 2f;

            direction.y = 2;
            dynamicCamera.localPosition = direction;

            dynamicCamera.LookAt(position);

            dynamicCamera.localRotation = Quaternion.AngleAxis(5, dynamicCamera.right) * dynamicCamera.localRotation;
        }

        if (autoCam)
        {
            updateAutoCam();
        }
    }

    private void updateAutoCam()
    {
        float now = Time.time;
        if (now >= autoCamLockedUntil && now >= autoCamNextRandomUpdate)
        {
            if (autoCamNextUpdateTarget == -1)
            {
                autoSwitchRandomCamera();
            }
            else
            {
                autoSwitchCamera(autoCamNextUpdateTarget);
            }
        }
    }

    public void _OnLocalSimEnd()
    {
        if (Random.Range(0.0f, 1.0f) > 0.6f)
        {
            autoCamNextRandomUpdate = Time.time + Random.Range(1.0f, 2.0f);
            autoCamNextUpdateTarget = -1;
        }
    }

    public void _OnPlayerPrepareShoot()
    {
        if (Random.Range(0.0f, 1.0f) > 0.8f)
        {
            autoCamNextRandomUpdate = Time.time + Random.Range(0.5f, 1.5f);
            autoCamNextUpdateTarget = 3;
        }
    }

    private void autoSwitchRandomCamera()
    {
        int cameraId = Random.Range(0, cameras.Length - 1);
        if (cameraId == currentCam) cameraId++;
        if (cameraId >= cameras.Length) cameraId = 0;

        autoSwitchCamera(cameraId);
    }

    private void autoSwitchCamera(int cameraId)
    {
        autoCamLockedUntil = Time.time + Random.Range(5.0f, 7.0f);
        autoCamNextRandomUpdate = Time.time + Random.Range(15.0f, 20.0f);
        autoCamNextUpdateTarget = -1;
        switchCamera(cameraId);
    }

    private void switchCamera(int newCam)
    {
        cameras[currentCam].SetActive(false);
        cameras[newCam].SetActive(true);
        table.cameraOverrideModule._SetTargetCamera(cameras[newCam].GetComponent<Camera>());
        currentCam = newCam;
    }

    public bool _IsAutoCam()
    {
        return autoCam;
    }

    public void _SetAutoCam(bool autoCam_)
    {
        autoCam = autoCam_;

        autoCamNextRandomUpdate = Time.time; // trigger update
        autoCamNextUpdateTarget = -1;
    }

    public int _GetCurrentCam()
    {
        return currentCam;
    }

    public void _SetCurrentCam(int newCam)
    {
        _SetAutoCam(false);
        switchCamera(newCam);
    }

    public void _SetEnabled(bool enabled_)
    {
        enabled = enabled_;

        if (cameras != null)
        {
            if (!enabled_)
            {
                for (int i = 0; i < cameras.Length; i++)
                {
                    cameras[i].SetActive(enabled);
                }

                table.cameraOverrideModule._SetRenderMode(0);
            }
            else
            {
                switchCamera(0);
                table.cameraOverrideModule._SetRenderMode(2);
            }
        }
    }

    public bool _IsEnabled()
    {
        return enabled;
    }
}
