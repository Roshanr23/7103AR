#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>
/// Two Xcode build settings that newer Xcode versions default to YES and that Unity's
/// generated iOS project isn't yet compatible with. Unity regenerates the Xcode project on
/// every Build and Run, so both have to be turned back off after each export rather than
/// fixed once by hand in Xcode:
///
///  - ENABLE_USER_SCRIPT_SANDBOXING blocks IL2CPP's build-phase scripts from reading/writing
///    Builds/Il2CppOutputProject and Builds/Il2CppTempDirArtifacts
///    ("Sandbox: deny file-write/file-read" in Xcode's build log).
///  - ENABLE_MODULE_VERIFIER runs an extra check that UnityFramework's generated headers
///    fail ("could not build module 'Test'", umbrella header / RedefinePlatforms.h errors).
/// </summary>
public static class DisableScriptSandboxing
{
    [PostProcessBuild(1)]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS) return;

        string projPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);

        foreach (var guid in new[] { proj.GetUnityMainTargetGuid(), proj.GetUnityFrameworkTargetGuid() })
        {
            proj.SetBuildProperty(guid, "ENABLE_USER_SCRIPT_SANDBOXING", "NO");
            proj.SetBuildProperty(guid, "ENABLE_MODULE_VERIFIER", "NO");
        }

        proj.WriteToFile(projPath);
    }
}
#endif
