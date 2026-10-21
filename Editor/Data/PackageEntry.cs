using System;

namespace PackageSetupTool
{
    [Serializable]
    public class PackageEntry
    {
        public PackageType type;
        // UnityRegistry: 패키지 이름, MyAssets: productId, Git: manifest에 쓰는 URL
        public string id;
        public string displayName;
        // Git 탭에서 URL을 직접 입력해 추가한 항목
        public bool isManual;

        public PackageEntry(PackageType type, string id, string displayName, bool isManual = false)
        {
            this.type = type;
            this.id = id;
            this.displayName = displayName;
            this.isManual = isManual;
        }

        public bool HasKey(PackageType otherType, string otherId)
        {
            return type == otherType && string.Equals(id, otherId, StringComparison.Ordinal);
        }

        public PackageEntry Clone()
        {
            return new PackageEntry(type, id, displayName, isManual);
        }
    }
}
