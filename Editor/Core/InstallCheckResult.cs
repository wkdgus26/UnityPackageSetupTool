using System.Collections.Generic;
using System.Linq;

namespace PackageSetupTool
{
    // 프리셋 적용 전 설치 상태 확인 결과. 확인 창에 보여주고, 적용을 시작할 때 다시 넘긴다.
    public class InstallCheckResult
    {
        public InstallCheckResult(
            PackagePresetData preset,
            List<PackageEntry> installedPackages,
            List<PackageUpdateInfo> updates,
            Dictionary<long, long> outdatedLocalUploadIds,
            List<LocalizedText> notices)
        {
            Preset = preset;
            InstalledPackages = installedPackages;
            Updates = updates;
            OutdatedLocalUploadIds = outdatedLocalUploadIds;
            Notices = notices;
        }

        // 확인을 시작할 때 복사한 프리셋. 확인한 목록과 적용할 목록이 같도록 이것으로 적용한다.
        public PackagePresetData Preset { get; }
        // 프리셋 순서
        public IReadOnlyList<PackageEntry> InstalledPackages { get; }
        // 이미 설치된 항목 중 업데이트가 있거나 리비전이 고정된 항목
        public IReadOnlyList<PackageUpdateInfo> Updates { get; }
        // 로컬 .unitypackage 가 최신이 아닌 구매 에셋: productId → 받아야 할 업로드 ID
        public IReadOnlyDictionary<long, long> OutdatedLocalUploadIds { get; }
        // 판정이 불완전할 수 있는 이유
        public IReadOnlyList<LocalizedText> Notices { get; }

        public int UpdateCount => Updates.Count(x => x.HasUpdate);

        // 없으면 null
        public PackageUpdateInfo FindUpdate(PackageEntry package)
        {
            return Updates.FirstOrDefault(x => x.Package.HasKey(package.type, package.id));
        }
    }
}
