using System.Collections.Generic;
using System.Linq;
using UnityEditor.PackageManager;

namespace PackageSetupTool
{
    // 설치된 패키지 등록 전 검사 결과. 등록할 항목을 고르는 창에 보여주고, 등록할 때 다시 넘긴다.
    public class InstalledScanResult
    {
        public InstalledScanResult(
            PackagePresetData preset,
            Dictionary<PackageType, List<PackageEntry>> installed,
            Dictionary<PackageEntry, string> descriptions,
            Dictionary<PackageEntry, RegistryInfo> scopedRegistries,
            Dictionary<PackageType, int> excludedCounts,
            List<LocalizedText> notices,
            bool hasFailure)
        {
            Preset = preset;
            Installed = installed;
            Descriptions = descriptions;
            ScopedRegistries = scopedRegistries;
            ExcludedCounts = excludedCounts;
            Notices = notices;
            HasFailure = hasFailure;
        }

        // 검사가 끝난 시점의 등록 대상 프리셋. 이미 등록된 항목을 표시하는 데 쓴다. 등록은 저장할 때의 파일 최신본에 한다.
        public PackagePresetData Preset { get; }
        // 검사를 마친 종류만 있다. 실패·시간 초과한 종류는 없고 그 사유가 Notices 에 있다.
        public IReadOnlyDictionary<PackageType, List<PackageEntry>> Installed { get; }
        // Registry·Git 항목 → 설명. 카탈로그에 넣을 때 함께 저장한다.
        public IReadOnlyDictionary<PackageEntry, string> Descriptions { get; }
        // scoped registry 에서 설치한 Registry 항목 → 그 레지스트리
        public IReadOnlyDictionary<PackageEntry, RegistryInfo> ScopedRegistries { get; }
        // 종류별로 등록 대상에서 뺀 패키지 수(에디터 포함·로컬 패키지)
        public IReadOnlyDictionary<PackageType, int> ExcludedCounts { get; }
        // 검사 실패·시간 초과, 결과가 불완전할 수 있는 이유, 제외한 패키지 안내
        public IReadOnlyList<LocalizedText> Notices { get; }
        // 실패·시간 초과한 종류가 있음
        public bool HasFailure { get; }

        public List<PackageEntry> InstalledPackages => Installed.Values.SelectMany(x => x).ToList();

        public bool IsRegistered(PackageEntry package) => Preset.Contains(package.type, package.id);

        // scoped registry 에서 설치한 항목이 아니면 null
        public RegistryInfo FindScopedRegistry(PackageEntry package)
        {
            return ScopedRegistries.TryGetValue(package, out var registry) ? registry : null;
        }
    }
}
