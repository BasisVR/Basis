using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class BasisContentSphereBobTests
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    GameObject _go;

    [TearDown]
    public void TearDown()
    {
        BasisContentSphereBillboardDriver.Dispose();
        if (_go != null) Object.DestroyImmediate(_go);
    }

    [Test]
    public void OrbsBobAndSpinTheWayTheOldPerOrbUpdateDid()
    {
        Vector3 rest = new Vector3(1f, 2f, 3f);
        Quaternion rotation = Quaternion.Euler(0f, 10f, 0f);
        _go = new GameObject("orb");
        _go.transform.SetPositionAndRotation(rest, rotation);
        var orb = _go.AddComponent<BasisContentSphere>();
        MethodInfo start = typeof(BasisContentSphere).GetMethod("Start", NP);
        FieldInfo phaseField = typeof(BasisContentSphere).GetField("_bobPhase", NP);
        Assert.IsNotNull(start, "Start moved");
        Assert.IsNotNull(phaseField, "_bobPhase moved");
        start.Invoke(orb, null);
        float phase = (float)phaseField.GetValue(orb);

        for (int frame = 0; frame < 120; frame++)
        {
            float dt = 1f / 60f + frame * 0.0001f;
            BasisContentSphereBillboardDriver.ScheduleBob(dt);
            BasisContentSphereBillboardDriver.Complete();
            phase += dt * BasisContentSphere.BobPhaseClock;
            Vector3 expected = rest + Vector3.up * (Mathf.Sin(phase) * BasisContentSphere.BobPhaseOffset);
            rotation = Quaternion.AngleAxis(BasisContentSphere.RotationSpeed * dt, Vector3.up) * rotation;
            Assert.LessOrEqual(Vector3.Distance(_go.transform.position, expected), 1e-5f, $"frame {frame} position");
            Assert.LessOrEqual(Quaternion.Angle(_go.transform.rotation, rotation), 0.02f, $"frame {frame} rotation");
        }
    }

    [Test]
    public void ADestroyedOrbLeavesTheOthersBobbing()
    {
        _go = new GameObject("orb-a");
        var other = new GameObject("orb-b");
        try
        {
            MethodInfo start = typeof(BasisContentSphere).GetMethod("Start", NP);
            var a = _go.AddComponent<BasisContentSphere>();
            var b = other.AddComponent<BasisContentSphere>();
            start.Invoke(a, null);
            start.Invoke(b, null);
            BasisContentSphereBillboardDriver.ScheduleBob(0.016f);
            BasisContentSphereBillboardDriver.Complete();
            BasisContentSphereBillboardDriver.UnregisterSphere(b);
            Object.DestroyImmediate(other);
            Vector3 before = _go.transform.position;
            Assert.DoesNotThrow(() => { BasisContentSphereBillboardDriver.ScheduleBob(0.25f); BasisContentSphereBillboardDriver.Complete(); });
            Assert.AreNotEqual(before, _go.transform.position, "the surviving orb keeps moving");
        }
        finally
        {
            if (other != null) Object.DestroyImmediate(other);
        }
    }
}
