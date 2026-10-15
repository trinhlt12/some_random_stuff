using System.IO;
using UnityEditor;

namespace DayNightSky.Editor
{
    /// <summary>
    /// Asset-database root of the weather system; every sample/generated asset lives under it.
    /// Found from the editor asmdef's GUID, so it works both as a package (Packages/...) and when the
    /// folder was imported from a .unitypackage or copied anywhere under Assets/.
    /// </summary>
    public static class PackagePaths
    {
        public const string PackageName = "com.trinhlt.daynight-weather";
        const string EditorAsmdefGuid = "49adb3b31387df5489a8fecb8b3c9587";

        public static readonly string Root = FindRoot();

        static string FindRoot()
        {
            string asmdef = AssetDatabase.GUIDToAssetPath(EditorAsmdefGuid);
            if (string.IsNullOrEmpty(asmdef)) return "Packages/" + PackageName;
            // <root>/Editor/DayNightSky.Editor.asmdef
            return Path.GetDirectoryName(Path.GetDirectoryName(asmdef)).Replace('\\', '/');
        }
    }
}
