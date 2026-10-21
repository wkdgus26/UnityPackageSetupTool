using System;
using UnityEngine;

namespace PackageSetupTool
{
    // 번역하지 않는 원문. Unity·UPM이 돌려준 오류 메시지, 이름, 경로처럼 언어와 무관한 값이다.
    [Serializable]
    public class RawText : LocalizedText
    {
        [SerializeField] private string _text;

        public RawText(string text)
        {
            _text = text;
        }

        public override string GetText()
        {
            return _text;
        }
    }
}
