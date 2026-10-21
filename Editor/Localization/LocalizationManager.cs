using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    // 도구 문장의 언어 선택과 조회. 언어별 문장은 <도구 루트>/Localization/ 의 LanguageData 에셋에 있다.
    // 다른 패키지(Localization 등)에 의존하지 않는다.
    // 아래 오류는 언어 데이터 자체가 잘못된 경우라 번역 문장을 쓸 수 없어 영어로 둔다.
    public static class LocalizationManager
    {
        public static event Action OnLanguageChanged;

        private static List<LanguageData> _languages;
        private static LanguageData _currentLanguage;

        public static IReadOnlyList<LanguageData> Languages => _languages ??= LoadLanguages();
        public static LanguageData CurrentLanguage => _currentLanguage ??= SelectStartLanguage();

        private static string LanguageFolderPath => $"{ResourceLoader.ToolRootPath}/{LOCALIZATION_FOLDER}";

        public static void SetLanguage(LanguageData language)
        {
            EditorPrefs.SetString(LANGUAGE_PREF_KEY, language.language.ToString());
            _currentLanguage = language;
            OnLanguageChanged?.Invoke();
        }

        // 언어 에셋을 추가·삭제했을 수 있어 언어 선택 목록을 열 때 다시 읽는다.
        public static void ReloadLanguages()
        {
            _languages = null;
            _currentLanguage = null;
        }

        public static string Get(string key, params object[] args)
        {
            if (!CurrentLanguage.TryGetText(key, out string text))
            {
                throw new KeyNotFoundException($"[Package Setup] Localization key '{key}' is missing in {CurrentLanguage.name}.");
            }
            return args.Length == 0 ? text : string.Format(text, args);
        }

        // 고른 언어 → 에디터 언어 → 기본 언어 순서. 저장값과 에디터 설정은 외부 값이라 해당 언어 데이터가 없을 수 있다.
        // 에디터 언어를 읽는 공개 API가 없어 Preferences 가 저장하는 키(SystemLanguage 이름, 6000.6 실측)를 읽는다.
        private static LanguageData SelectStartLanguage()
        {
            return FindLanguage(EditorPrefs.GetString(LANGUAGE_PREF_KEY, string.Empty))
                   ?? FindLanguage(EditorPrefs.GetString(EDITOR_LOCALE_PREF_KEY, string.Empty))
                   ?? FindLanguage(DEFAULT_LANGUAGE.ToString())
                   ?? throw new InvalidOperationException($"[Package Setup] {DEFAULT_LANGUAGE} language data is missing in {LanguageFolderPath}.");
        }

        private static LanguageData FindLanguage(string languageName)
        {
            if (!Enum.TryParse(languageName, out SystemLanguage language))
            {
                return null;
            }
            return Languages.FirstOrDefault(x => x.language == language);
        }

        private static List<LanguageData> LoadLanguages()
        {
            string folderPath = LanguageFolderPath;
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                throw new InvalidOperationException($"[Package Setup] Localization folder is missing: {folderPath}");
            }
            var languages = AssetDatabase.FindAssets($"t:{nameof(LanguageData)}", new[] { folderPath })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<LanguageData>)
                .OrderBy(x => x.name)
                .ToList();
            if (languages.Count == 0)
            {
                throw new InvalidOperationException($"[Package Setup] No language data in {folderPath}.");
            }
            var duplicated = languages.GroupBy(x => x.language).FirstOrDefault(x => x.Count() > 1);
            if (duplicated != null)
            {
                throw new InvalidOperationException($"[Package Setup] Multiple language data for {duplicated.Key}: {string.Join(", ", duplicated.Select(x => x.name))}");
            }
            return languages;
        }
    }
}
