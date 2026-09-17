using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Basis.Scripts.BasisSdk;
using Cilbox;
using NUnit.Framework;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Basis.Tests.Sync
{
    [Cilboxable]
    public sealed class BasisCilboxBuildTestBehaviour : MonoBehaviour
    {
        public static int NativeAwakeCount;
        public string Marker = "before";

        private void Awake()
        {
            NativeAwakeCount++;
        }

        public void TouchMarker()
        {
        }
    }

    [Cilboxable]
    public sealed class BasisCilboxBuildTypeFailureBehaviour : MonoBehaviour
    {
        public IntPtr ReadDisallowedType()
        {
            return IntPtr.Zero;
        }
    }

    public sealed class BasisCilboxBuildPipelineTests
    {
        [Test]
        public void PrefabFinalization_SerializesPreparedStateWithoutTouchingOtherScenes()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene externalScene = default;
            Scene contentScene = default;
            GameObject externalRoot = null;
            GameObject contentRoot = null;
            Action<Scene> handler = null;

            try
            {
                externalScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                externalRoot = new GameObject("CilboxExternalSceneRoot");
                SceneManager.MoveGameObjectToScene(externalRoot, externalScene);
                CilboxAvatarBasis externalCilbox = externalRoot.AddComponent<CilboxAvatarBasis>();
                externalCilbox.assemblyData = "external-sentinel";

                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxContentRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                CilboxAvatarBasis contentCilbox = contentRoot.AddComponent<CilboxAvatarBasis>();
                BasisCilboxBuildTestBehaviour behaviour = contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();
                behaviour.Marker = "before";

                int loadedSceneCountBefore = SceneManager.sceneCount;
                Scene activeSceneBefore = SceneManager.GetActiveScene();
                bool callbackReceivedValidLoadedScene = false;
                string callbackSceneName = null;
                handler = scene =>
                {
                    callbackReceivedValidLoadedScene = scene.IsValid() && scene.isLoaded;
                    callbackSceneName = scene.name;
                    behaviour.Marker = "prepared";
                };
                BasisCilboxBuildEvents.OnBeforeCilboxSerialize += handler;

                BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(contentRoot, null);

                Assert.AreEqual(contentScene, contentRoot.scene,
                    "The isolated content root was not restored to its owning scene after Cilbox conversion.");
                Assert.AreEqual(activeSceneBefore, SceneManager.GetActiveScene(),
                    "Cilbox finalization did not restore the active scene before closing its staging scene.");
                Assert.IsTrue(externalRoot.activeSelf,
                    "Cilbox conversion must not deactivate unrelated loaded scene roots.");
                Assert.AreEqual("external-sentinel", externalCilbox.assemblyData,
                    "Cilbox conversion mutated assembly data in another loaded scene.");
                Assert.AreEqual(loadedSceneCountBefore, SceneManager.sceneCount,
                    "The temporary Cilbox staging scene was not cleaned up.");
                Assert.IsTrue(callbackReceivedValidLoadedScene,
                    "The pre-serialization callback did not receive the loaded staging scene.");
                Assert.IsNotEmpty(callbackSceneName,
                    "The pre-serialization callback did not receive a named temporary scene.");

                Assert.IsTrue(behaviour == null,
                    "The authored Cilboxable behaviour should be replaced by a CilboxProxy.");
                CilboxProxy proxy = contentRoot.GetComponent<CilboxProxy>();
                Assert.IsNotNull(proxy, "Cilbox did not create a proxy for the generic Cilboxable behaviour.");
                Assert.AreSame(contentCilbox, proxy.box,
                    "The generated proxy must point to the Cilbox that belongs to the isolated content.");
                Assert.AreEqual("prepared", ReadSerializedStringField(proxy, nameof(BasisCilboxBuildTestBehaviour.Marker)),
                    "The pre-serialization callback ran too late; the proxy captured the pre-build field value.");
            }
            finally
            {
                if (handler != null)
                {
                    BasisCilboxBuildEvents.OnBeforeCilboxSerialize -= handler;
                }

                DestroyImmediateIfPresent(contentRoot);
                DestroyImmediateIfPresent(externalRoot);
                CloseEditorSceneIfLoaded(contentScene);
                CloseEditorSceneIfLoaded(externalScene);
                RestoreActiveScene(previousActiveScene);
            }
        }

        [Test]
        public void PrefabFinalization_ReplacesStaleContentAssemblyData()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene contentScene = default;
            GameObject contentRoot = null;
            try
            {
                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxStaleAssemblyRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                CilboxAvatarBasis contentCilbox = contentRoot.AddComponent<CilboxAvatarBasis>();
                contentCilbox.assemblyData = "stale-assembly";
                contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();

                BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(contentRoot, null);

                Assert.AreNotEqual("stale-assembly", contentCilbox.assemblyData,
                    "Fresh Cilbox conversion did not replace authored stale assembly data.");
                Assert.IsNotEmpty(contentCilbox.assemblyData);
                Assert.AreSame(contentCilbox, contentRoot.GetComponent<CilboxProxy>().box);
            }
            finally
            {
                DestroyImmediateIfPresent(contentRoot);
                CloseEditorSceneIfLoaded(contentScene);
                RestoreActiveScene(previousActiveScene);
            }
        }

        [Test]
        public void PrefabFinalization_MissingCilboxFailsBeforeFeaturePreparation()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene contentScene = default;
            GameObject contentRoot = null;
            Action<Scene> handler = null;
            try
            {
                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxMissingHostRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                BasisCilboxBuildTestBehaviour behaviour = contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();
                int sceneCountBefore = SceneManager.sceneCount;
                bool featurePreparationRan = false;
                handler = _ => featurePreparationRan = true;
                BasisCilboxBuildEvents.OnBeforeCilboxSerialize += handler;

                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(contentRoot, null));

                StringAssert.Contains("no Cilbox component", error.Message);
                Assert.IsFalse(featurePreparationRan,
                    "Feature preparation should not mutate content that cannot be safely Cilbox-converted.");
                Assert.AreEqual(contentScene, contentRoot.scene);
                Assert.AreEqual(sceneCountBefore, SceneManager.sceneCount,
                    "The failed finalization leaked its staging scene.");
                Assert.IsNotNull(behaviour,
                    "A failed conversion must leave the authored behaviour intact rather than partially converting it.");
                Assert.IsNull(contentRoot.GetComponent<CilboxProxy>());
            }
            finally
            {
                if (handler != null)
                {
                    BasisCilboxBuildEvents.OnBeforeCilboxSerialize -= handler;
                }
                DestroyImmediateIfPresent(contentRoot);
                CloseEditorSceneIfLoaded(contentScene);
                RestoreActiveScene(previousActiveScene);
            }
        }

        [Test]
        public void PreSerializationHandlers_AllRunThenAggregateFailuresAndCleanup()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene contentScene = default;
            GameObject contentRoot = null;
            Action<Scene> failingHandler = null;
            Action<Scene> succeedingHandler = null;
            try
            {
                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxSubscriberFailureRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                contentRoot.AddComponent<CilboxAvatarBasis>();
                BasisCilboxBuildTestBehaviour behaviour = contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();
                int sceneCountBefore = SceneManager.sceneCount;
                bool laterHandlerRan = false;

                failingHandler = _ => throw new InvalidOperationException("expected subscriber failure");
                succeedingHandler = _ => laterHandlerRan = true;
                BasisCilboxBuildEvents.OnBeforeCilboxSerialize += failingHandler;
                BasisCilboxBuildEvents.OnBeforeCilboxSerialize += succeedingHandler;

                AggregateException error = Assert.Throws<AggregateException>(
                    () => BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(contentRoot, null));

                Assert.IsTrue(laterHandlerRan,
                    "A failing feature preparer hid a later independent preparer.");
                Assert.AreEqual(1, error.InnerExceptions.Count);
                StringAssert.Contains(nameof(PreSerializationHandlers_AllRunThenAggregateFailuresAndCleanup),
                    error.InnerExceptions[0].ToString());
                Assert.AreEqual(contentScene, contentRoot.scene,
                    "Exception cleanup did not restore the content root to its scene.");
                Assert.AreEqual(sceneCountBefore, SceneManager.sceneCount,
                    "Exception cleanup leaked the temporary staging scene.");
                Assert.IsNotNull(behaviour,
                    "Cilbox conversion should not begin after feature preparation has failed.");
                Assert.IsNull(contentRoot.GetComponent<CilboxProxy>());
            }
            finally
            {
                if (failingHandler != null)
                {
                    BasisCilboxBuildEvents.OnBeforeCilboxSerialize -= failingHandler;
                }
                if (succeedingHandler != null)
                {
                    BasisCilboxBuildEvents.OnBeforeCilboxSerialize -= succeedingHandler;
                }
                DestroyImmediateIfPresent(contentRoot);
                CloseEditorSceneIfLoaded(contentScene);
                RestoreActiveScene(previousActiveScene);
            }
        }

        [Test]
        public void PrefabFinalization_RebindsAlreadyConvertedProxyToContentCilbox()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene externalScene = default;
            Scene contentScene = default;
            GameObject externalRoot = null;
            GameObject contentRoot = null;
            Action<Scene> handler = null;
            try
            {
                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxPreconvertedRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                CilboxAvatarBasis contentCilbox = contentRoot.AddComponent<CilboxAvatarBasis>();
                contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();
                BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(contentRoot, null);

                CilboxProxy proxy = contentRoot.GetComponent<CilboxProxy>();
                string generatedAssembly = contentCilbox.assemblyData;
                Assert.IsNotEmpty(generatedAssembly);

                externalScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                externalRoot = new GameObject("CilboxProxySourceRoot");
                SceneManager.MoveGameObjectToScene(externalRoot, externalScene);
                CilboxAvatarBasis externalCilbox = externalRoot.AddComponent<CilboxAvatarBasis>();
                externalCilbox.assemblyData = generatedAssembly;

                proxy.box = externalCilbox;
                contentCilbox.assemblyData = string.Empty;
                contentCilbox.ForceReinit();
                bool preparationRanAgain = false;
                handler = _ => preparationRanAgain = true;
                BasisCilboxBuildEvents.OnBeforeCilboxSerialize += handler;

                BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(contentRoot, null);

                Assert.IsFalse(preparationRanAgain,
                    "An already-converted clone should preserve its serialized program rather than rerunning feature baking.");
                Assert.AreSame(contentCilbox, proxy.box,
                    "The cloned proxy remained dependent on a Cilbox outside its content hierarchy.");
                Assert.AreEqual(generatedAssembly, contentCilbox.assemblyData,
                    "The cloned content Cilbox did not inherit the serialized program backing its proxies.");
                Assert.AreEqual(generatedAssembly, externalCilbox.assemblyData,
                    "Rebinding a clone must not mutate the source/foreign Cilbox.");
            }
            finally
            {
                if (handler != null)
                {
                    BasisCilboxBuildEvents.OnBeforeCilboxSerialize -= handler;
                }
                DestroyImmediateIfPresent(contentRoot);
                DestroyImmediateIfPresent(externalRoot);
                CloseEditorSceneIfLoaded(contentScene);
                CloseEditorSceneIfLoaded(externalScene);
                RestoreActiveScene(previousActiveScene);
            }
        }

        [Test]
        public void SceneProcessor_IsOrderedBeforeCilboxAndSkipsUnrelatedScenes()
        {
            Type basisProcessorType = typeof(BasisCilboxBuildHook).Assembly.GetType(
                "BasisCilboxPreSerializeSceneProcessor", throwOnError: true);
            Type cilboxProcessorType = typeof(Cilbox.Cilbox).Assembly.GetType(
                "Cilbox.CilboxCustomBuildProcessor", throwOnError: true);
            IProcessSceneWithReport basisProcessor = (IProcessSceneWithReport)Activator.CreateInstance(
                basisProcessorType, nonPublic: true);
            IProcessSceneWithReport cilboxProcessor = (IProcessSceneWithReport)Activator.CreateInstance(
                cilboxProcessorType, nonPublic: true);

            Assert.Less(basisProcessor.callbackOrder, cilboxProcessor.callbackOrder,
                "Basis feature preparation must run before Cilbox's order-0 conversion stage.");

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene scene = default;
            GameObject root = null;
            Action<Scene> handler = null;
            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                root = new GameObject("CilboxSceneProcessorFilterRoot");
                SceneManager.MoveGameObjectToScene(root, scene);
                int callbacks = 0;
                handler = _ => callbacks++;
                BasisCilboxBuildEvents.OnBeforeCilboxSerialize += handler;

                basisProcessor.OnProcessScene(scene, null);
                Assert.AreEqual(0, callbacks,
                    "The Basis scene processor invoked feature preparers for a scene with no Cilboxable content.");

                root.AddComponent<CilboxAvatarBasis>();
                root.AddComponent<BasisCilboxBuildTestBehaviour>();
                basisProcessor.OnProcessScene(scene, null);
                Assert.AreEqual(1, callbacks);
            }
            finally
            {
                if (handler != null)
                {
                    BasisCilboxBuildEvents.OnBeforeCilboxSerialize -= handler;
                }
                DestroyImmediateIfPresent(root);
                CloseEditorSceneIfLoaded(scene);
                RestoreActiveScene(previousActiveScene);
            }
        }

        [Test]
        public void TestInEditorStages_KeepStructuralWorkInactiveButPreserveLegacyActiveContract()
        {
            EnsureBuildHookRegistered();

            GameObject clone = new GameObject("TestInEditorStageContract");
            clone.SetActive(false);
            bool inactivePreparationSawInactive = false;
            bool finalizationSawInactive = false;
            bool legacySawActive = false;
            BasisAvatarSDKInspector.BeforeTestInEditorHandler prepare = go => inactivePreparationSawInactive = !go.activeSelf;
            BasisAvatarSDKInspector.BeforeTestInEditorHandler finalize = go => finalizationSawInactive = !go.activeSelf;
            BasisAvatarSDKInspector.BeforeTestInEditorHandler legacy = go => legacySawActive = go.activeSelf;

            try
            {
                BasisAvatarSDKInspector.OnBeforeTestInEditorPrepareInactive += prepare;
                BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize += finalize;
                BasisAvatarSDKInspector.OnBeforeTestInEditor += legacy;

                InvokePrivateStatic(typeof(BasisAvatarSDKInspector), "ProcessTestInEditorClone", clone);

                Assert.IsTrue(inactivePreparationSawInactive);
                Assert.IsTrue(finalizationSawInactive);
                Assert.IsTrue(legacySawActive,
                    "The legacy Test In Editor hook no longer receives an active clone as it did before this branch.");
                Assert.IsTrue(clone.activeSelf,
                    "Test In Editor must force the prepared clone active before loading it.");
            }
            finally
            {
                BasisAvatarSDKInspector.OnBeforeTestInEditorPrepareInactive -= prepare;
                BasisAvatarSDKInspector.OnBeforeTestInEditorFinalize -= finalize;
                BasisAvatarSDKInspector.OnBeforeTestInEditor -= legacy;
                DestroyImmediateIfPresent(clone);
            }
        }

        [Test]
        public void PrefabFinalization_WorksWithoutPreSerializationSubscribers()
        {
            EnsureBuildHookRegistered();

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene contentScene = default;
            GameObject contentRoot = null;

            try
            {
                contentScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                contentRoot = new GameObject("CilboxNoSubscriberRoot");
                SceneManager.MoveGameObjectToScene(contentRoot, contentScene);
                CilboxAvatarBasis contentCilbox = contentRoot.AddComponent<CilboxAvatarBasis>();
                contentRoot.AddComponent<BasisCilboxBuildTestBehaviour>();

                BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(contentRoot, null);

                CilboxProxy proxy = contentRoot.GetComponent<CilboxProxy>();
                Assert.IsNotNull(proxy, "Generic Cilbox conversion should not require an event subscriber.");
                Assert.AreSame(contentCilbox, proxy.box);
            }
            finally
            {
                DestroyImmediateIfPresent(contentRoot);
                CloseEditorSceneIfLoaded(contentScene);
                RestoreActiveScene(previousActiveScene);
            }
        }

        [UnityTest]
        public IEnumerator TestInEditor_CilboxTypeFailureDestroysPreparedCloneAndRestoresSource()
        {
            yield return new EnterPlayMode();

            EnsureBuildHookRegistered();
            Scene contentScene = SceneManager.CreateScene("BasisCilboxTypeFailureCleanupTest");
            GameObject sourceRoot = new GameObject("CilboxTypeFailureSource");
            SceneManager.MoveGameObjectToScene(sourceRoot, contentScene);
            BasisAvatar avatar = sourceRoot.AddComponent<BasisAvatar>();
            sourceRoot.AddComponent<CilboxAvatarBasis>();
            sourceRoot.AddComponent<BasisCilboxBuildTypeFailureBehaviour>();

            int rootCountBeforeLoad = contentScene.rootCount;
            int sceneCountBeforeLoad = SceneManager.sceneCount;
            LogAssert.Expect(LogType.Error, new Regex(
                "Test In Editor failed while preparing the avatar clone:.*CilboxException",
                RegexOptions.Singleline));

            InvokePrivateStatic(typeof(BasisAvatarSDKInspector), "LoadAvatar", avatar);

            Assert.IsTrue(sourceRoot.activeSelf,
                "A failed Test In Editor conversion must restore the authored avatar immediately.");

            // LoadAvatar uses Destroy in Play Mode and the Cilbox staging scene unload is asynchronous.
            // Give both cleanup paths enough frames to remove their temporary objects/scenes.
            yield return null;
            yield return null;

            Assert.AreEqual(rootCountBeforeLoad, contentScene.rootCount,
                "A failed Cilbox type check left a cloned Test In Editor object in the authored scene.");
            Assert.AreEqual(sceneCountBeforeLoad, SceneManager.sceneCount,
                "A failed Cilbox type check leaked its temporary staging scene.");
            Assert.AreSame(sourceRoot, contentScene.GetRootGameObjects().Single(),
                "The authored scene contains an unexpected root after failed Test In Editor preparation.");
            Assert.IsTrue(sourceRoot.activeSelf,
                "The authored avatar remained disabled after failed Test In Editor preparation.");

            UnityEngine.Object.Destroy(sourceRoot);
            yield return null;
            AsyncOperation unload = SceneManager.UnloadSceneAsync(contentScene);
            if (unload != null)
            {
                yield return unload;
            }

            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator PrefabFinalization_PlayModeUsesRuntimeStagingSceneAndKeepsNativeCloneInert()
        {
            yield return new EnterPlayMode();

            EnsureBuildHookRegistered();
            Scene contentScene = SceneManager.CreateScene("BasisCilboxPlayModeTest");
            GameObject sourceRoot = new GameObject("CilboxPlayModeSource");
            SceneManager.MoveGameObjectToScene(sourceRoot, contentScene);
            sourceRoot.AddComponent<CilboxAvatarBasis>();
            sourceRoot.AddComponent<BasisCilboxBuildTestBehaviour>();
            BasisCilboxBuildTestBehaviour.NativeAwakeCount = 0;

            GameObject clone = (GameObject)InvokePrivateStatic(
                typeof(BasisAvatarSDKInspector), "InstantiateInactiveClone", sourceRoot);
            Assert.IsFalse(clone.activeSelf,
                "The Test In Editor clone was activated before structural/finalization hooks could replace authoring scripts.");
            Assert.AreEqual(0, BasisCilboxBuildTestBehaviour.NativeAwakeCount,
                "Cloning an active source executed the native Cilboxable Awake before conversion.");

            int sceneCountBeforeFinalization = SceneManager.sceneCount;
            BasisAssetBundlePipeline.OnBeforeBuildPrefabSerialization.Invoke(clone, null);
            Assert.AreEqual(contentScene, clone.scene);
            Assert.IsNull(clone.GetComponent<BasisCilboxBuildTestBehaviour>());
            Assert.IsNotNull(clone.GetComponent<CilboxProxy>());
            Assert.AreEqual(0, BasisCilboxBuildTestBehaviour.NativeAwakeCount,
                "Native authoring code ran while the play-mode clone was being converted.");

            // Runtime scene unload is asynchronous; give it frames to complete.
            yield return null;
            yield return null;
            Assert.AreEqual(sceneCountBeforeFinalization, SceneManager.sceneCount,
                "The play-mode Cilbox staging scene did not unload asynchronously.");

            UnityEngine.Object.Destroy(clone);
            UnityEngine.Object.Destroy(sourceRoot);
            yield return null;
            AsyncOperation unload = SceneManager.UnloadSceneAsync(contentScene);
            if (unload != null)
            {
                yield return unload;
            }

            yield return new ExitPlayMode();
        }

        private static void EnsureBuildHookRegistered()
        {
            InvokePrivateStatic(typeof(BasisCilboxBuildHook), "Initialize");
        }

        private static object InvokePrivateStatic(Type type, string methodName, params object[] args)
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, $"Could not find {type.FullName}.{methodName} for regression validation.");
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static string ReadSerializedStringField(CilboxProxy proxy, string fieldName)
        {
            byte[] bytes = Convert.FromBase64String(proxy.serializedObjectData);
            Serializee[] fields = new Serializee(bytes, Serializee.ElementType.List).AsArray();
            foreach (Serializee field in fields)
            {
                Dictionary<string, Serializee> map = field.AsMap();
                if (map.TryGetValue("n", out Serializee name) && name.AsString() == fieldName)
                {
                    return map["d"].AsString();
                }
            }

            Assert.Fail($"Serialized proxy data did not contain field '{fieldName}'. Fields: "
                + string.Join(", ", fields.Select(field => field.AsMap()["n"].AsString())));
            return null;
        }

        private static void DestroyImmediateIfPresent(GameObject gameObject)
        {
            if (gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static void CloseEditorSceneIfLoaded(Scene scene)
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void RestoreActiveScene(Scene scene)
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                SceneManager.SetActiveScene(scene);
            }
        }
    }
}
