using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.UIElements;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    public static class ResourceLoader
    {
        // 도구 폴더를 어느 경로에 복사해도 동작하도록 asmdef 위치(<루트>/Editor/)에서 루트를 구한다.
        public static string ToolRootPath
        {
            get
            {
                string asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(ASSEMBLY_NAME);
                string editorFolder = Path.GetDirectoryName(asmdefPath);
                return Path.GetDirectoryName(editorFolder).Replace('\\', '/');
            }
        }

        public static VisualElement GetUXML(string fileName)
        {
            string path = $"{ToolRootPath}/{UXML_FOLDER}/{fileName}";
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            if (visualTree == null)
            {
                throw new FileNotFoundException(LocalizationManager.Get("error.uxmlNotFound", path));
            }
            return visualTree.Instantiate();
        }
    }
}
