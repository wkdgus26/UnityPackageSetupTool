using System;

namespace PackageSetupTool
{
    // 카탈로그 항목의 설명. 프리셋 JSON 에 설명이 들어가지 않도록 PackageEntry 와 따로 카탈로그에만 저장한다.
    [Serializable]
    public class PackageDescription
    {
        public PackageType type;
        public string id;
        public string text;

        public PackageDescription(PackageType type, string id, string text)
        {
            this.type = type;
            this.id = id;
            this.text = text;
        }

        public bool HasKey(PackageType otherType, string otherId)
        {
            return type == otherType && string.Equals(id, otherId, StringComparison.Ordinal);
        }
    }
}
