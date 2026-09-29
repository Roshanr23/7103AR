#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;
using System.IO;

namespace AR7103.Hands.EditorTools
{
    /// <summary>
    /// Links Vision.framework into the generated Xcode project.
    ///
    /// Unity copies Assets/Plugins/iOS/*.mm into the Xcode project and compiles
    /// them, but it does NOT infer which system frameworks those files need.
    /// Without this, HandPoseVision.mm compiles and then fails at link time with
    /// undefined symbols for VNDetectHumanHandPoseRequest.
    ///
    /// Also sets the camera usage description, which iOS requires before it will
    /// hand any app the camera at all.
    /// </summary>
    public static class HandPoseBuildPostProcess
    {
        const string CameraUsage =
            "This app uses the camera to show AR content and to recognise hand gestures.";

        [PostProcessBuild(100)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            string projPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);

            // Vision lives in the UnityFramework target on modern Unity, but add
            // it to both so this keeps working if that layout changes.
            string unityFramework = proj.GetUnityFrameworkTargetGuid();
            string mainTarget = proj.GetUnityMainTargetGuid();

            foreach (string guid in new[] { unityFramework, mainTarget })
            {
                if (string.IsNullOrEmpty(guid)) continue;
                proj.AddFrameworkToProject(guid, "Vision.framework", weak: false);
                proj.AddFrameworkToProject(guid, "CoreGraphics.framework", weak: false);
                proj.AddFrameworkToProject(guid, "CoreVideo.framework", weak: false);
            }

            proj.WriteToFile(projPath);
            Debug.Log("[HandPose] Linked Vision / CoreGraphics / CoreVideo into Xcode project.");

            // Camera permission string
            string plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
            if (File.Exists(plistPath))
            {
                var plist = new PlistDocument();
                plist.ReadFromFile(plistPath);
                if (plist.root["NSCameraUsageDescription"] == null)
                {
                    plist.root.SetString("NSCameraUsageDescription", CameraUsage);
                    plist.WriteToFile(plistPath);
                    Debug.Log("[HandPose] Added NSCameraUsageDescription to Info.plist.");
                }
            }
        }
    }
}
#endif
