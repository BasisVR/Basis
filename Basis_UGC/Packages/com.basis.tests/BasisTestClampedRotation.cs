using UnityEngine;

namespace Basis.Scripts.Tests
{
public class BasisTestClampedRotation : MonoBehaviour
{
    public Transform lookTarget;
    public Quaternion CalibrationRotation;
    public Vector3 RotationLimits;
    public float clampAngle;
    public void Start()
    {
        CalibrationRotation = this.transform.rotation;
        RotationLimits = CalibrationRotation.eulerAngles;
    }
    public void Update()
    {
        UpdateRotation();
    }

    private void UpdateRotation()
    {
        Quaternion targetRotation = lookTarget.rotation;

        float xAngle = Mathf.Clamp(targetRotation.eulerAngles.x, CalibrationRotation.eulerAngles.x - RotationLimits.x, CalibrationRotation.eulerAngles.x + RotationLimits.x);
        float yAngle = Mathf.Clamp(targetRotation.eulerAngles.y, CalibrationRotation.eulerAngles.y - RotationLimits.y, CalibrationRotation.eulerAngles.y + RotationLimits.y);
        float zAngle = Mathf.Clamp(targetRotation.eulerAngles.z, CalibrationRotation.eulerAngles.z - RotationLimits.z, CalibrationRotation.eulerAngles.z + RotationLimits.z);

        Quaternion newRotation = Quaternion.Euler(xAngle, yAngle, zAngle);
        Quaternion finalRotation = Quaternion.RotateTowards(CalibrationRotation, newRotation, clampAngle);

        transform.rotation = finalRotation;
    }
}
}