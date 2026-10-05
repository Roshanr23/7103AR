#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace AR7103.EditorTools
{
    /// <summary>
    /// Info.plist and framework entries the native plugins need, applied to every
    /// iOS build: permission text for saving photos (AR7103Photos.mm), and the
    /// Photos framework it links against.
    /// </summary>
    public static class IOSPostBuild
    {
        [PostProcessBuild(100)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;

            string plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetString("NSPhotoLibraryAddUsageDescription",
                "Save the photos you take of the animals to your library.");
            plist.WriteToFile(plistPath);

            string projPath = PBXProject.GetPBXProjectPath(path);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);
            string fw = proj.GetUnityFrameworkTargetGuid();
            proj.AddFrameworkToProject(fw, "Photos.framework", false);
            proj.WriteToFile(projPath);
        }
    }
}
#endif
