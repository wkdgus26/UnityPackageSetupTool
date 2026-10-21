using System;
using System.Collections.Generic;
using UnityEngine;

namespace PackageSetupTool
{
    // 언어 하나의 문장 목록. <도구 루트>/Localization/ 폴더에 에셋을 추가하면 언어 선택 목록에 나타난다.
    public class LanguageData : ScriptableObject
    {
        public SystemLanguage language;
        // 언어 선택 목록에 보이는 이름. 그 언어로 쓴다(예: 한국어, English).
        public string displayName;
        public List<LanguageEntry> entries = new();

        // 키 → 문장. 인스펙터에서 고치면 OnValidate 에서 비워 다시 만든다.
        [NonSerialized] private Dictionary<string, string> _texts;

        private Dictionary<string, string> Texts => _texts ??= BuildTexts();

        public bool TryGetText(string key, out string text)
        {
            return Texts.TryGetValue(key, out text);
        }

        private Dictionary<string, string> BuildTexts()
        {
            var texts = new Dictionary<string, string>();
            foreach (var entry in entries)
            {
                if (!texts.TryAdd(entry.key, entry.text))
                {
                    throw new InvalidOperationException($"[Package Setup] Duplicate localization key '{entry.key}' in {name}.");
                }
            }
            return texts;
        }

        private void OnValidate()
        {
            _texts = null;
        }
    }
}
