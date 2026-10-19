using Basis.Network.Core.Compute;
using System;
using System.IO;
using System.Reflection;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    /// <summary>
    /// Finds the optional compute backend and hands back a solver, or nothing.
    ///
    /// <para><b>Resolved by name rather than referenced, for the same reason the first-boot tuner
    /// runs as a child process.</b> The backend carries ILGPU; this assembly has an asmdef and is
    /// compiled by Unity, where that dependency would have to be vendored as a managed DLL and
    /// survive IL2CPP to serve a path the client never takes. Going through
    /// <see cref="Assembly.LoadFrom"/> means the type is never named at compile time, so Unity sees
    /// nothing, and a server on a host with no GPU pays one <see cref="File.Exists"/>.</para>
    ///
    /// <para>Every failure here is ordinary rather than exceptional — no file, no driver, no
    /// device, a kernel that will not compile — and every one of them means the same thing to the
    /// caller, which is that the sweep stays on the CPU.</para>
    /// </summary>
    public static class BasisComputeBackend
    {
        private const string AssemblyFileName = "BasisNetworkCompute.dll";

        /// <summary>What was tried and what came back, for the boot log.</summary>
        public static string Status { get; private set; } = "not attempted";

        public static IBasisDistanceSolver TryLoadDistanceSolver(int baseIntervalMs, string deviceSelector)
        {
            string directory = AppContext.BaseDirectory;
            string path = Path.Combine(directory, AssemblyFileName);
            if (!File.Exists(path))
            {
                Status = $"no {AssemblyFileName} beside the server";
                return null;
            }

            try
            {
                Assembly assembly = LoadBackendAssembly(path);
                Type factory = assembly.GetType(BasisComputeFactoryTypeName);
                if (factory == null)
                {
                    Status = $"{AssemblyFileName} has no {BasisComputeFactoryTypeName}";
                    return null;
                }

                MethodInfo method = factory.GetMethod(BasisComputeFactoryMethodName,
                    BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    Status = $"{BasisComputeFactoryTypeName} has no {BasisComputeFactoryMethodName}";
                    return null;
                }

                object[] arguments = { baseIntervalMs, deviceSelector, null };
                object solver = method.Invoke(null, arguments);
                string failure = arguments[2] as string;

                if (solver is IBasisDistanceSolver typed)
                {
                    Status = $"{typed.Backend} ({typed.DeviceName})";
                    return typed;
                }

                Status = failure ?? "the backend returned no solver";
                return null;
            }
            catch (Exception ex)
            {
                // A TargetInvocationException here is the factory's own failure and its inner
                // exception is the only part worth reporting; anything else is a load problem.
                Exception reported = (ex as TargetInvocationException)?.InnerException ?? ex;
                Status = $"{reported.GetType().Name}: {reported.Message}";
                return null;
            }
        }

        /// <summary>
        /// The devices an operator may choose between, one per line, or null when the backend is
        /// not present. Logged at startup whenever there is more than one, because a setting that
        /// selects from a list is useless without the list.
        /// </summary>
        public static string DescribeDevices()
        {
            string path = Path.Combine(AppContext.BaseDirectory, AssemblyFileName);
            if (!File.Exists(path)) return null;

            try
            {
                Assembly assembly = LoadBackendAssembly(path);
                Type factory = assembly.GetType(BasisComputeFactoryTypeName);
                MethodInfo method = factory?.GetMethod("DescribeDevices", BindingFlags.Public | BindingFlags.Static);
                return method?.Invoke(null, null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Assembly LoadBackendAssembly(string path)
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.LoadFromPath(path);
#else
            return Assembly.LoadFrom(path);
#endif
        }

        private const string BasisComputeFactoryTypeName = "Basis.Network.Compute.BasisComputeFactory";
        private const string BasisComputeFactoryMethodName = "TryCreateDistanceSolver";
    }
}
