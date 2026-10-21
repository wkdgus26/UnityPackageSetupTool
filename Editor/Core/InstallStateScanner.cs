using System.Collections.Generic;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PackageSetupTool
{
    // 메인 목록의 설치 상태 배지를 위해 현재 프로젝트의 설치 상태를 판정한다.
    // Registry·Git: 오프라인 Client.List 전체(간접 의존 포함, 적용 판정과 같은 범위)의 이름·git URL.
    //   출처가 BuiltIn 이면 BuiltIn, 직접 설치가 아니면 Dependency, 그 밖은 Installed 로 구분한다.
    // Asset Store: 임포트 기록(적용 판정과 같음). 기록은 PM 이 에셋 변경 때마다 갱신하므로 조회할 때마다 묻는다.
    //   6000.0~6000.4 는 기록을 채우는 내부 API가 없어 PM이 가진 기록만 본다(실측에서는 새 세션에도 이전 세션의 기록이 있었다).
    //   전체 스캔은 브리지가 도메인당 한 번만 한다.
    public class InstallStateScanner
    {
        private readonly AssetStoreInternalBridge _bridge;
        // 이름·git URL → 패키지
        private readonly Dictionary<string, PackageInfo> _upmPackages = new();
        private ListRequest _listRequest;
        private bool _canCheckImported;

        public InstallStateScanner(AssetStoreInternalBridge bridge)
        {
            _bridge = bridge;
        }

        public bool IsDone { get; private set; }
        // 패키지 목록을 받지 못한 사유. 없으면 null
        public LocalizedText Error { get; private set; }

        public void Begin()
        {
            // 설치 판정에는 레지스트리 정보가 필요 없어 로컬 목록으로 받는다(0.2초 실측).
            _listRequest = Client.List(true, true);
            _canCheckImported = _bridge.IsAvailable(AssetStoreFeature.ImportedInfo);
            if (_canCheckImported)
            {
                _bridge.TryFillImportedPackages();
            }
        }

        public void Tick()
        {
            if (!_listRequest.IsCompleted)
            {
                return;
            }
            IsDone = true;
            if (_listRequest.Status == StatusCode.Failure)
            {
                Error = new RawText(_listRequest.Error.message);
                return;
            }
            foreach (var packageInfo in _listRequest.Result)
            {
                _upmPackages[packageInfo.name] = packageInfo;
                if (packageInfo.source == PackageSource.Git)
                {
                    _upmPackages[packageInfo.GetGitUrl()] = packageInfo;
                }
            }
        }

        public (PackageInstallState state, string version) GetState(PackageEntry entry)
        {
            if (entry.type == PackageType.MyAssets)
            {
                bool isImported = _canCheckImported && _bridge.IsImported(long.Parse(entry.id));
                return (isImported ? PackageInstallState.Installed : PackageInstallState.NotInstalled, null);
            }
            if (!_upmPackages.TryGetValue(entry.id, out var packageInfo))
            {
                return (PackageInstallState.NotInstalled, null);
            }
            PackageInstallState state = packageInfo.source == PackageSource.BuiltIn ? PackageInstallState.BuiltIn
                : packageInfo.isDirectDependency ? PackageInstallState.Installed
                : PackageInstallState.Dependency;
            return (state, packageInfo.GetVersionLabel());
        }
    }
}
