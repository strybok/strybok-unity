#if UNITY_ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace Strybok.Editor
{
    /// <summary>
    /// After Unity generated the Gradle project, adds the App Links intent filter for
    /// every link host of <see cref="StrybokSettings"/> to the launcher activity. The
    /// project's own <c>Assets/Plugins/Android/AndroidManifest.xml</c> is never touched.
    /// </summary>
    internal sealed class StrybokAndroidPostprocessor : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder
        {
            get { return 900; }
        }

        /// <param name="path">Path of the <c>unityLibrary</c> module, which declares the activity.</param>
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            StrybokSettings settings = StrybokSettings.Load();
            if (settings == null || !settings.AddAndroidIntentFilter) return;

            List<string> hosts = settings.GetNormalizedHosts();
            if (hosts.Count == 0)
            {
                Debug.LogWarning("[Strybok] No link hosts in StrybokSettings: the Android intent filter was not added.");
                return;
            }

            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[Strybok] Android manifest not found at " + manifestPath + ": add the intent filter manually.");
                return;
            }

            try
            {
                int added;
                int launchers;
                string patched = AndroidManifestPatcher.Patch(File.ReadAllText(manifestPath), hosts, out added, out launchers);
                if (launchers == 0)
                {
                    Debug.LogWarning("[Strybok] No launcher activity in " + manifestPath + ": add the intent filter manually.");
                    return;
                }
                if (added > 0) File.WriteAllText(manifestPath, patched);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Strybok] Could not add the Android intent filter (" + error.Message + "): add it manually.");
            }
        }
    }
}
#endif
