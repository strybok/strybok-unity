using System.IO;
using UnityEditor;
using UnityEngine;

namespace Strybok.Editor
{
    /// <summary><c>Tools &gt; Strybok &gt; Settings</c>: creates and selects the settings asset.</summary>
    internal static class StrybokSettingsMenu
    {
        private const string ResourcesFolder = "Assets/Resources";

        [MenuItem("Tools/Strybok/Settings")]
        private static void OpenSettings()
        {
            StrybokSettings settings = StrybokSettings.Load();
            if (settings == null)
            {
                if (!AssetDatabase.IsValidFolder(ResourcesFolder)) Directory.CreateDirectory(ResourcesFolder);
                settings = ScriptableObject.CreateInstance<StrybokSettings>();
                AssetDatabase.CreateAsset(settings, ResourcesFolder + "/" + StrybokSettings.ResourceName + ".asset");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }
    }
}
