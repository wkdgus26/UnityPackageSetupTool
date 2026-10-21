using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    // 공용 폴더의 프리셋(<공용 폴더>/Presets/<이름>.json) 읽기·생성·복제·이름 변경·삭제·변경 저장. 실패하면 사유, 성공하면 null 을 돌려준다.
    // 다른 에디터도 같은 파일을 고칠 수 있어, 변경은 항상 파일 최신본을 읽어 그 위에 적용한 뒤 쓴다.
    // 읽을 수 없는 파일은 목록에서 빼되 덮어쓰거나 지우지 않는다.
    public static class PresetStore
    {
        // 읽을 수 있는 프리셋이 없으면 Default 를 만든다. 다만 Default 파일이 읽을 수 없는 상태로 있으면 덮어쓰지 않도록 만들지 않는다.
        // 읽지 못한 파일·폴더의 사유는 errors 에 더한다.
        public static List<PackagePresetData> LoadAllOrCreateDefault(List<LocalizedText> errors)
        {
            LocalizedText folderError = SharedDataFolder.EnsureCreated();
            if (folderError != null)
            {
                errors.Add(folderError);
                return new List<PackagePresetData>();
            }
            var presets = LoadAll(errors);
            if (presets.Count > 0 || FindPath(DEFAULT_PRESET_NAME) != null)
            {
                return presets;
            }
            LocalizedText createError = Create(DEFAULT_PRESET_NAME);
            if (createError != null)
            {
                errors.Add(createError);
                return presets;
            }
            presets.Add(new PackagePresetData { name = DEFAULT_PRESET_NAME });
            return presets;
        }

        // 사용할 수 없는 이름이면 사유를, 사용할 수 있으면 null 을 돌려준다.
        public static LocalizedText ValidateName(string presetName, PackagePresetData renameTarget)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return new LocalizedText("preset.nameRequired");
            }
            if (presetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return new LocalizedText("preset.invalidNameChars", presetName);
            }
            string path = FindPath(presetName);
            if (path == null)
            {
                return null;
            }
            // 대소문자만 바꾸는 이름 변경
            if (renameTarget != null && IsSameName(Path.GetFileNameWithoutExtension(path), renameTarget.name))
            {
                return null;
            }
            return PresetJsonSerializer.ReadPreset(path, out _) == null
                ? new LocalizedText("preset.duplicateName", presetName.Trim())
                : new LocalizedText("preset.nameTakenByUnreadable", Path.GetFileName(path));
        }

        public static LocalizedText Create(string presetName)
        {
            presetName = presetName.Trim();
            // 이름을 검사한 뒤 다른 에디터가 같은 이름을 만들었을 수 있다.
            if (FindPath(presetName) != null)
            {
                return new LocalizedText("preset.duplicateName", presetName);
            }
            return Write(GetPath(presetName), new PackagePresetData());
        }

        // 다른 에디터가 원본을 바꿨을 수 있어 원본 파일의 최신본을 새 이름으로 쓴다.
        public static LocalizedText Duplicate(string sourceName, string newName)
        {
            newName = newName.Trim();
            if (FindPath(newName) != null)
            {
                return new LocalizedText("preset.duplicateName", newName);
            }
            LocalizedText error = Load(sourceName, out var preset, out _);
            if (error != null)
            {
                return error;
            }
            return Write(GetPath(newName), preset);
        }

        public static LocalizedText Rename(string presetName, string newName)
        {
            newName = newName.Trim();
            string path = FindPath(presetName);
            if (path == null)
            {
                return new LocalizedText("data.changedElsewhere", presetName);
            }
            string existingPath = FindPath(newName);
            if (existingPath != null && existingPath != path)
            {
                return new LocalizedText("preset.duplicateName", newName);
            }
            try
            {
                File.Move(path, GetPath(newName));
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

        // 휴지통으로 보내 복구할 수 있게 한다.
        public static LocalizedText Delete(string presetName)
        {
            string path = FindPath(presetName);
            return path == null ? new LocalizedText("data.changedElsewhere", presetName) : FileTrash.MoveToTrash(path);
        }

        // 프리셋 하나의 파일 최신본을 읽는다.
        public static LocalizedText Load(string presetName, out PackagePresetData preset)
        {
            return Load(presetName, out preset, out _);
        }

        // 파일 최신본에 change 를 적용해 저장한다. 다른 곳에서 지워졌으면 다시 만들지 않는다.
        public static LocalizedText Update(string presetName, Action<PackagePresetData> change)
        {
            LocalizedText error = Load(presetName, out var preset, out string path);
            if (error != null)
            {
                return error;
            }
            change(preset);
            return Write(path, preset);
        }

        private static LocalizedText Load(string presetName, out PackagePresetData preset, out string path)
        {
            preset = null;
            path = FindPath(presetName);
            if (path == null)
            {
                return new LocalizedText("data.changedElsewhere", presetName);
            }
            LocalizedText error = PresetJsonSerializer.ReadPreset(path, out preset);
            return error == null ? null : new LocalizedText("data.unreadableFile", path, error);
        }

        private static List<PackagePresetData> LoadAll(List<LocalizedText> errors)
        {
            var presets = new List<PackagePresetData>();
            foreach (string path in GetPresetFilePaths())
            {
                LocalizedText error = PresetJsonSerializer.ReadPreset(path, out var preset);
                if (error != null)
                {
                    errors.Add(new LocalizedText("data.unreadableFile", path, error));
                    continue;
                }
                presets.Add(preset);
            }
            return presets.OrderBy(x => x.name).ToList();
        }

        private static LocalizedText Write(string path, PackagePresetData preset)
        {
            LocalizedText error = PresetJsonSerializer.WriteFile(path, preset);
            return error == null ? null : new LocalizedText("data.saveFailed", path, error);
        }

        // 같은 이름(대소문자 무시)의 프리셋 파일 경로. 읽을 수 없는 파일도 포함한다. 없으면 null
        private static string FindPath(string presetName)
        {
            return GetPresetFilePaths().FirstOrDefault(x => IsSameName(Path.GetFileNameWithoutExtension(x), presetName.Trim()));
        }

        private static string[] GetPresetFilePaths()
        {
            string folderPath = SharedDataFolder.PresetFolderPath;
            return Directory.Exists(folderPath)
                ? Directory.GetFiles(folderPath, $"*{PRESET_FILE_EXTENSION}").Select(x => x.Replace('\\', '/')).ToArray()
                : Array.Empty<string>();
        }

        private static string GetPath(string presetName) => $"{SharedDataFolder.PresetFolderPath}/{presetName}{PRESET_FILE_EXTENSION}";

        private static bool IsSameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
