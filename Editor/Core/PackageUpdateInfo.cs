namespace PackageSetupTool
{
    // 이미 설치된 항목 하나의 업데이트 정보. 업데이트가 있거나 리비전이 고정된 항목만 만든다.
    public class PackageUpdateInfo
    {
        // isPinned: 태그·커밋으로 고정된 Git 리비전이라 업데이트를 확인하지 않음. 이때 currentVersion 은 고정 리비전, latestVersion 은 null
        public PackageUpdateInfo(PackageEntry package, string currentVersion, string latestVersion, bool isPinned)
        {
            Package = package;
            CurrentVersion = currentVersion;
            LatestVersion = latestVersion;
            IsPinned = isPinned;
        }

        public PackageEntry Package { get; }
        public string CurrentVersion { get; }
        public string LatestVersion { get; }
        public bool IsPinned { get; }
        public bool HasUpdate => !IsPinned;
    }
}
