using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    // 프리셋·카탈로그를 두는 공용 폴더. 프로젝트 밖에 있어 같은 PC의 모든 프로젝트가 같은 데이터를 쓴다.
    // 경로는 사용자 단위(EditorPrefs)로 저장하고, 고른 적이 없으면 <내 문서>/PackageSetupTool 이다.
    // 파일 입출력 예외(권한·드라이브 없음)는 정상 흐름에서 생길 수 있어 사유로 돌려준다. 성공하면 null 이다.
    public static class SharedDataFolder
    {
        private static string DefaultPath => ToUnixPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SHARED_FOLDER_NAME));

        public static string FolderPath => EditorPrefs.GetString(SHARED_FOLDER_PREF_KEY, DefaultPath);

        public static string PresetFolderPath => GetPresetFolderPath(FolderPath);

        public static string CatalogFilePath => GetCatalogFilePath(FolderPath);

        public static void SetFolderPath(string folderPath)
        {
            EditorPrefs.SetString(SHARED_FOLDER_PREF_KEY, ToUnixPath(Path.GetFullPath(folderPath)));
        }

        public static bool IsSameFolder(string a, string b)
        {
            // Windows·macOS 기본 파일 시스템은 대소문자를 구분하지 않는다.
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        // 프리셋 파일이나 카탈로그 파일이 하나라도 있으면 도구 데이터가 있는 폴더다.
        public static bool HasToolData(string folderPath)
        {
            string presetFolderPath = GetPresetFolderPath(folderPath);
            return File.Exists(GetCatalogFilePath(folderPath))
                   || (Directory.Exists(presetFolderPath) && Directory.GetFiles(presetFolderPath, $"*{PRESET_FILE_EXTENSION}").Length > 0);
        }

        public static LocalizedText EnsureCreated()
        {
            try
            {
                Directory.CreateDirectory(PresetFolderPath);
            }
            catch (IOException e)
            {
                return new LocalizedText("data.folderUnavailable", FolderPath, e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                return new LocalizedText("data.folderUnavailable", FolderPath, e.Message);
            }
            return null;
        }

        // 다른 에디터가 파일을 바꿨는지 비교하는 값. 데이터 파일마다 이름·크기·수정 시각을 이어 붙인다.
        public static string GetModificationStamp()
        {
            var stamp = new StringBuilder();
            try
            {
                AppendStamp(stamp, CatalogFilePath);
                if (Directory.Exists(PresetFolderPath))
                {
                    foreach (string path in Directory.GetFiles(PresetFolderPath, $"*{PRESET_FILE_EXTENSION}").OrderBy(x => x, StringComparer.Ordinal))
                    {
                        AppendStamp(stamp, path);
                    }
                }
            }
            catch (IOException e)
            {
                // 폴더를 읽을 수 없는 상태도 하나의 값으로 보아, 다시 읽을 수 있게 되면 바뀐 것으로 잡는다.
                return e.Message;
            }
            catch (UnauthorizedAccessException e)
            {
                return e.Message;
            }
            return stamp.ToString();
        }

        // 프리셋 파일과 카탈로그 파일만 복사한다. 대상 폴더에 도구 데이터가 없을 때 쓰므로 덮어쓰지 않는다.
        public static LocalizedText CopyData(string fromFolderPath, string toFolderPath)
        {
            try
            {
                string toPresetFolderPath = GetPresetFolderPath(toFolderPath);
                Directory.CreateDirectory(toPresetFolderPath);
                string fromPresetFolderPath = GetPresetFolderPath(fromFolderPath);
                if (Directory.Exists(fromPresetFolderPath))
                {
                    foreach (string path in Directory.GetFiles(fromPresetFolderPath, $"*{PRESET_FILE_EXTENSION}"))
                    {
                        File.Copy(path, Path.Combine(toPresetFolderPath, Path.GetFileName(path)), false);
                    }
                }
                string fromCatalogPath = GetCatalogFilePath(fromFolderPath);
                if (File.Exists(fromCatalogPath))
                {
                    File.Copy(fromCatalogPath, GetCatalogFilePath(toFolderPath), false);
                }
            }
            catch (IOException e)
            {
                return new RawText(e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                return new RawText(e.Message);
            }
            return null;
        }

        public static LocalizedText Open()
        {
            LocalizedText error = EnsureCreated();
            if (error != null)
            {
                return error;
            }
            EditorUtility.OpenWithDefaultApp(FolderPath);
            return null;
        }

        private static string GetPresetFolderPath(string folderPath) => $"{folderPath}/{PRESET_FOLDER}";

        private static string GetCatalogFilePath(string folderPath) => $"{folderPath}/{CATALOG_FILE_NAME}";

        private static void AppendStamp(StringBuilder stamp, string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return;
            }
            stamp.Append(file.Name).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
        }

        private static string ToUnixPath(string path) => path.Replace('\\', '/');
    }
}
