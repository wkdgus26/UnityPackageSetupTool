using System;

namespace PackageSetupTool
{
    // package.json 에서 UPM 이 요구하는 필드만 읽는다(JsonUtility 는 나머지 필드를 무시한다).
    [Serializable]
    public class UpmPackageManifest
    {
        public string name;
        public string version;
    }
}
