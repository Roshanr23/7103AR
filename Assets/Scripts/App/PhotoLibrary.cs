using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AR7103.App
{
    /// <summary>
    /// Save a PNG to the photo library (Plugins/iOS/AR7103Photos.mm). The result
    /// arrives as a UnitySendMessage to <c>gameObject.method(string)</c>: "ok",
    /// "denied" or "error". Elsewhere (the Editor) the PNG is written to
    /// persistentDataPath/Photos and the reply is "ok".
    /// </summary>
    public static class PhotoLibrary
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void AR7103_SaveToPhotos(byte[] bytes, int length, string gameObject, string method);
#endif

        public static void Save(byte[] png, GameObject replyTo, string method)
        {
#if UNITY_IOS && !UNITY_EDITOR
            AR7103_SaveToPhotos(png, png.Length, replyTo.name, method);
#else
            string dir = Path.Combine(Application.persistentDataPath, "Photos");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"WinterWild_{System.DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, png);
            Debug.Log("[Photo] saved " + path);
            replyTo.SendMessage(method, "ok", SendMessageOptions.DontRequireReceiver);
#endif
        }
    }
}
