using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PackageSetupTool
{
    // 프리셋을 적용하기 전에 항목마다 이미 설치·임포트됐는지, 설치된 항목에 업데이트가 있는지 판정한다.
    // 확인 창에 보여주고 재임포트·제외를 고르게 하기 위함이다.
    // 설치 판정 기준은 적용 단계와 같다. Registry·Git: Client.List 전체(간접 의존 포함)의 이름·git URL(UpmInstallStep),
    // Asset Store: 임포트 기록(AssetStoreImportStep). 등록용 InstalledPackageScanner 는 직접 설치·BuiltIn 제외 기준이라 쓰지 않는다.
    // 업데이트 판정: Registry 는 권장 버전, Git 은 git ls-remote 의 원격 커밋, Asset Store 는 권장 업로드 ID 와 비교한다.
    // 업데이트 확인은 실패해도 설치 판정을 막지 않는다(안내만 남긴다).
    public class InstallStatusChecker
    {
        private const int AssetStoreUpdateTimeoutSeconds = 30;

        private readonly AssetStoreInternalBridge _bridge;
        private List<PackageEntry> _packages;
        private HashSet<string> _importedProductIds;
        private HashSet<string> _upmInstalledKeys;
        // UPM 항목이 없으면 null
        private ListRequest _listRequest;
        // 온라인 목록을 받지 못해 로컬 캐시 목록으로 다시 요청했는지
        private bool _isOfflineList;
        private readonly List<(PackageEntry package, PackageInfo installed, GitRemoteQuery query)> _gitQueries = new();
        private readonly List<PackageUpdateInfo> _updates = new();
        private readonly Dictionary<long, long> _outdatedLocalUploadIds = new();
        private readonly List<LocalizedText> _notices = new();
        // Asset Store 업데이트 확인 요청이 끝나기를 기다리는 수(권장 업로드 조회 1 + 최신 버전 문자열 조회 n)
        private int _pendingAssetStoreRequests;
        private DateTime _assetStoreDeadline;

        public InstallStatusChecker(AssetStoreInternalBridge bridge)
        {
            _bridge = bridge;
        }

        public bool IsDone => _upmInstalledKeys != null && _gitQueries.All(x => x.query.IsDone) && _pendingAssetStoreRequests == 0;
        // 패키지 목록을 받지 못한 사유. 없으면 null. 이때는 IsDone 이 되지 않고 시간 상한으로 끝나지 않도록 HasFailed 로 알린다.
        public LocalizedText Error { get; private set; }
        public bool HasFailed => Error != null;

        public void Begin(List<PackageEntry> packages)
        {
            _packages = packages;
            _importedProductIds = FindImportedProductIds();
            BeginAssetStoreUpdateCheck();
            if (packages.Exists(x => x.type != PackageType.MyAssets))
            {
                // 업데이트 판정에 레지스트리 최신 버전 정보가 필요해 온라인으로 받는다(오프라인 0.2초, 온라인 1.1초 실측).
                _listRequest = Client.List(false, true);
                return;
            }
            _upmInstalledKeys = new HashSet<string>();
        }

        public void Tick()
        {
            if (_upmInstalledKeys == null && !HasFailed && _listRequest.IsCompleted)
            {
                OnListCompleted();
            }
            foreach (var (package, installed, query) in _gitQueries.Where(x => !x.query.IsDone))
            {
                query.Tick();
                if (query.IsDone)
                {
                    AddGitUpdate(package, installed, query);
                }
            }
            if (_pendingAssetStoreRequests > 0 && DateTime.UtcNow > _assetStoreDeadline)
            {
                _pendingAssetStoreRequests = 0;
                _notices.Add(new LocalizedText("check.update.assetStoreFailed", new LocalizedText("check.update.timeoutSeconds", AssetStoreUpdateTimeoutSeconds)));
            }
        }

        // 진행 줄에 보여줄 진행 중인 확인. 패키지 목록을 받기 전에는 목록 조회, 받은 뒤에는 남은 업데이트 확인을 보여준다.
        public List<LocalizedText> GetProgressTexts()
        {
            var texts = new List<LocalizedText>();
            if (_upmInstalledKeys == null)
            {
                texts.Add(new LocalizedText("busy.check.list"));
            }
            else if (_gitQueries.Count > 0)
            {
                texts.Add(new LocalizedText("busy.check.git", _gitQueries.Count(x => x.query.IsDone), _gitQueries.Count));
            }
            if (_pendingAssetStoreRequests > 0)
            {
                texts.Add(new LocalizedText("busy.check.assetStore"));
            }
            return texts;
        }

        // 확인을 끝내지 못하고 버릴 때(시간 상한) 아직 실행 중인 git 프로세스를 끝낸다.
        public void StopGitQueries()
        {
            foreach (var (_, _, query) in _gitQueries.Where(x => !x.query.IsDone))
            {
                query.Stop();
            }
        }

        // preset: 확인한 프리셋(복사본)
        public InstallCheckResult CreateResult(PackagePresetData preset)
        {
            var installedPackages = _packages
                .Where(x => (x.type == PackageType.MyAssets ? _importedProductIds : _upmInstalledKeys).Contains(x.id))
                .Select(x => x.Clone())
                .ToList();
            // 설치된 항목만 업데이트 대상이다(임포트 기록이 없는 구매 에셋은 로컬 파일 갱신만 한다).
            var updates = _updates.Where(x => installedPackages.Exists(y => y.HasKey(x.Package.type, x.Package.id))).ToList();
            return new InstallCheckResult(preset, installedPackages, updates, _outdatedLocalUploadIds, _notices);
        }

        private void OnListCompleted()
        {
            if (_listRequest.Status == StatusCode.Failure && !_isOfflineList)
            {
                // 네트워크가 없으면 설치 판정은 할 수 있도록 로컬 캐시 목록으로 다시 받는다. 업데이트 판정은 캐시 기준이 된다.
                _notices.Add(new LocalizedText("check.update.registryOffline", new RawText(_listRequest.Error.message)));
                _isOfflineList = true;
                _listRequest = Client.List(true, true);
                return;
            }
            if (_listRequest.Status == StatusCode.Failure)
            {
                Error = new LocalizedText("check.listFailed", new RawText(_listRequest.Error.message));
                return;
            }
            PackageCollection installedPackages = _listRequest.Result;
            _upmInstalledKeys = installedPackages.GetInstalledKeys();
            foreach (var package in _packages.Where(x => x.type != PackageType.MyAssets))
            {
                PackageInfo installed = installedPackages.FindByKey(package.id);
                if (installed == null)
                {
                    continue;
                }
                if (installed.source == PackageSource.Git)
                {
                    BeginGitUpdateCheck(package, installed);
                }
                else
                {
                    AddRegistryUpdate(package, installed);
                }
            }
        }

        // 다시 요청하면 설치되는 버전(권장, 없으면 최신 호환)이 호환 목록에서 설치 버전보다 뒤에 있으면 업데이트로 본다.
        // 권장보다 높은 버전이 설치돼 있거나, 설치 버전이 목록에 없거나(로컬·내장 패키지 등), BuiltIn 이면 업데이트로 보지 않는다.
        private void AddRegistryUpdate(PackageEntry package, PackageInfo installed)
        {
            if (installed.source == PackageSource.BuiltIn)
            {
                return;
            }
            string targetVersion = string.IsNullOrEmpty(installed.versions.recommended) ? installed.versions.latestCompatible : installed.versions.recommended;
            string[] compatibleVersions = installed.versions.compatible;
            int installedIndex = Array.IndexOf(compatibleVersions, installed.version);
            int targetIndex = Array.IndexOf(compatibleVersions, targetVersion);
            if (installedIndex >= 0 && targetIndex > installedIndex)
            {
                _updates.Add(new PackageUpdateInfo(package.Clone(), installed.version, targetVersion, false));
            }
        }

        // 전체 커밋 해시로 고정한 리비전은 조회하지 않는다. 그 밖에는 원격에 물어 태그·짧은 커밋이면 고정, HEAD·브랜치면 커밋을 비교한다.
        private void BeginGitUpdateCheck(PackageEntry package, PackageInfo installed)
        {
            string revision = string.IsNullOrEmpty(installed.git.revision) ? GitRemoteQuery.HeadRevision : installed.git.revision;
            if (GitRevision.IsFullCommitHash(revision))
            {
                _updates.Add(new PackageUpdateInfo(package.Clone(), GitRevision.ToShortCommit(revision), null, true));
                return;
            }
            var query = new GitRemoteQuery(new GitPackageUrl(installed.GetGitUrl()).RepositoryUrl, revision);
            query.Begin();
            _gitQueries.Add((package, installed, query));
            if (query.IsDone)
            {
                AddGitUpdate(package, installed, query);
            }
        }

        private void AddGitUpdate(PackageEntry package, PackageInfo installed, GitRemoteQuery query)
        {
            if (query.Error != null)
            {
                _notices.Add(new LocalizedText("check.update.gitFailed", package.displayName, query.Error));
                return;
            }
            if (query.IsPinned)
            {
                _updates.Add(new PackageUpdateInfo(package.Clone(), installed.git.revision, null, true));
                return;
            }
            if (!string.Equals(query.RemoteCommit, installed.git.hash, StringComparison.OrdinalIgnoreCase))
            {
                _updates.Add(new PackageUpdateInfo(package.Clone(), installed.GetVersionLabel(), GitRevision.ToShortCommit(query.RemoteCommit), false));
            }
        }

        // 로컬 파일이나 임포트 기록이 있는 구매 에셋만 권장 업로드와 비교한다. 둘 다 없으면 받을 때 최신을 받으므로 확인할 것이 없다.
        private void BeginAssetStoreUpdateCheck()
        {
            var assetStorePackages = _packages.FindAll(x => x.type == PackageType.MyAssets);
            if (assetStorePackages.Count == 0)
            {
                return;
            }
            if (!_bridge.IsAvailable(AssetStoreFeature.UpdateInfo) || !_bridge.IsAvailable(AssetStoreFeature.LocalInfo))
            {
                var feature = _bridge.IsAvailable(AssetStoreFeature.UpdateInfo) ? AssetStoreFeature.LocalInfo : AssetStoreFeature.UpdateInfo;
                _notices.Add(new LocalizedText("check.update.assetStoreUnavailable", _bridge.GetMissingMessage(feature)));
                return;
            }
            if (string.IsNullOrEmpty(CloudProjectSettings.userId))
            {
                _notices.Add(new LocalizedText("check.update.loginRequired"));
                return;
            }

            // LocalInfo 는 PM 창을 연 적이 없는 세션에서 비어 있어 먼저 채운다.
            _bridge.RefreshLocalInfos();
            var targets = assetStorePackages
                .Select(x => (package: x, productId: long.Parse(x.id)))
                .Select(x => (x.package, x.productId, local: _bridge.GetLocalVersion(x.productId), imported: _bridge.GetImportedVersion(x.productId)))
                .Where(x => x.local != null || x.imported != null)
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            _pendingAssetStoreRequests = 1;
            _assetStoreDeadline = DateTime.UtcNow.AddSeconds(AssetStoreUpdateTimeoutSeconds);
            _bridge.RequestRecommendedUploadIds(targets.Select(x => x.productId).ToList(), recommendedUploadIds =>
            {
                // 시간 상한으로 이미 끝냈으면 늦게 온 응답은 버린다.
                if (_pendingAssetStoreRequests == 0)
                {
                    return;
                }
                foreach (var (package, productId, local, imported) in targets)
                {
                    if (!recommendedUploadIds.TryGetValue(productId, out long recommendedUploadId))
                    {
                        continue;
                    }
                    if (local != null && local.Value.uploadId != recommendedUploadId)
                    {
                        _outdatedLocalUploadIds[productId] = recommendedUploadId;
                    }
                    if (imported != null && imported.Value.uploadId != recommendedUploadId)
                    {
                        RequestLatestAssetStoreVersion(package, imported.Value.versionString);
                    }
                }
                _pendingAssetStoreRequests--;
            }, error =>
            {
                if (_pendingAssetStoreRequests == 0)
                {
                    return;
                }
                _notices.Add(new LocalizedText("check.update.assetStoreFailed", new RawText(error)));
                _pendingAssetStoreRequests--;
            });
        }

        // 업데이트 응답에는 버전 문자열이 없어 상품 상세의 스토어 최신 버전을 표시에 쓴다.
        private void RequestLatestAssetStoreVersion(PackageEntry package, string importedVersion)
        {
            _pendingAssetStoreRequests++;
            _bridge.RequestProductVersion(long.Parse(package.id), latestVersion =>
            {
                if (_pendingAssetStoreRequests == 0)
                {
                    return;
                }
                _updates.Add(new PackageUpdateInfo(package.Clone(), importedVersion, latestVersion, false));
                _pendingAssetStoreRequests--;
            }, error =>
            {
                if (_pendingAssetStoreRequests == 0)
                {
                    return;
                }
                _notices.Add(new LocalizedText("check.update.assetStoreFailed", new RawText(error)));
                _pendingAssetStoreRequests--;
            });
        }

        // 임포트 기록을 쓸 수 없으면 모두 미임포트로 본다. 임포트 단계도 같은 조건에서 임포트를 진행한다.
        private HashSet<string> FindImportedProductIds()
        {
            var result = new HashSet<string>();
            var assetStorePackages = _packages.FindAll(x => x.type == PackageType.MyAssets);
            if (assetStorePackages.Count == 0)
            {
                return result;
            }
            if (!_bridge.IsAvailable(AssetStoreFeature.ImportedInfo))
            {
                _notices.Add(new LocalizedText("check.assetStoreUnavailable", _bridge.GetMissingMessage(AssetStoreFeature.ImportedInfo)));
                return result;
            }
            if (!_bridge.TryFillImportedPackages())
            {
                _notices.Add(new LocalizedText("check.assetStoreSessionOnly"));
            }
            foreach (var package in assetStorePackages)
            {
                if (_bridge.IsImported(long.Parse(package.id)))
                {
                    result.Add(package.id);
                }
            }
            return result;
        }
    }
}
