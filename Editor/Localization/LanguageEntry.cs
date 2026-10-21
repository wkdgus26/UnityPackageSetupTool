using System;
using UnityEngine;

namespace PackageSetupTool
{
    // 문장 하나. 인자는 {0}, {1} 형식으로 넣는다.
    [Serializable]
    public struct LanguageEntry
    {
        public string key;
        [TextArea] public string text;
    }
}
