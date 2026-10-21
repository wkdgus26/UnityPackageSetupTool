using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PackageSetupTool
{
    // 공용 폴더의 프리셋·카탈로그 JSON 파일 읽기·쓰기와 내용 검증.
    // 형식은 각 클래스의 직렬화 필드 그대로다. 프리셋: { "packages": [ { "type", "id", "displayName", "isManual" } ] }
    // 파일 입출력 예외(잠김·권한·경로 없음)는 정상 흐름에서 생길 수 있어 사유로 돌려준다. 성공하면 null 이다.
    public static class PresetJsonSerializer
    {
        // 읽지 못하면 preset 은 null 이다. 프리셋 이름은 파일 이름이다.
        public static LocalizedText ReadPreset(string path, out PackagePresetData preset)
        {
            var result = new PackagePresetData();
            LocalizedText error = ReadFile(path, result) ?? ValidateEntries(result.packages);
            if (error != null)
            {
                preset = null;
                return error;
            }
            result.name = Path.GetFileNameWithoutExtension(path);
            preset = result;
            return null;
        }

        // 읽지 못하면 catalog 는 null 이다.
        public static LocalizedText ReadCatalog(string path, out PackageCatalog catalog)
        {
            var result = new PackageCatalog();
            LocalizedText error = ReadFile(path, result) ?? ValidateEntries(result.entries) ?? ValidateDescriptions(result.descriptions);
            catalog = error == null ? result : null;
            return error;
        }

        public static LocalizedText WriteFile(string path, object data)
        {
            // 에디터 종료·동기화 도구 때문에 쓰다 만 파일이 남지 않도록 임시 파일에 다 쓴 뒤 바꿔 넣는다.
            string tempPath = $"{path}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(tempPath, JsonUtility.ToJson(data, true));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
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

        // 파일 내용으로 target 의 필드를 덮어쓴다. 다른 에디터나 사람이 고칠 수 있는 파일이라 형식이 보장되지 않는다.
        private static LocalizedText ReadFile(string path, object target)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException e)
            {
                return new RawText(e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                return new RawText(e.Message);
            }

            // JsonUtility 는 빈 문자열을 오류 없이 빈 객체로 읽는다(6000.6 실측). 쓰다 만 파일일 수 있어 거부한다.
            if (string.IsNullOrWhiteSpace(json))
            {
                return new LocalizedText("presetFile.empty");
            }
            try
            {
                JsonUtility.FromJsonOverwrite(json, target);
            }
            catch (ArgumentException e)
            {
                return new LocalizedText("presetFile.invalidJson", e.Message);
            }
            return null;
        }

        // JsonUtility 는 정의되지 않은 enum 값과 빠진 문자열 필드(null)를 그대로 받아들인다(6000.6 실측).
        private static LocalizedText ValidateEntries(List<PackageEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!Enum.IsDefined(typeof(PackageType), entry.type))
                {
                    return new LocalizedText("presetFile.unknownType", i, (int)entry.type);
                }
                if (string.IsNullOrWhiteSpace(entry.id))
                {
                    return new LocalizedText("presetFile.emptyId", i);
                }
                if (string.IsNullOrEmpty(entry.displayName))
                {
                    return new LocalizedText("presetFile.emptyDisplayName", i, entry.id);
                }
            }
            var duplicated = entries.GroupBy(x => (x.type, x.id)).FirstOrDefault(x => x.Count() > 1);
            return duplicated != null ? new LocalizedText("presetFile.duplicateItem", duplicated.Key.type, duplicated.Key.id) : null;
        }

        // 설명 문장은 비어 있어도 된다(툴팁을 띄우지 않을 뿐).
        private static LocalizedText ValidateDescriptions(List<PackageDescription> descriptions)
        {
            for (int i = 0; i < descriptions.Count; i++)
            {
                var description = descriptions[i];
                if (!Enum.IsDefined(typeof(PackageType), description.type))
                {
                    return new LocalizedText("presetFile.unknownType", i, (int)description.type);
                }
                if (string.IsNullOrWhiteSpace(description.id))
                {
                    return new LocalizedText("presetFile.emptyId", i);
                }
            }
            return null;
        }
    }
}
