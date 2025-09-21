/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor;
using System.IO;
using System.Diagnostics;

public class OVRDumpBuildInfo : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    public int callbackOrder => 0;


    public void OnPreprocessBuild(BuildReport report)
    {

        PrepareRuntimeActionBindings();
    }

    public static void PrepareRuntimeActionBindings()
    {
        // Save to streaming assets dir.
        Meta.XR.InputActions.RuntimeSettings.UpdateBindingsOnDisk();
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        // Copy path from streaming assets folder to root directory.
        // We don't do this on android since on android we can get the apk path & access them that way,
        // but on windows there's no central owner to provide access to the data path.

        var pcPath = report.summary.platformGroup == BuildTargetGroup.Standalone ? report.summary.outputPath
                                                                                 : null;

        // Clean up jsons since we generated them in OnPreprocessBuild,
        // and they have no reason to be persisted in the project.

        Meta.XR.InputActions.RuntimeSettings.UpdateBindingsOnDisk(clean: true, buildPath: pcPath);

        // (Allow any exceptions above to bubble up so builds fail and buildmasters get a clear signal.)
        // ((Reason: If an app depends on these input bindings, then it's a broken build without them.))
    }
}
