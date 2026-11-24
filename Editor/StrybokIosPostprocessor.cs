#if UNITY_IOS
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace Strybok.Editor
{
    /// <summary>
    /// After Unity generated the Xcode project, adds <c>applinks:&lt;host&gt;</c> for every
    /// link host of <see cref="StrybokSettings"/> to the Associated Domains entitlement
    /// of the app target. Domains that are already there (from other plugins) are kept.
    /// </summary>
    internal static class StrybokIosPostprocessor
    {
        private const string AssociatedDomainsKey = "com.apple.developer.associated-domains";
        private const string DefaultEntitlementsFile = "Unity-iPhone.entitlements";

        [PostProcessBuild(900)]
        public static void OnPostProcessBuild(BuildTarget target, string buildPath)
        {
            if (target != BuildTarget.iOS) return;
            StrybokSettings settings = StrybokSettings.Load();
            if (settings == null || !settings.AddIosAssociatedDomains) return;

            List<string> hosts = settings.GetNormalizedHosts();
            if (hosts.Count == 0)
            {
                Debug.LogWarning("[Strybok] No link hosts in StrybokSettings: Associated Domains were not added.");
                return;
            }

            try
            {
                string projectPath = PBXProject.GetPBXProjectPath(buildPath);
                var project = new PBXProject();
                project.ReadFromFile(projectPath);
                string targetGuid = project.GetUnityMainTargetGuid();

                // Reuse the entitlements file another post-processor may have set up.
                string entitlementsFile = project.GetBuildPropertyForAnyConfig(targetGuid, "CODE_SIGN_ENTITLEMENTS");
                if (string.IsNullOrEmpty(entitlementsFile)) entitlementsFile = DefaultEntitlementsFile;

                var domains = new List<string>();
                string entitlementsPath = Path.Combine(buildPath, entitlementsFile);
                if (File.Exists(entitlementsPath))
                {
                    var entitlements = new PlistDocument();
                    entitlements.ReadFromFile(entitlementsPath);
                    PlistElement existing = entitlements.root[AssociatedDomainsKey];
                    if (existing != null)
                    {
                        foreach (PlistElement value in existing.AsArray().values)
                        {
                            string domain = value.AsString();
                            if (!domains.Contains(domain)) domains.Add(domain);
                        }
                    }
                }
                foreach (string host in hosts)
                {
                    string domain = "applinks:" + host;
                    if (!domains.Contains(domain)) domains.Add(domain);
                }

                var capabilities = new ProjectCapabilityManager(projectPath, entitlementsFile, null, targetGuid);
                capabilities.AddAssociatedDomains(domains.ToArray());
                capabilities.WriteToFile();
            }
            catch (Exception error)
            {
                Debug.LogWarning(
                    "[Strybok] Could not add Associated Domains (" + error.Message +
                    "): add the capability in Xcode manually.");
            }
        }
    }
}
#endif
