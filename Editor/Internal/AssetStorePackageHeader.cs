using System;

namespace PackageSetupTool
{
    // Asset Store .unitypackage gzip 헤더의 "A$" 필드에 담긴 JSON 중 필요한 값
    [Serializable]
    public class AssetStorePackageHeader
    {
        public string id;
    }
}
