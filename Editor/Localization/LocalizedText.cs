using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace PackageSetupTool
{
    // 완성된 문장 대신 키와 인자로 저장하는 문장. 언어를 바꾸면 저장된 진행 문구·결과 로그도 새 언어로 다시 그리기 위함이다.
    // 인자도 LocalizedText 라 번역된 문장을 다른 문장 안에 넣을 수 있다. 번역하지 않는 값은 RawText 로 들어간다.
    [Serializable]
    public class LocalizedText
    {
        [SerializeField] private string _key;
        [SerializeReference] private List<LocalizedText> _args = new();

        public LocalizedText(string key, params object[] args)
        {
            _key = key;
            _args = args
                .Select(x => x as LocalizedText ?? new RawText(Convert.ToString(x, CultureInfo.InvariantCulture)))
                .ToList();
        }

        // RawText 용
        protected LocalizedText()
        {
        }

        public bool HasKey(string key) => _key == key;

        // 현재 언어의 문장
        public virtual string GetText()
        {
            return LocalizationManager.Get(_key, _args.Select(x => x.GetText()).ToArray<object>());
        }
    }
}
