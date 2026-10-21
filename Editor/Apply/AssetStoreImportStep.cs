using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PackageSetupTool
{
    // 구매 에셋을 한 개씩 비대화식으로 임포트한다.
    // 내부 Install 을 쓸 수 있으면 Package Manager와 같은 방식(AssetOrigin 기록)으로, 없으면 공개 AssetDatabase.ImportPackage 로 임포트한다.
    // 이미 임포트된 항목은 건너뛰고, 사용자가 재임포트로 고른 항목만 다시 임포트한다.
    // 로컬 파일이 최신이 아니던 항목은 다운로드 단계에서 최신을 받지 못했으면 예전 버전으로 덮어쓰지 않도록 임포트하지 않는다.
    [Serializable]
    public class AssetStoreImportStep : ApplyStepBase
    {
        private const int ImportTimeoutMinutes = 10;

        [SerializeField] private List<PackageEntry> _reimportTargets = new();
        // 최신을 받아야 하는 상품과 받아야 할 업로드 ID(같은 순서)
        [SerializeField] private List<long> _refreshProductIds = new();
        [SerializeField] private List<long> _refreshUploadIds = new();
        [SerializeField] private int _index;
        [SerializeField] private bool _isImporting;
        [SerializeField] private string _importingPackagePath;
        [SerializeField] private bool _isPublicApiImport;
        [SerializeField] private bool _isReimport;
        [SerializeField] private long _deadlineTicks;

        // 도메인 리로드로 사라지는 값
        private ApplyContext _context;
        private bool _isSubscribed;

        public AssetStoreImportStep(List<PackageEntry> targets, List<PackageEntry> reimportTargets, IReadOnlyDictionary<long, long> outdatedLocalUploadIds) : base(targets)
        {
            _reimportTargets = reimportTargets;
            FillRefreshTargets(outdatedLocalUploadIds, _refreshProductIds, _refreshUploadIds);
        }

        public override string StepNameKey => "step.assetStoreImport";

        protected override void OnBegin(ApplyContext context)
        {
            Subscribe(context);
            FillImportedPackages(context);
        }

        public override void OnResume(ApplyContext context)
        {
            Subscribe(context);
            FillImportedPackages(context);
            if (!_isImporting)
            {
                return;
            }

            // 재임포트는 임포트 전부터 기록과 에셋이 있어 아래 판정으로는 다시 임포트됐는지 알 수 없다.
            // 에셋 파일의 수정 시각도 패키지에 기록된 시각으로 남아 쓸 수 없다(6000.6 실측). 완료 이벤트만 근거로 삼는다.
            if (_isReimport)
            {
                CompleteCurrent(ApplyOutcome.Unverified, new LocalizedText("import.reimportUnverified", new LocalizedText("import.reason.reload")));
                return;
            }

            // 임포트 직후 스크립트 컴파일로 리로드되면 완료 이벤트를 받지 못할 수 있다. 임포트 기록이 있으면 그것으로 판정한다.
            // 기록을 쓸 수 없거나(공개 API 임포트) 기록이 없으면(재시작으로 비었는데 전체 스캔이 없는 6000.0~6000.4 포함)
            // 패키지 에셋이 프로젝트에 있는지로 판정한다.
            bool canUseImportedInfo = !_isPublicApiImport && context.Bridge.IsAvailable(AssetStoreFeature.ImportedInfo);
            if (canUseImportedInfo && context.Bridge.IsImported(long.Parse(_targets[_index].id)))
            {
                CompleteCurrent(ApplyOutcome.Success, null);
                return;
            }
            CompleteByAssetPresence(new LocalizedText(canUseImportedInfo ? "import.reason.reloadWithoutRecord" : "import.reason.reload"));
        }

        public override void Tick(ApplyContext context)
        {
            if (_isImporting)
            {
                if (NowTicks > _deadlineTicks)
                {
                    var reason = new LocalizedText("import.reason.timeout", ImportTimeoutMinutes);
                    if (_isReimport)
                    {
                        CompleteCurrent(ApplyOutcome.Unverified, new LocalizedText("import.reimportUnverified", reason));
                    }
                    else
                    {
                        CompleteByAssetPresence(reason);
                    }
                }
                return;
            }

            if (_index >= _targets.Count)
            {
                Unsubscribe();
                Finish(context);
                return;
            }

            var package = _targets[_index];
            long productId = long.Parse(package.id);
            bool isImported = context.Bridge.IsAvailable(AssetStoreFeature.ImportedInfo) && context.Bridge.IsImported(productId);
            bool isReimportTarget = _reimportTargets.Exists(x => x.HasKey(package.type, package.id));
            if (isImported && !isReimportTarget)
            {
                MoveNext(ApplyOutcome.Skipped, new LocalizedText("import.alreadyImported"));
                return;
            }

            string path = context.LocalPackages.GetPath(productId);
            if (path == null)
            {
                MoveNext(ApplyOutcome.Failed, new LocalizedText("import.noLocalFile"));
                return;
            }
            int refreshIndex = _refreshProductIds.IndexOf(productId);
            if (refreshIndex >= 0 && context.LocalPackages.GetVersion(productId).uploadId != _refreshUploadIds[refreshIndex])
            {
                MoveNext(ApplyOutcome.Skipped, new LocalizedText("import.notLatest"));
                return;
            }

            // 내부 Install 은 Package Manager의 LocalInfo 로 파일을 찾으므로 둘 다 쓸 수 있을 때만 사용한다.
            _isPublicApiImport = !context.Bridge.IsAvailable(AssetStoreFeature.Install) || !context.LocalPackages.UsesInternalInfo;
            _isImporting = true;
            _isReimport = isImported;
            _importingPackagePath = path;
            _deadlineTicks = GetDeadlineTicks(TimeSpan.FromMinutes(ImportTimeoutMinutes));
            // 공개 임포트 API는 서명 없는 패키지에 확인 창을 띄워 사용자가 진행을 눌러야 끝난다(6000.6 실측).
            string progressKey = _isPublicApiImport ? "import.progress.publicApi" : "import.progress";
            context.SetProgress(new LocalizedText(progressKey, _index + 1, _targets.Count, package.displayName));
            context.Save();

            if (_isPublicApiImport)
            {
                // 6000.6 에서 AssetDatabase.ImportPackage 가 Package.Import 로 대체됐다(이전 버전에는 새 API가 없음).
#if UNITY_6000_6_OR_NEWER
                UnityEditor.AssetPackage.Package.Import(path, false);
#else
                AssetDatabase.ImportPackage(path, false);
#endif
            }
            else
            {
                context.Bridge.Install(productId);
            }
        }

        protected override void OnAbort(ApplyContext context)
        {
            Unsubscribe();
            int firstRemainingIndex = _index;
            if (_isImporting)
            {
                // 시작한 임포트는 취소할 수 없어 Unity가 마칠 수 있다.
                context.Report(_targets[_index], StepNameKey, ApplyOutcome.Unverified, new LocalizedText("import.cancelledInFlight"));
                _isImporting = false;
                firstRemainingIndex++;
            }
            ReportAll(_targets.Skip(firstRemainingIndex), context, ApplyOutcome.Skipped, new LocalizedText("apply.cancelled"));
        }

        public override LocalizedText GetActivityText(PackageEntry package)
        {
            return _isImporting && _targets[_index].HasKey(package.type, package.id) ? new LocalizedText("applyItem.activity.importing") : null;
        }

        // 임포트 기록은 에디터 세션 메모리에만 있어, 새 세션(에디터 재시작)에서는 먼저 채워야 "이미 임포트됨"을 판정할 수 있다.
        // 전체 스캔 멤버가 없는 버전(6000.0~6000.4)에서는 PM이 가진 기록만으로 판정한다(적용 시작 시 누락으로 보고됨).
        private void FillImportedPackages(ApplyContext context)
        {
            if (context.Bridge.IsAvailable(AssetStoreFeature.ImportedInfo))
            {
                context.Bridge.TryFillImportedPackages();
            }
        }

        // 완료 이벤트 없이 끝난 임포트를 패키지 에셋이 프로젝트에 있는지로 판정한다.
        // GUIDToAssetPath 는 지운 에셋의 경로도 돌려주므로(실측) AssetPathExists 로 실제 존재를 본다.
        private void CompleteByAssetPresence(LocalizedText reason)
        {
            if (!AssetStorePackageFileReader.TryReadAssetGuids(_importingPackagePath, out List<string> assetGuids, out LocalizedText error))
            {
                CompleteCurrent(ApplyOutcome.Unverified, new LocalizedText("import.unreadable", error, reason));
                return;
            }
            if (assetGuids.Count == 0)
            {
                CompleteCurrent(ApplyOutcome.Unverified, new LocalizedText("import.noAssetList", reason));
                return;
            }

            int presentCount = assetGuids.Count(IsAssetInProject);
            if (presentCount == assetGuids.Count)
            {
                CompleteCurrent(ApplyOutcome.Success, new LocalizedText("import.allAssetsPresent", presentCount, reason));
            }
            else if (presentCount == 0)
            {
                CompleteCurrent(ApplyOutcome.Failed, new LocalizedText("import.noAssetsPresent", assetGuids.Count, reason));
            }
            else
            {
                CompleteCurrent(ApplyOutcome.Unverified, new LocalizedText("import.someAssetsPresent", presentCount, assetGuids.Count, reason));
            }
        }

        private bool IsAssetInProject(string guid)
        {
            // 프로젝트에 없는 GUID 는 빈 문자열을 돌려준다.
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            return !string.IsNullOrEmpty(assetPath) && AssetDatabase.AssetPathExists(assetPath);
        }

        // message 는 사유가 없으면 null
        private void CompleteCurrent(ApplyOutcome outcome, LocalizedText message)
        {
            if (_isReimport && outcome == ApplyOutcome.Success)
            {
                message = new LocalizedText("import.reimported");
            }
            if (_isPublicApiImport && outcome == ApplyOutcome.Success)
            {
                var publicApiNotice = new LocalizedText("import.publicApiImported");
                message = message == null ? publicApiNotice : new LocalizedText("common.joined", message, publicApiNotice);
            }
            _isImporting = false;
            MoveNext(outcome, message);
        }

        private void MoveNext(ApplyOutcome outcome, LocalizedText message)
        {
            var package = _targets[_index];
            _index++;
            _context.Report(package, StepNameKey, outcome, message);
            _context.Save();
        }

        private void Subscribe(ApplyContext context)
        {
            _context = context;
            if (_isSubscribed)
            {
                return;
            }
            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageFailed += OnImportFailed;
            AssetDatabase.importPackageCancelled += OnImportCancelled;
            _isSubscribed = true;
        }

        private void Unsubscribe()
        {
            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageFailed -= OnImportFailed;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            _isSubscribed = false;
        }

        private void OnImportCompleted(string packageName)
        {
            if (IsCurrentImport(packageName))
            {
                CompleteCurrent(ApplyOutcome.Success, null);
            }
        }

        private void OnImportFailed(string packageName, string errorMessage)
        {
            if (IsCurrentImport(packageName))
            {
                CompleteCurrent(ApplyOutcome.Failed, new RawText(errorMessage));
            }
        }

        private void OnImportCancelled(string packageName)
        {
            if (IsCurrentImport(packageName))
            {
                CompleteCurrent(ApplyOutcome.Failed, new LocalizedText("import.cancelled"));
            }
        }

        // 임포트 완료 이벤트는 상품 제목("Wingman - Your Inspector’s Best Friend")을, 캐시 파일 이름은 특수문자를 뺀
        // 제목("Wingman - Your Inspectors Best Friend")을 쓰므로 문자·숫자만 남겨 비교한다(6000.4 실측).
        private bool IsCurrentImport(string packageName)
        {
            return _isImporting && GetComparableName(packageName) == GetComparableName(Path.GetFileNameWithoutExtension(_importingPackagePath));
        }

        private string GetComparableName(string packageName)
        {
            return new string(packageName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }
    }
}
