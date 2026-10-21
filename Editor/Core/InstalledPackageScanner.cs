using System.Collections.Generic;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace PackageSetupTool
{
    // 현재 프로젝트에 설치된 패키지를 종류별로 찾는다. 설치된 패키지를 프리셋에 등록할 때 PackageSetupSystem 이 쓴다.
    // Registry·Git: 직접 설치된 UPM 패키지. 다음은 다른 프로젝트에서 같은 방법으로 설치할 수 없어 제외한다.
    //   - 내장 모듈과 출처가 BuiltIn 인 패키지(에디터에 포함된 패키지)
    //   - 로컬 패키지(Local·Embedded·LocalTarball): 이 PC·프로젝트의 경로에 묶여 있다.
    // Asset Store: 카탈로그의 구매 항목 중 임포트 기록이 있는 것. 구매 목록은 목록 갱신으로만 얻을 수 있다.
    public class InstalledPackageScanner
    {
        private const string ModulePackageType = "module";

        private readonly AssetStoreInternalBridge _bridge;
        private ListRequest _listRequest;

        public InstalledPackageScanner(AssetStoreInternalBridge bridge)
        {
            _bridge = bridge;
        }

        public bool IsDone { get; private set; }
        public Dictionary<PackageType, List<PackageEntry>> Installed { get; } = new();
        // 제외한 에디터 포함 패키지 수
        public int BuiltInExcludedCount { get; private set; }
        // 제외한 로컬 패키지의 표시명
        public List<string> LocalExcludedNames { get; } = new();
        // 기본 레지스트리가 아닌 scoped registry 에서 설치한 Registry 항목 → 그 레지스트리
        public Dictionary<PackageEntry, RegistryInfo> ScopedRegistries { get; } = new();
        // Registry·Git 항목 → 설명. 카탈로그에 넣을 때 함께 저장한다.
        public Dictionary<PackageEntry, string> Descriptions { get; } = new();
        // 종류별 실패 사유. 실패한 종류는 Installed 에 없다.
        public Dictionary<PackageType, LocalizedText> Errors { get; } = new();
        // 실패는 아니지만 결과가 불완전할 수 있는 이유
        public List<LocalizedText> Notices { get; } = new();

        public void Begin(List<PackageEntry> purchasedEntries)
        {
            _listRequest = Client.List(true, false);
            FindImportedAssetStorePackages(purchasedEntries);
        }

        public void Tick()
        {
            if (!_listRequest.IsCompleted)
            {
                return;
            }
            if (_listRequest.Status == StatusCode.Failure)
            {
                var error = new RawText(_listRequest.Error.message);
                Errors[PackageType.UnityRegistry] = error;
                Errors[PackageType.Git] = error;
                IsDone = true;
                return;
            }

            var registryEntries = new List<PackageEntry>();
            var gitEntries = new List<PackageEntry>();
            foreach (var packageInfo in _listRequest.Result)
            {
                if (!packageInfo.isDirectDependency || packageInfo.IsThisTool())
                {
                    continue;
                }
                if (packageInfo.source == PackageSource.Git)
                {
                    var entry = new PackageEntry(PackageType.Git, packageInfo.GetGitUrl(), packageInfo.displayName);
                    gitEntries.Add(entry);
                    Descriptions[entry] = packageInfo.description;
                }
                // 6000.6 부터 URP·Timeline 같은 코어 패키지도 출처가 BuiltIn 이다. 이전 버전에서는 Registry 라 등록된다.
                else if (packageInfo.type == ModulePackageType || packageInfo.source == PackageSource.BuiltIn)
                {
                    BuiltInExcludedCount++;
                }
                else if (packageInfo.source == PackageSource.Registry)
                {
                    var entry = new PackageEntry(PackageType.UnityRegistry, packageInfo.name, packageInfo.displayName);
                    registryEntries.Add(entry);
                    Descriptions[entry] = packageInfo.description;
                    if (!packageInfo.registry.isDefault)
                    {
                        ScopedRegistries[entry] = packageInfo.registry;
                    }
                }
                else
                {
                    LocalExcludedNames.Add(packageInfo.displayName);
                }
            }
            Installed[PackageType.UnityRegistry] = registryEntries;
            Installed[PackageType.Git] = gitEntries;
            IsDone = true;
        }

        private void FindImportedAssetStorePackages(List<PackageEntry> purchasedEntries)
        {
            if (!_bridge.IsAvailable(AssetStoreFeature.ImportedInfo))
            {
                Errors[PackageType.MyAssets] = _bridge.GetMissingMessage(AssetStoreFeature.ImportedInfo);
                return;
            }
            if (purchasedEntries.Count == 0)
            {
                Notices.Add(new LocalizedText("register.assetStoreNoPurchases"));
            }
            if (!_bridge.TryFillImportedPackages())
            {
                Notices.Add(new LocalizedText("register.assetStoreSessionOnly"));
            }

            Installed[PackageType.MyAssets] = purchasedEntries.FindAll(x => _bridge.IsImported(long.Parse(x.id)));
        }
    }
}
