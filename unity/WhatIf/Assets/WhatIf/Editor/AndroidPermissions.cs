using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace WhatIf.EditorTools
{
    /// <summary>
    /// Adds the permissions What If asks for at start to the generated Android manifest. Android
    /// only shows a permission dialog for permissions the manifest declares: without
    /// SPATIAL_MAPPING the headset delivers no planes or meshes, without EYE_TRACKING no gaze.
    /// (Unity adds CAMERA and RECORD_AUDIO itself when the code uses them; listed for safety.)
    /// </summary>
    public class AndroidPermissions : IPostGenerateGradleAndroidProject
    {
        static readonly string[] Permissions =
        {
            "com.magicleap.permission.SPATIAL_MAPPING",
            "com.magicleap.permission.EYE_TRACKING",
            "com.magicleap.permission.DEPTH_CAMERA",
            "com.magicleap.permission.HAND_TRACKING",
            "android.permission.CAMERA",
            "android.permission.RECORD_AUDIO",
            "android.permission.INTERNET",
        };

        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 100;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(file)) { Debug.LogWarning("[WhatIf] manifest not found: " + file); return; }
            var doc = new XmlDocument();
            doc.Load(file);
            var manifest = doc.DocumentElement;
            int added = 0;
            foreach (var p in Permissions)
            {
                bool present = false;
                foreach (XmlElement e in manifest.GetElementsByTagName("uses-permission"))
                    if (e.GetAttribute("name", AndroidNs) == p) { present = true; break; }
                if (present) continue;
                var el = doc.CreateElement("uses-permission");
                el.SetAttribute("name", AndroidNs, p);
                manifest.PrependChild(el);
                added++;
            }
            doc.Save(file);
            Debug.Log($"[WhatIf] Android manifest: {added} permission(s) added");
        }
    }
}
