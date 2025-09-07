using Unity.Collections;
using Unity.Mathematics;
using Unity.Burst;
using Unity.Jobs;
using UnityEngine;

[BurstCompile]
public struct PDO_IKSolver : IJob
{
	// Input data (read-only)
	[ReadOnly] public NativeArray<DHParameters> dhParams;
	[ReadOnly] public NativeArray<float> jointLimitsMin;
	[ReadOnly] public NativeArray<float> jointLimitsMax;
	[ReadOnly] public float3 targetPosition;
	[ReadOnly] public quaternion targetRotation;
	[ReadOnly] public int maxIterations;
	[ReadOnly] public float tolerance;

	// Working data
	public NativeArray<float> omega; // Optimization variables (slack variables)
	public NativeArray<float4x4> transformMatrices; // Ti matrices
	public NativeArray<float3> jointPositions; // ui positions
	public NativeArray<float> jacobian; // Flattened jacobian matrix

	// Output
	public NativeArray<float> solutionAngles;

	public void Execute()
	{
		// Main PDO-IK algorithm loop
		for (int iteration = 0; iteration < maxIterations; iteration++)
		{
			float objective = ForwardRollout();
			ComputeJacobian();

			bool converged = UpdateOptimizationVariables();
			if (converged) break;
		}

		// Convert final omega to joint angles
		ConvertOmegaToAngles();
	}

	/// <summary>
	/// Algorithm 1: Forward Rollout - Propagative computation along kinematic chain
	/// </summary>
	[BurstCompile]
	private float ForwardRollout()
	{
		// Initialize with world frame
		transformMatrices[0] = float4x4.identity;

		// Propagate transformations along kinematic chain
		for (int i = 1; i < dhParams.Length; i++)
		{
			// Convert omega to Li using squashing function (Eq. 4.11a)
			float Li = ComputeSquashedDistance(omega[i - 1], jointLimitsMin[i - 1], jointLimitsMax[i - 1]);

			// Compute modified DH transformation matrix (Eq. 4.3)
			float4x4 localTransform = ComputeModifiedDHMatrix(Li, dhParams[i - 1]);

			// Propagative multiplication: Ti = Ti-1 * (i-1)Ti
			transformMatrices[i] = math.mul(transformMatrices[i - 1], localTransform);

			// Extract joint position: ui = [Ti(1,4), Ti(2,4), Ti(3,4)]^T
			jointPositions[i - 1] = new float3(
				transformMatrices[i].c3.x,
				transformMatrices[i].c3.y,
				transformMatrices[i].c3.z
			);
		}

		// Compute end effector pose objective (Eq. 4.16)
		float endEffectorObjective = ComputeEndEffectorObjective();

		return endEffectorObjective;
	}

	/// <summary>
	/// Algorithm 2: Jacobian Computation using reverse accumulation
	/// </summary>
	[BurstCompile]
	private void ComputeJacobian()
	{
		// Initialize jacobian array
		for (int i = 0; i < jacobian.Length; i++)
		{
			jacobian[i] = 0f;
		}

		// Compute end effector pose objective gradient (Eq. 5.6)
		ComputeEndEffectorGradient();

		// Backward propagation through kinematic chain (Eq. 5.7)
		BackwardPropagation();
	}

	/// <summary>
	/// Algorithm 3: Update optimization variables using L-BFGS approximation
	/// </summary>
	[BurstCompile]
	private bool UpdateOptimizationVariables()
	{
		// Simplified L-BFGS step (single iteration)
		// In practice, you'd implement full L-BFGS with history

		float maxGradient = 0f;
		float stepSize = 0.01f; // Adaptive step size in practice

		// Gradient descent step with line search
		for (int i = 0; i < omega.Length; i++)
		{
			float gradient = jacobian[i];
			maxGradient = math.max(maxGradient, math.abs(gradient));

			// Update omega: ω ← ω - α∇J(ω)
			omega[i] -= stepSize * gradient;
		}

		// Check convergence criteria (only gradient-based now)
		return maxGradient < tolerance;
	}

	/// <summary>
	/// Compute squashing function for joint limits (Eq. 4.11a)
	/// </summary>
	[BurstCompile]
	private float ComputeSquashedDistance(float omega, float minLimit, float maxLimit)
	{
		// Li = (L_max - L_min) * σ(ω) + L_min
		// where σ(ω) = 1/(1 + e^(-ω)) is sigmoid function
		float sigmoid = 1.0f / (1.0f + math.exp(-omega));
		float Lmin = 1.0f - math.cos(maxLimit); // Convert angle limits to distance limits
		float Lmax = 1.0f - math.cos(minLimit);
		return (Lmax - Lmin) * sigmoid + Lmin;
	}

	/// <summary>
	/// Compute modified DH transformation matrix (Eq. 4.3)
	/// </summary>
	[BurstCompile]
	private float4x4 ComputeModifiedDHMatrix(float Li, DHParameters dh)
	{
		// Trigonometric functions from distance parameterization
		float cosTheta = 1.0f - Li;
		float sinTheta = math.sqrt(math.max(0f, 2.0f * Li - Li * Li));

		float ca = math.cos(dh.alpha);
		float sa = math.sin(dh.alpha);

		// Modified DH matrix from Eq. 4.3
		return new float4x4(
			cosTheta, -sinTheta, 0f, dh.a,
			ca * sinTheta, ca * cosTheta, -sa, dh.d * sa,
			sa * sinTheta, sa * cosTheta, ca, dh.d * ca,
			0f, 0f, 0f, 1f
		);
	}

	/// <summary>
	/// Compute end effector pose objective (Eq. 4.16)
	/// </summary>
	[BurstCompile]
	private float ComputeEndEffectorObjective()
	{
		// Get end effector position from last transformation matrix
		float3 endEffectorPos = jointPositions[jointPositions.Length - 1];

		// Position error
		float3 positionError = endEffectorPos - targetPosition;
		float positionCost = 0.5f * math.dot(positionError, positionError);

		// Orientation error (simplified - full implementation would use quaternion error)
		float4x4 endEffectorTransform = transformMatrices[transformMatrices.Length - 1];
		quaternion endEffectorRotation = quaternion.LookRotationSafe(endEffectorTransform.c2.xyz, endEffectorTransform.c1.xyz);

		// Quaternion difference approximation
		quaternion rotationError = math.mul(math.inverse(targetRotation), endEffectorRotation);
		float orientationCost = 0.5f * (1.0f - math.abs(rotationError.value.w));

		return positionCost + orientationCost;
	}

	/// <summary>
	/// Compute end effector objective gradient
	/// </summary>
	[BurstCompile]
	private void ComputeEndEffectorGradient()
	{
		// ∂J/∂T_e computation (Eq. 5.6)
		float3 endEffectorPos = jointPositions[jointPositions.Length - 1];
		float3 positionError = endEffectorPos - targetPosition;

		// Add position error contribution to jacobian
		// This is simplified - full implementation requires proper matrix derivatives
		for (int i = 0; i < omega.Length; i++)
		{
			jacobian[i] += math.dot(positionError, new float3(1f)) * 0.1f; // Simplified weighting
		}
	}

	/// <summary>
	/// Backward propagation through kinematic chain (Eq. 5.7, 5.8)
	/// </summary>
	[BurstCompile]
	private void BackwardPropagation()
	{
		// Reverse accumulation from end effector to base
		// ∂J/∂T_i = ∂J/∂T_{i+1} * ∂T_{i+1}/∂T_i

		for (int i = transformMatrices.Length - 2; i >= 0; i--)
		{
			// Compute ∂J/∂T_i recursively
			// This requires proper implementation of matrix derivatives
			// Simplified version shown here

			float4x4 currentTransform = transformMatrices[i];
			float4x4 nextTransform = transformMatrices[i + 1];

			// Chain rule application would go here
			// Full implementation requires computing ∂T_{i+1}/∂T_i and propagating gradients
		}
	}

	/// <summary>
	/// Convert final omega values to joint angles (Eq. 5.9)
	/// </summary>
	[BurstCompile]
	private void ConvertOmegaToAngles()
	{
		for (int i = 0; i < omega.Length; i++)
		{
			// Li = squashing function result
			float Li = ComputeSquashedDistance(omega[i], jointLimitsMin[i], jointLimitsMax[i]);

			// θ_i = arccos(1 - L_i) + min(0, θ_min_i)
			float angle = math.acos(math.clamp(1.0f - Li, -1f, 1f));
			solutionAngles[i] = angle + math.min(0f, jointLimitsMin[i]);
		}
	}
}

/// <summary>
/// DH Parameters structure for Burst compatibility
/// </summary>
public struct DHParameters
{
	public float a;     // Link length
	public float alpha; // Link twist
	public float d;     // Link offset
						// theta is computed from Li, not stored
}

/// <summary>
/// MonoBehaviour wrapper for PDO-IK solver (IK only, no collision)
/// </summary>
public class PDOIKController : MonoBehaviour
{
	[Header("Robot Configuration")]
	public DHParameters[] dhParameters;
	public float[] jointLimitsMin;
	public float[] jointLimitsMax;

	[Header("Target")]
	public Transform target;

	[Header("Solver Settings")]
	public int maxIterations = 20;
	public float tolerance = 1e-4f;

	private NativeArray<DHParameters> nativeDHParams;
	private NativeArray<float> nativeJointLimitsMin;
	private NativeArray<float> nativeJointLimitsMax;
	private NativeArray<float> omega;
	private NativeArray<float4x4> transformMatrices;
	private NativeArray<float3> jointPositions;
	private NativeArray<float> jacobian;
	private NativeArray<float> solutionAngles;

	void Start()
	{
		InitializeNativeArrays();
	}

	void Update()
	{
		if (target != null)
		{
			SolvePDOIK();
		}
	}

	void InitializeNativeArrays()
	{
		int jointCount = dhParameters.Length;

		nativeDHParams = new NativeArray<DHParameters>(dhParameters, Allocator.Persistent);
		nativeJointLimitsMin = new NativeArray<float>(jointLimitsMin, Allocator.Persistent);
		nativeJointLimitsMax = new NativeArray<float>(jointLimitsMax, Allocator.Persistent);

		// Initialize working arrays
		omega = new NativeArray<float>(jointCount, Allocator.Persistent);
		transformMatrices = new NativeArray<float4x4>(jointCount + 1, Allocator.Persistent);
		jointPositions = new NativeArray<float3>(jointCount, Allocator.Persistent);
		jacobian = new NativeArray<float>(jointCount, Allocator.Persistent);
		solutionAngles = new NativeArray<float>(jointCount, Allocator.Persistent);
	}

	void SolvePDOIK()
	{
		var solver = new PDO_IKSolver
		{
			dhParams = nativeDHParams,
			jointLimitsMin = nativeJointLimitsMin,
			jointLimitsMax = nativeJointLimitsMax,
			targetPosition = target.position,
			targetRotation = target.rotation,
			maxIterations = maxIterations,
			tolerance = tolerance,
			omega = omega,
			transformMatrices = transformMatrices,
			jointPositions = jointPositions,
			jacobian = jacobian,
			solutionAngles = solutionAngles
		};

		JobHandle handle = solver.Schedule();
		handle.Complete();

		// Apply solution to robot joints
		ApplySolutionToRobot();
	}

	void ApplySolutionToRobot()
	{
		// Apply computed joint angles to your robot representation
		for (int i = 0; i < solutionAngles.Length; i++)
		{
			float angle = solutionAngles[i];
			// Apply angle to joint i
			Debug.Log($"Joint {i}: {angle * Mathf.Rad2Deg} degrees");
		}
	}

	void OnDestroy()
	{
		// Dispose all native arrays
		if (nativeDHParams.IsCreated) nativeDHParams.Dispose();
		if (nativeJointLimitsMin.IsCreated) nativeJointLimitsMin.Dispose();
		if (nativeJointLimitsMax.IsCreated) nativeJointLimitsMax.Dispose();
		if (omega.IsCreated) omega.Dispose();
		if (transformMatrices.IsCreated) transformMatrices.Dispose();
		if (jointPositions.IsCreated) jointPositions.Dispose();
		if (jacobian.IsCreated) jacobian.Dispose();
		if (solutionAngles.IsCreated) solutionAngles.Dispose();
	}
}
