using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    // 목록 수집(갱신)·설치된 패키지 등록·프리셋 적용·추가할 git URL 확인의 퍼사드. 외부(UI)에는 시작·중단 API와 상태 이벤트만 제공하고,
    // 실제 처리는 수집 소스(PackageSourceBase), 설치 판정(InstalledPackageScanner), 목록 설치 상태 배지(InstallStateScanner), 적용 단계(ApplyStepBase), git URL 확인(GitPackageProbe)이 한다.
    // 적용 상태는 Library 파일에 저장되어 도메인 리로드·에디터 재시작 뒤 PackageSetupBootstrap 이 이어서 진행한다.
    // 수집·적용은 패키지 설치·임포트로 컴파일과 리로드를 일으키므로 Play Mode에서는 시작하지 않고, 진행 중에는 Play Mode 진입을 막는다.
    [FilePath(STATE_FILE_PATH, FilePathAttribute.Location.ProjectFolder)]
    public class PackageSetupSystem : ScriptableSingleton<PackageSetupSystem>
    {
        private const int CollectTimeoutMinutes = 5;

        public event Action OnStateChanged;
        public event Action OnCollectFinished;
        // 설치된 패키지 검사가 끝났을 때. 등록 대상 프리셋을 읽지 못하면 부르지 않는다.
        public event Action<InstalledScanResult> OnInstalledScanFinished;
        public event Action OnRegisterInstalledFinished;
        // 설치 상태 확인이 끝났을 때. 확인에 실패하면 부르지 않는다.
        public event Action<InstallCheckResult> OnInstallCheckFinished;
        // 추가할 git URL 확인이 끝났을 때(확인한 URL, 결과)
        public event Action<string, GitUrlCheckResult> OnGitUrlCheckFinished;
        // 도구 제거에 실패했을 때(사유). 성공하면 도메인 리로드로 도구 코드가 사라지므로 알림이 없다.
        public event Action<LocalizedText> OnToolRemovalFailed;
        // 현재 프로젝트의 설치 상태 판정이 끝났을 때(GetInstallState 결과가 바뀌었을 수 있음). 판정에 실패하면 부르지 않는다.
        public event Action OnInstallStatesChanged;

        [SerializeField] private PackageSetupState _state;
        [SerializeField] private string _presetName;
        // 프리셋 적용 진행 오버레이의 진행 문구. 목록 갱신·등록 결과, 중단·확인 실패 알림의 첫 줄로도 쓴다.
        // 언어를 바꾸면 다시 그리도록 문장 대신 LocalizedText 로 저장한다.
        [SerializeReference] private LocalizedText _progressMessage;
        [SerializeField] private bool _isCancelRequested;
        [SerializeField] private int _stepIndex;
        [SerializeReference] private List<ApplyStepBase> _steps = new();
        [SerializeField] private List<ApplyItemRecord> _applyRecords = new();
        // 이번 적용의 전체 항목(제외한 항목 포함, 실행 순서). 항목별 진행 표시에 쓴다.
        [SerializeField] private List<PackageEntry> _applyTargets = new();
        [SerializeReference] private List<LocalizedText> _resultMessages = new();
        // 창이 아직 보여주지 않은 알림 줄(목록 갱신·등록 결과, 확인 실패, 리로드로 중단). 창이 닫혀 있거나 리로드 중에 생겨도 다음에 보여주도록 저장한다.
        [SerializeReference] private List<LocalizedText> _notices = new();

        // 도메인 리로드로 사라지는 값
        private AssetStoreInternalBridge _bridge;
        private ApplyContext _applyContext;
        private List<PackageSourceBase> _sources;
        private InstalledPackageScanner _installedScanner;
        private string _registerPresetName;
        private InstallStatusChecker _installChecker;
        private PackagePresetData _checkPreset;
        private GitPackageProbe _gitUrlProbe;
        private string _checkingGitUrl;
        private RemoveRequest _toolRemoveRequest;
        // 메인 목록 설치 상태 배지의 판정. 마지막으로 성공한 판정(없으면 null)과 진행 중인 판정(없으면 null)
        private InstallStateScanner _installStates;
        private InstallStateScanner _installStateScanner;
        // 판정 중에 다시 요청됐으면 끝난 뒤 한 번 더 판정한다.
        private bool _isInstallStateRefreshPending;
        // 목록 수집·설치된 패키지 등록·설치 상태 확인의 시간 상한
        private DateTime _collectDeadline;
        private bool _isUpdateRegistered;

        public PackageSetupState State => _state;
        public bool IsBusy => _state != PackageSetupState.Idle;
        // Play Mode 이거나 진입하는 중
        public bool IsPlayModeActive => EditorApplication.isPlayingOrWillChangePlaymode;
        // 진행 문구. 아직 없으면 null
        public LocalizedText ProgressMessage => _progressMessage;
        // 프리셋 적용 중 안내(내부 API가 없어 대체 경로로 진행하는 기능)
        public IReadOnlyList<LocalizedText> Messages => _resultMessages;

        private AssetStoreInternalBridge Bridge => _bridge ??= new AssetStoreInternalBridge();

        private ApplyContext ApplyContext => _applyContext ??= new ApplyContext(
            Bridge,
            new LocalPackageLocator(Bridge),
            AddRecord,
            SetProgress,
            () => Save(true));

        // 카탈로그에 항목을 추가·갱신만 한다. 프리셋은 바꾸지 않는다.
        public void StartCollect()
        {
            ValidateCanStart();

            // 소스는 TypeCache 로 찾는다. 새 소스는 파생 클래스만 추가하면 되고 이 목록을 고치지 않는다.
            var sources = TypeCache.GetTypesDerivedFrom<PackageSourceBase>()
                .Where(x => !x.IsAbstract)
                .Select(x => (PackageSourceBase)Activator.CreateInstance(x))
                .OrderBy(x => x.Type)
                .ToList();
            var context = new CollectContext(Bridge);
            foreach (var source in sources)
            {
                source.Begin(context);
            }

            _sources = sources;
            _collectDeadline = DateTime.UtcNow.AddMinutes(CollectTimeoutMinutes);
            ClearApplyHistory();
            EnterBusyState(PackageSetupState.Collecting);
        }

        // 현재 프로젝트에 설치된 패키지를 찾는다. 끝나면 OnInstalledScanFinished 로 알리고, 고른 항목은 RegisterInstalled 로 등록한다.
        // 검사만으로는 프리셋·카탈로그를 바꾸지 않는다.
        public void StartRegisterInstalled(PackagePresetData preset)
        {
            ValidateCanStart();

            CatalogStore.Load(out var catalog);
            var scanner = new InstalledPackageScanner(Bridge);
            scanner.Begin(catalog.GetEntries(PackageType.MyAssets));

            _installedScanner = scanner;
            _registerPresetName = preset.name;
            _collectDeadline = DateTime.UtcNow.AddMinutes(CollectTimeoutMinutes);
            ClearApplyHistory();
            EnterBusyState(PackageSetupState.RegisteringInstalled);
        }

        // 검사 결과에서 고른 항목을 프리셋에 체크한다(추가만, 이미 등록된 항목은 그대로). 고른 Registry·Git 항목은 카탈로그에도 추가·갱신한다.
        // 다른 에디터가 그 사이 바꿨을 수 있어 공용 폴더의 프리셋·카탈로그 최신본에 적용한다.
        public void RegisterInstalled(InstalledScanResult scanResult, IReadOnlyList<PackageEntry> selectedPackages)
        {
            ValidateCanStart();

            string presetName = scanResult.Preset.name;
            var addedCounts = new Dictionary<PackageType, int>();
            LocalizedText saveError = CatalogStore.Update(catalog =>
            {
                foreach (var entry in selectedPackages)
                {
                    // 검색 결과에 없는 패키지(실험 패키지 등)·처음 보는 git 패키지도 목록에 남도록 카탈로그에 넣는다.
                    catalog.AddOrUpdate(entry);
                    if (scanResult.Descriptions.TryGetValue(entry, out string description))
                    {
                        catalog.SetDescription(entry.type, entry.id, description);
                    }
                }
            }) ?? PresetStore.Update(presetName, preset =>
            {
                foreach (var entry in selectedPackages.Where(x => !preset.Contains(x.type, x.id)))
                {
                    preset.SetIncluded(entry, true);
                    addedCounts[entry.type] = addedCounts.GetValueOrDefault(entry.type) + 1;
                }
            });

            var results = new List<LocalizedText>(scanResult.Notices);
            // 저장하지 못했으면 추가 건수가 의미 없어 저장 실패만 알린다.
            if (saveError == null)
            {
                foreach (var pair in scanResult.Installed.OrderBy(x => x.Key))
                {
                    int registeredCount = pair.Value.Count(scanResult.IsRegistered);
                    int selectedCount = selectedPackages.Count(x => x.type == pair.Key);
                    results.Add(new LocalizedText("register.result",
                        pair.Key,
                        addedCounts.GetValueOrDefault(pair.Key),
                        registeredCount,
                        pair.Value.Count - registeredCount - selectedCount,
                        scanResult.ExcludedCounts.GetValueOrDefault(pair.Key)));
                }
            }
            else
            {
                results.Add(saveError);
            }

            _progressMessage = saveError == null
                ? new LocalizedText("progress.registerFinished", presetName, addedCounts.Values.Sum())
                : new LocalizedText("progress.registerSaveFailed", presetName);
            NotifyFinished(results, scanResult.HasFailure || saveError != null);

            OnRegisterInstalledFinished?.Invoke();
            NotifyStateChanged();
        }

        // 프리셋 적용 전에 항목마다 이미 설치·임포트됐는지, 업데이트가 있는지 확인한다. 끝나면 OnInstallCheckFinished 로 알린다.
        public void StartInstallCheck(PackagePresetData preset)
        {
            ValidateCanStart();

            // 확인하는 동안 창에서 체크를 바꾸거나 다시 읽어도 확인한 목록과 적용할 목록이 같도록 복사본으로 진행한다.
            var presetCopy = new PackagePresetData { name = preset.name, packages = preset.packages.Select(x => x.Clone()).ToList() };
            var checker = new InstallStatusChecker(Bridge);
            checker.Begin(presetCopy.packages);

            _installChecker = checker;
            _checkPreset = presetCopy;
            _collectDeadline = DateTime.UtcNow.AddMinutes(CollectTimeoutMinutes);
            EnterBusyState(PackageSetupState.CheckingInstalled);
        }

        // 추가할 git URL 로 UPM 패키지를 설치할 수 있는지 설치하지 않고 확인한다. 끝나면 OnGitUrlCheckFinished 로 알린다.
        public void StartGitUrlCheck(string url)
        {
            ValidateCanStart();

            var probe = new GitPackageProbe(url);
            probe.Begin();

            // 형식 오류처럼 바로 끝난 결과는 다음 Tick 에서 알린다.
            _gitUrlProbe = probe;
            _checkingGitUrl = url;
            EnterBusyState(PackageSetupState.CheckingGitUrl);
            Save(true);
        }

        // 단계 순서: UPM 설치 → Asset Store 다운로드 → Asset Store 임포트
        // checkResult: 적용 직전의 설치 상태 확인 결과. 그 프리셋(복사본)을 적용하고, 로컬 파일이 최신이 아닌 구매 에셋은 최신을 다시 받는다.
        // excludedPackages: 이미 설치돼 이번 적용에서 뺄 항목. 프리셋은 바꾸지 않는다.
        // reinstallPackages: 이미 설치됐지만 다시 설치(UPM 재요청)·임포트할 항목
        public void StartApply(InstallCheckResult checkResult, IReadOnlyList<PackageEntry> excludedPackages, IReadOnlyList<PackageEntry> reinstallPackages)
        {
            ValidateCanStart();

            var preset = checkResult.Preset;

            bool IsExcluded(PackageEntry entry) => excludedPackages.Any(x => x.HasKey(entry.type, entry.id));
            // UPM 항목은 하나씩 설치하므로 적용 확인 창에 보인 순서(종류 순 → 표시명 순)로 진행한다.
            var upmPackages = preset.packages
                .Where(x => x.type == PackageType.UnityRegistry || x.type == PackageType.Git)
                .Where(x => !IsExcluded(x))
                .OrderBy(x => x.type)
                .ThenBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Clone())
                .ToList();
            // 구매 에셋도 하나씩 임포트하므로 진행 목록에 보이는 순서(표시명 순)로 진행한다.
            var assetStorePackages = preset.GetPackages(PackageType.MyAssets)
                .Where(x => !IsExcluded(x))
                .OrderBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var reinstallClones = reinstallPackages.Select(x => x.Clone()).ToList();

            // 로컬 패키지 조회 결과는 실행마다 새로 읽는다.
            _applyContext = null;
            ClearApplyHistory();
            // 단계 실행 순서(UPM: Registry → Git, 그다음 Asset Store)로 보여준다. 제외한 항목도 제자리에 둔다.
            _applyTargets = preset.packages
                .OrderBy(x => x.type == PackageType.MyAssets)
                .ThenBy(x => x.type)
                .ThenBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Clone())
                .ToList();
            // 브리지를 처음 만드는 보고를 상태 변경 전에 해서, 여기서 예외가 나도 Applying 상태로 남지 않게 한다.
            if (assetStorePackages.Count > 0)
            {
                ReportAssetStoreFallbacks();
            }
            var upmStep = new UpmInstallStep(upmPackages, reinstallClones.FindAll(x => x.type != PackageType.MyAssets));
            var importStep = new AssetStoreImportStep(assetStorePackages.Select(x => x.Clone()).ToList(), reinstallClones.FindAll(x => x.type == PackageType.MyAssets), checkResult.OutdatedLocalUploadIds);
            _steps = new List<ApplyStepBase>
            {
                upmStep,
                new AssetStoreDownloadStep(assetStorePackages.Select(x => x.Clone()).ToList(), checkResult.OutdatedLocalUploadIds),
                importStep,
            };
            // 제외한 항목은 단계에 넘기지 않고, 그 항목을 맡았을 단계의 건너뜀으로 바로 기록한다.
            foreach (var package in excludedPackages)
            {
                string stepNameKey = package.type == PackageType.MyAssets ? importStep.StepNameKey : upmStep.StepNameKey;
                _applyRecords.Add(new ApplyItemRecord(package, stepNameKey, ApplyOutcome.Skipped, new LocalizedText("apply.excluded")));
            }
            _presetName = preset.name;
            _isCancelRequested = false;
            _stepIndex = 0;
            _progressMessage = new LocalizedText("progress.applyStarted", _presetName);
            EnterBusyState(PackageSetupState.Applying);
            Save(true);
        }

        // 마지막 프리셋 적용의 항목별 진행 상태(실행 순서). 적용한 적이 없거나 이후 목록 갱신·등록을 시작했으면 빈 목록
        // 항목을 맡는 마지막 단계(UPM 설치 또는 Asset Store 임포트)의 결과가 기록되면 완료, 진행 중인 단계가 처리 중이면 처리 중, 그 밖에는 대기다.
        public List<ApplyItemProgress> GetApplyProgress()
        {
            var activeStep = _state == PackageSetupState.Applying && _steps[_stepIndex].IsStarted && !_steps[_stepIndex].IsFinished
                ? _steps[_stepIndex]
                : null;
            var progress = new List<ApplyItemProgress>();
            foreach (var package in _applyTargets)
            {
                var records = _applyRecords.FindAll(x => x.package.HasKey(package.type, package.id));
                // 제외한 항목은 어느 단계에도 없고 시작할 때 기록된다.
                var finalStep = _steps.LastOrDefault(x => x.Targets.Any(t => t.HasKey(package.type, package.id)));
                bool isDone = finalStep == null ? records.Count > 0 : records.Exists(x => x.stepNameKey == finalStep.StepNameKey);
                if (isDone)
                {
                    progress.Add(new ApplyItemProgress(package, ApplyItemState.Done, records, null));
                    continue;
                }
                LocalizedText activityText = activeStep?.GetActivityText(package);
                if (activityText != null)
                {
                    progress.Add(new ApplyItemProgress(package, ApplyItemState.InProgress, records, activityText));
                    continue;
                }
                progress.Add(new ApplyItemProgress(package, ApplyItemState.Waiting, records, activeStep?.GetWaitingText(package) ?? GetNextStepWaitingText(package, records)));
            }
            return progress;
        }

        // 끝난 프리셋 적용의 진행 기록(대상·결과·안내)을 지운다. 결과는 끝날 때 콘솔 로그로 남아 있다.
        public void ClearApplyProgress()
        {
            if (_state == PackageSetupState.Applying)
            {
                throw new InvalidOperationException(LocalizationManager.Get("error.alreadyBusy", _state));
            }
            ClearApplyHistory();
            Save(true);
            NotifyStateChanged();
        }

        // 마지막 프리셋 적용이 중단 없이 끝났고 모든 항목이 설치·임포트됐거나 정상적으로 건너뛰었는지(이미 있음·사용자가 제외).
        // 실패·확인 필요·최신을 받지 못해 임포트를 건너뛴 항목이 있거나 적용 기록이 없으면 false
        public bool IsLastApplyFullyCompleted()
        {
            if (_state == PackageSetupState.Applying || _isCancelRequested || _applyTargets.Count == 0)
            {
                return false;
            }
            var lastRecords = new Dictionary<(PackageType, string), ApplyItemRecord>();
            foreach (var record in _applyRecords)
            {
                lastRecords[(record.package.type, record.package.id)] = record;
            }
            return _applyTargets.All(x => lastRecords.TryGetValue((x.type, x.id), out var record) && IsCompletedOutcome(record));
        }

        // 이 도구를 프로젝트에서 뺀다. Assets(.unitypackage)·Embedded 는 도구 폴더를 OS 휴지통으로 보내고, 그 밖의 UPM 패키지(git URL 등)는 manifest 에서 뺀다.
        // 도구의 Library 상태도 지운다. 공용 폴더 데이터와 EditorPrefs 는 다른 프로젝트도 쓰므로 남긴다.
        // 성공하면 도메인 리로드로 도구 코드가 사라진다. 실패하면 OnToolRemovalFailed 로 알린다.
        public void RemoveTool()
        {
            ValidateCanStart();

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(GetType().Assembly);
            DeleteStateFolder();
            if (packageInfo == null || packageInfo.source == PackageSource.Embedded)
            {
                string folderPath = packageInfo == null ? ResourceLoader.ToolRootPath : packageInfo.assetPath;
                Debug.Log($"[Package Setup] {LocalizationManager.Get("log.removeToolStarted", folderPath)}");
                if (!AssetDatabase.MoveAssetToTrash(folderPath))
                {
                    FailToolRemoval(new LocalizedText("removeTool.trashFailed", folderPath));
                }
                return;
            }

            Debug.Log($"[Package Setup] {LocalizationManager.Get("log.removeToolStarted", packageInfo.name)}");
            _toolRemoveRequest = Client.Remove(packageInfo.name);
            EnterBusyState(PackageSetupState.RemovingTool);
        }

        // 아직 보여주지 않은 알림 줄을 꺼낸다. 꺼낸 줄은 다시 돌려주지 않는다.
        public List<LocalizedText> TakeNotices()
        {
            var notices = new List<LocalizedText>(_notices);
            if (notices.Count > 0)
            {
                _notices.Clear();
                Save(true);
            }
            return notices;
        }

        // 프리셋 적용을 처리 중인 항목을 기다리지 않고 바로 끝낸다. 진행 중인 단계는 받는 중인 다운로드를 취소하고 결과가 없는 대상을 기록하며,
        // 시작하지 않은 단계의 대상은 건너뜀으로 기록한다. 이미 Unity에 보낸 UPM 설치·임포트와 그에 따른 컴파일은 취소할 API가 없어
        // Unity가 끝까지 진행할 수 있다(그 항목은 확인 필요로 기록). 끝난 뒤의 리로드에서는 이어서 진행하지 않는다.
        public void Cancel()
        {
            if (_state != PackageSetupState.Applying)
            {
                throw new InvalidOperationException($"Not applying: {_state}");
            }

            _isCancelRequested = true;
            // 단계는 끝나면 같은 Tick 에서 다음 단계로 넘어가므로, 현재 단계는 시작 전이거나 진행 중이다.
            if (_steps[_stepIndex].IsStarted)
            {
                _steps[_stepIndex].Abort(ApplyContext);
            }
            foreach (var step in _steps.Where(x => !x.IsStarted))
            {
                foreach (var package in step.Targets)
                {
                    _applyRecords.Add(new ApplyItemRecord(package, step.StepNameKey, ApplyOutcome.Skipped, new LocalizedText("apply.cancelled")));
                }
            }
            FinishApply();
        }

        // 상세 보기를 할 수 없는 이유. 할 수 있으면 null
        public LocalizedText GetDetailsUnavailableReason(PackageEntry entry)
        {
            return entry.type switch
            {
                PackageType.UnityRegistry => null,
                PackageType.MyAssets => Bridge.IsAvailable(AssetStoreFeature.SelectInMyAssets) ? null : Bridge.GetMissingMessage(AssetStoreFeature.SelectInMyAssets),
                PackageType.Git => GetGitWebUrl(entry.id) != null ? null : new LocalizedText("details.gitUrlNotWeb", entry.id),
                _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.type, null),
            };
        }

        // Registry 는 Package Manager에서, Asset Store 는 Package Manager의 My Assets 에서 선택해 상세를 보여준다.
        // Git 은 Package Manager가 설치된 패키지만 보여주므로 저장소 주소를 브라우저로 연다.
        public void ShowDetails(PackageEntry entry)
        {
            switch (entry.type)
            {
                case PackageType.UnityRegistry:
                    UnityEditor.PackageManager.UI.Window.Open(entry.id);
                    break;
                case PackageType.MyAssets:
                    Bridge.SelectInMyAssets(long.Parse(entry.id));
                    break;
                case PackageType.Git:
                    Application.OpenURL(GetGitWebUrl(entry.id) ?? throw new InvalidOperationException($"Not a web URL: {entry.id}"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(entry), entry.type, null);
            }
        }

        // 진행 줄에 보여줄 현재 작업과 세부 진행. 작업이 없거나 프리셋 적용 중이면 null(적용은 진행 오버레이가 보여준다).
        // 저장하지 않고 매번 계산하므로 표시하는 시점의 언어로 만든다.
        // 리로드 직후 부트스트랩이 중단 처리하기 전(첫 update 전)에는 진행 객체가 없어 세부 진행 없이 작업만 보여준다.
        public string GetBusyProgressText()
        {
            var details = new List<LocalizedText>();
            switch (_state)
            {
                case PackageSetupState.Idle:
                case PackageSetupState.Applying:
                    return null;
                case PackageSetupState.Collecting:
                    if (_sources != null)
                    {
                        details.AddRange(_sources.Select(GetSourceProgressText));
                    }
                    return FormatBusyText("busy.collecting", details);
                case PackageSetupState.RegisteringInstalled:
                    return FormatBusyText("busy.registering", details);
                case PackageSetupState.CheckingInstalled:
                    if (_installChecker != null)
                    {
                        details.AddRange(_installChecker.GetProgressTexts());
                    }
                    return FormatBusyText("busy.checkingInstalled", details);
                case PackageSetupState.CheckingGitUrl:
                    if (_gitUrlProbe != null)
                    {
                        details.Add(new LocalizedText(GetGitProbePhaseKey(_gitUrlProbe.Phase)));
                    }
                    return FormatBusyText("busy.checkingGitUrl", details);
                case PackageSetupState.RemovingTool:
                    return FormatBusyText("busy.removingTool", details);
                default:
                    throw new ArgumentOutOfRangeException(nameof(_state), _state, null);
            }
        }

        // 현재 프로젝트의 설치 상태를 다시 판정한다. 끝나면 OnInstallStatesChanged 로 알린다.
        // 적용 중에는 설치 요청과 겹치지 않도록 하지 않고, 적용이 끝날 때 다시 판정한다.
        public void RefreshInstallStates()
        {
            if (_state == PackageSetupState.Applying)
            {
                return;
            }
            if (_installStateScanner != null)
            {
                _isInstallStateRefreshPending = true;
                return;
            }
            _installStateScanner = new InstallStateScanner(Bridge);
            _installStateScanner.Begin();
            EditorApplication.update += TickInstallStateScan;
        }

        // 판정한 적이 없거나 판정이 모두 실패했으면 설치되지 않은 것으로 본다(배지 없음).
        // version: 설치된 UPM 패키지의 버전(git 은 커밋 7자 포함). Asset Store·미설치는 null
        public (PackageInstallState state, string version) GetInstallState(PackageEntry entry)
        {
            return _installStates?.GetState(entry) ?? (PackageInstallState.NotInstalled, null);
        }

        // 도메인 리로드·에디터 재시작 뒤 PackageSetupBootstrap 이 호출한다.
        public void ResumeAfterReload()
        {
            string interruptedKey = _state switch
            {
                PackageSetupState.Collecting => "progress.collectInterrupted",
                PackageSetupState.RegisteringInstalled => "progress.registerInterrupted",
                PackageSetupState.CheckingInstalled => "progress.installCheckInterrupted",
                PackageSetupState.CheckingGitUrl => "progress.gitUrlCheckInterrupted",
                PackageSetupState.RemovingTool => "progress.removeToolInterrupted",
                _ => null,
            };
            if (interruptedKey != null)
            {
                _state = PackageSetupState.Idle;
                _progressMessage = new LocalizedText(interruptedKey);
                _notices.Add(_progressMessage);
                Save(true);
                Debug.LogWarning($"[Package Setup] {_progressMessage.GetText()}");
                NotifyStateChanged();
                return;
            }
            if (_state != PackageSetupState.Applying)
            {
                return;
            }

            // 단계 복구 중 예외가 나도 진행이 멈춰 있지 않도록 Tick 을 먼저 등록한다. 복구하지 못한 대기는 각 단계의 시간 상한으로 정리된다.
            RegisterUpdate();
            var step = _steps[_stepIndex];
            Debug.Log($"[Package Setup] {LocalizationManager.Get("log.applyResumed", _presetName, LocalizationManager.Get(step.StepNameKey))}");
            if (step.IsStarted && !step.IsFinished)
            {
                step.OnResume(ApplyContext);
            }
            NotifyStateChanged();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            UnregisterUpdate();
            EditorApplication.update -= TickInstallStateScan;
            // 도메인 리로드로 확인 결과를 받을 수 없게 되므로 git 프로세스를 끝내고 임시 폴더를 지운다.
            _gitUrlProbe?.Stop();
        }

        private void ValidateCanStart()
        {
            if (IsBusy)
            {
                throw new InvalidOperationException(LocalizationManager.Get("error.alreadyBusy", _state));
            }
            if (IsPlayModeActive)
            {
                throw new InvalidOperationException(LocalizationManager.Get("error.playModeStart"));
            }
        }

        private LocalizedText GetSourceProgressText(PackageSourceBase source)
        {
            if (!source.IsDone)
            {
                return source.GetProgressText();
            }
            return new LocalizedText(source.Error == null ? "busy.source.done" : "busy.source.failed", source.Type);
        }

        private string GetGitProbePhaseKey(GitProbePhase phase) => phase switch
        {
            GitProbePhase.Clone or GitProbePhase.Init or GitProbePhase.Fetch => "busy.git.download",
            GitProbePhase.ListTree => "busy.git.listTree",
            GitProbePhase.ReadManifest => "busy.git.readManifest",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
        };

        // '작업 문구 세부1 · 세부2'
        private string FormatBusyText(string titleKey, List<LocalizedText> details)
        {
            string title = LocalizationManager.Get(titleKey);
            return details.Count == 0 ? title : $"{title} {string.Join(" · ", details.Select(x => x.GetText()))}";
        }

        private void TickInstallStateScan()
        {
            _installStateScanner.Tick();
            if (!_installStateScanner.IsDone)
            {
                return;
            }
            EditorApplication.update -= TickInstallStateScan;
            var scanner = _installStateScanner;
            _installStateScanner = null;
            if (_isInstallStateRefreshPending)
            {
                _isInstallStateRefreshPending = false;
                RefreshInstallStates();
            }
            // 실패하면 이전 판정의 배지를 그대로 둔다.
            if (scanner.Error != null)
            {
                Debug.LogWarning($"[Package Setup] {LocalizationManager.Get("log.installStateFailed", scanner.Error.GetText())}");
                return;
            }
            _installStates = scanner;
            OnInstallStatesChanged?.Invoke();
        }

        // 진입 리로드가 진행 중인 요청·이벤트 구독을 끊고, 수집은 중단되며, 임포트 완료 이벤트도 잃게 된다.
        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode || !IsBusy)
            {
                return;
            }
            EditorApplication.isPlaying = false;
            string key = _state switch
            {
                PackageSetupState.Collecting => "log.playModeBlockedByCollect",
                PackageSetupState.RegisteringInstalled => "log.playModeBlockedByRegister",
                PackageSetupState.CheckingInstalled => "log.playModeBlockedByInstallCheck",
                PackageSetupState.CheckingGitUrl => "log.playModeBlockedByGitUrlCheck",
                PackageSetupState.RemovingTool => "log.playModeBlockedByRemoveTool",
                _ => "log.playModeBlockedByApply",
            };
            Debug.LogWarning($"[Package Setup] {LocalizationManager.Get(key)}");
        }

        private void Tick()
        {
            // 패키지 해석·임포트·컴파일 중에는 기다린다.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            if (_state == PackageSetupState.Collecting)
            {
                TickCollect();
            }
            else if (_state == PackageSetupState.RegisteringInstalled)
            {
                TickRegisterInstalled();
            }
            else if (_state == PackageSetupState.Applying)
            {
                TickApply();
            }
            else if (_state == PackageSetupState.CheckingInstalled)
            {
                TickInstallCheck();
            }
            else if (_state == PackageSetupState.CheckingGitUrl)
            {
                TickGitUrlCheck();
            }
            else if (_state == PackageSetupState.RemovingTool)
            {
                TickRemoveTool();
            }
        }

        private void TickCollect()
        {
            foreach (var source in _sources.Where(x => !x.IsDone))
            {
                source.Tick();
            }
            bool isTimedOut = DateTime.UtcNow > _collectDeadline;
            if (!isTimedOut && _sources.Any(x => !x.IsDone))
            {
                return;
            }

            // 수집하는 동안 다른 에디터가 카탈로그를 바꿨을 수 있어 최신본에 적용한다.
            var completedSources = _sources.Where(x => x.IsDone && x.Error == null).ToList();
            var addedCounts = new Dictionary<PackageType, int>();
            LocalizedText saveError = CatalogStore.Update(catalog =>
            {
                foreach (var source in completedSources)
                {
                    int addedCount = 0;
                    foreach (var entry in source.Entries)
                    {
                        if (catalog.AddOrUpdate(entry))
                        {
                            addedCount++;
                        }
                        if (source.Descriptions.TryGetValue(entry.id, out string description))
                        {
                            catalog.SetDescription(entry.type, entry.id, description);
                        }
                    }
                    addedCounts[source.Type] = addedCount;
                }
            });
            var results = new List<LocalizedText>();
            bool hasFailure = saveError != null;
            foreach (var source in _sources)
            {
                if (!source.IsDone)
                {
                    results.Add(new LocalizedText("collect.timeout", source.Type, CollectTimeoutMinutes));
                    hasFailure = true;
                    continue;
                }
                if (source.Error != null)
                {
                    results.Add(new LocalizedText("collect.failed", source.Type, source.Error));
                    hasFailure = true;
                    continue;
                }
                // 저장하지 못했으면 추가 건수가 의미 없어 저장 실패만 알린다.
                if (saveError != null)
                {
                    continue;
                }
                results.Add(new LocalizedText("collect.result", source.Type, source.Entries.Count, addedCounts[source.Type]));
                results.AddRange(source.Notices);
            }
            if (saveError != null)
            {
                results.Add(saveError);
            }

            _state = PackageSetupState.Idle;
            _progressMessage = new LocalizedText("progress.collectFinished");
            _sources = null;
            UnregisterUpdate();
            NotifyFinished(results, hasFailure);

            OnCollectFinished?.Invoke();
            NotifyStateChanged();
        }

        private void TickRegisterInstalled()
        {
            _installedScanner.Tick();
            bool isTimedOut = DateTime.UtcNow > _collectDeadline;
            if (!isTimedOut && !_installedScanner.IsDone)
            {
                return;
            }

            var scanner = _installedScanner;
            string presetName = _registerPresetName;
            _state = PackageSetupState.Idle;
            _installedScanner = null;
            _registerPresetName = null;
            UnregisterUpdate();

            // 이미 등록된 항목을 표시하도록 검사가 끝난 시점의 프리셋을 읽는다. 그 사이 지워졌거나 읽을 수 없게 됐으면 등록할 곳이 없다.
            LocalizedText loadError = PresetStore.Load(presetName, out var preset);
            if (loadError != null)
            {
                _progressMessage = new LocalizedText("progress.registerPresetLoadFailed", presetName, loadError);
                _notices.Add(_progressMessage);
                Save(true);
                Debug.LogWarning($"[Package Setup] {_progressMessage.GetText()}");
                NotifyStateChanged();
                return;
            }

            Save(true);
            NotifyStateChanged();
            OnInstalledScanFinished?.Invoke(CreateScanResult(scanner, preset));
        }

        // 판정에 실패했거나 시간 상한 안에 끝나지 않은 종류는 scanner.Installed 에 없고 그 사유를 안내에 넣는다.
        private InstalledScanResult CreateScanResult(InstalledPackageScanner scanner, PackagePresetData preset)
        {
            var notices = new List<LocalizedText>();
            bool hasFailure = false;
            foreach (PackageType type in Enum.GetValues(typeof(PackageType)))
            {
                if (scanner.Errors.TryGetValue(type, out var error))
                {
                    notices.Add(new LocalizedText("collect.failed", type, error));
                    hasFailure = true;
                }
                else if (!scanner.Installed.ContainsKey(type))
                {
                    notices.Add(new LocalizedText("collect.timeout", type, CollectTimeoutMinutes));
                    hasFailure = true;
                }
            }
            notices.AddRange(scanner.Notices);
            if (scanner.BuiltInExcludedCount > 0)
            {
                notices.Add(new LocalizedText("register.excludedBuiltIn", scanner.BuiltInExcludedCount));
            }
            if (scanner.LocalExcludedNames.Count > 0)
            {
                notices.Add(new LocalizedText("register.excludedLocal", scanner.LocalExcludedNames.Count, string.Join(", ", scanner.LocalExcludedNames)));
            }

            // 제외한 패키지는 모두 UPM 패키지라 UnityRegistry 결과에 센다.
            var excludedCounts = new Dictionary<PackageType, int>
            {
                [PackageType.UnityRegistry] = scanner.BuiltInExcludedCount + scanner.LocalExcludedNames.Count,
            };
            return new InstalledScanResult(
                preset,
                new Dictionary<PackageType, List<PackageEntry>>(scanner.Installed),
                new Dictionary<PackageEntry, string>(scanner.Descriptions),
                new Dictionary<PackageEntry, UnityEditor.PackageManager.RegistryInfo>(scanner.ScopedRegistries),
                excludedCounts,
                notices,
                hasFailure);
        }

        private void TickInstallCheck()
        {
            _installChecker.Tick();
            bool isTimedOut = DateTime.UtcNow > _collectDeadline;
            if (!isTimedOut && !_installChecker.IsDone && !_installChecker.HasFailed)
            {
                return;
            }

            var checker = _installChecker;
            var preset = _checkPreset;
            checker.StopGitQueries();
            LocalizedText error = checker.HasFailed ? checker.Error : checker.IsDone ? null : new LocalizedText("check.timeout", CollectTimeoutMinutes);
            _state = PackageSetupState.Idle;
            _installChecker = null;
            _checkPreset = null;
            UnregisterUpdate();
            if (error != null)
            {
                _progressMessage = new LocalizedText("progress.installCheckFailed", preset.name, error);
                _notices.Add(_progressMessage);
                Save(true);
                Debug.LogWarning($"[Package Setup] {_progressMessage.GetText()}");
                NotifyStateChanged();
                return;
            }

            Save(true);
            NotifyStateChanged();
            OnInstallCheckFinished?.Invoke(checker.CreateResult(preset));
        }

        // 확인은 git 프로세스마다 시간 상한이 있어 따로 상한을 두지 않는다.
        private void TickGitUrlCheck()
        {
            _gitUrlProbe.Tick();
            if (!_gitUrlProbe.IsDone)
            {
                return;
            }

            GitUrlCheckResult result = _gitUrlProbe.Result;
            string url = _checkingGitUrl;
            _state = PackageSetupState.Idle;
            _gitUrlProbe = null;
            _checkingGitUrl = null;
            UnregisterUpdate();
            Save(true);
            NotifyStateChanged();
            OnGitUrlCheckFinished?.Invoke(url, result);
        }

        // 성공하면 Package Manager 가 패키지를 빼고 도메인 리로드를 일으켜 도구 코드가 사라진다.
        private void TickRemoveTool()
        {
            if (!_toolRemoveRequest.IsCompleted)
            {
                return;
            }

            var request = _toolRemoveRequest;
            _toolRemoveRequest = null;
            _state = PackageSetupState.Idle;
            UnregisterUpdate();
            NotifyStateChanged();
            if (request.Status == StatusCode.Failure)
            {
                FailToolRemoval(new LocalizedText("removeTool.failed", request.PackageIdOrName, new RawText(request.Error.message)));
            }
        }

        private void FailToolRemoval(LocalizedText error)
        {
            Debug.LogError($"[Package Setup] {error.GetText()}");
            OnToolRemovalFailed?.Invoke(error);
        }

        // 다시 설치했을 때 예전 적용 기록·알림이 남지 않도록 지운다.
        private void DeleteStateFolder()
        {
            string folderPath = Path.GetDirectoryName(STATE_FILE_PATH);
            if (!Directory.Exists(folderPath))
            {
                return;
            }
            // 파일 입출력: 다른 프로세스가 잡고 있으면 실패할 수 있다. 상태 파일이 남아도 제거는 진행한다.
            try
            {
                Directory.Delete(folderPath, true);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[Package Setup] {LocalizationManager.Get("log.stateDeleteFailed", folderPath, e.Message)}");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"[Package Setup] {LocalizationManager.Get("log.stateDeleteFailed", folderPath, e.Message)}");
            }
        }

        // 최신을 받지 못해 임포트를 건너뛴 항목은 예전 상태로 남아 있다.
        private bool IsCompletedOutcome(ApplyItemRecord record)
        {
            return record.outcome == ApplyOutcome.Success
                   || (record.outcome == ApplyOutcome.Skipped && record.message?.HasKey("import.notLatest") != true);
        }

        private void TickApply()
        {
            var step = _steps[_stepIndex];
            if (!step.IsStarted)
            {
                step.Begin(ApplyContext);
            }
            if (!step.IsFinished)
            {
                step.Tick(ApplyContext);
            }
            if (!step.IsFinished)
            {
                return;
            }

            _stepIndex++;
            if (_stepIndex < _steps.Count)
            {
                Save(true);
                NotifyStateChanged();
                return;
            }
            FinishApply();
        }

        private void FinishApply()
        {
            List<ApplyOutcome> packageOutcomes = GetPackageOutcomes();
            int successCount = packageOutcomes.Count(x => x == ApplyOutcome.Success);
            int unverifiedCount = packageOutcomes.Count(x => x == ApplyOutcome.Unverified);
            int skippedCount = packageOutcomes.Count(x => x == ApplyOutcome.Skipped);
            int failedCount = packageOutcomes.Count(x => x == ApplyOutcome.Failed);
            string resultKey = _isCancelRequested ? "progress.applyCancelled" : "progress.applyFinished";

            _state = PackageSetupState.Idle;
            _progressMessage = new LocalizedText(resultKey, _presetName, packageOutcomes.Count, successCount, unverifiedCount, skippedCount, failedCount);
            UnregisterUpdate();
            Save(true);

            var log = new StringBuilder($"[Package Setup] {_progressMessage.GetText()}");
            foreach (var message in _resultMessages)
            {
                log.Append($"\n{message.GetText()}");
            }
            foreach (var record in _applyRecords)
            {
                log.Append($"\n[{record.GetOutcomeText()}] {record.GetStepName()} | {record.package.type} | {record.package.displayName} ({record.package.id}) {record.GetMessage()}");
            }
            if (failedCount > 0 || unverifiedCount > 0)
            {
                Debug.LogWarning(log.ToString());
            }
            else
            {
                Debug.Log(log.ToString());
            }
            NotifyStateChanged();
            // 적용 중에는 판정하지 않았으므로 바뀐 설치 상태를 다시 판정한다.
            RefreshInstallStates();
        }

        // 레코드는 패키지 × 단계 단위다. 단계는 목표(설치·임포트)를 향해 순서대로 진행되므로
        // 패키지마다 마지막으로 기록된 단계 결과를 최종 결과로 본다.
        // (결과 우선순위로 합치면 "다운로드 성공 + 임포트 중단됨"이 성공으로 잡힌다)
        private List<ApplyOutcome> GetPackageOutcomes()
        {
            var lastOutcomes = new Dictionary<(PackageType, string), ApplyOutcome>();
            foreach (var record in _applyRecords)
            {
                lastOutcomes[(record.package.type, record.package.id)] = record.outcome;
            }
            return lastOutcomes.Values.ToList();
        }

        // 앞 단계를 마치고 다음 단계를 기다리는 항목(예: 다운로드를 마치고 임포트를 기다리는 구매 에셋)의 대기 사유. 아직 처리된 단계가 없으면 null
        private LocalizedText GetNextStepWaitingText(PackageEntry package, List<ApplyItemRecord> records)
        {
            if (records.Count == 0)
            {
                return null;
            }
            int lastStepIndex = _steps.FindIndex(x => x.StepNameKey == records[records.Count - 1].stepNameKey);
            var nextStep = _steps.Skip(lastStepIndex + 1).First(x => x.Targets.Any(t => t.HasKey(package.type, package.id)));
            return new LocalizedText("applyItem.waiting.step", new LocalizedText(nextStep.StepNameKey));
        }

        // 내부 API가 없어 대체 경로로 진행하는 기능을 적용 시작 시 한 번 알린다.
        private void ReportAssetStoreFallbacks()
        {
            var features = new List<AssetStoreFeature>
            {
                AssetStoreFeature.LocalInfo,
                AssetStoreFeature.ImportedInfo,
                AssetStoreFeature.ImportedInfoScan,
                AssetStoreFeature.Install,
            };
            // 캐시 경로는 LocalInfo 를 쓸 수 없을 때만 쓴다.
            if (!Bridge.IsAvailable(AssetStoreFeature.LocalInfo))
            {
                features.Add(AssetStoreFeature.CachePath);
            }
            foreach (var feature in features.Where(x => !Bridge.IsAvailable(x)))
            {
                _resultMessages.Add(new LocalizedText("apply.fallback", feature, Bridge.GetMissingMessage(feature)));
            }
        }

        // manifest 의 git URL 을 브라우저로 열 주소로 바꾼다. "git+" 접두어만 떼고 나머지(.git, ?path=, #리비전)는 그대로 둔다.
        // GitHub 는 .git 주소를 저장소 페이지로 리다이렉트한다. ssh·file 처럼 브라우저로 열 수 없는 주소는 null
        private string GetGitWebUrl(string gitUrl)
        {
            const string gitPlusPrefix = "git+";
            string url = gitUrl.StartsWith(gitPlusPrefix, StringComparison.OrdinalIgnoreCase) ? gitUrl.Substring(gitPlusPrefix.Length) : gitUrl;
            bool isWebUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            return isWebUrl ? url : null;
        }

        // 목록 갱신·등록 결과를 알림으로 넘기고 콘솔에 남긴다. 실패·시간 초과가 있으면 경고로 남긴다.
        private void NotifyFinished(List<LocalizedText> results, bool hasFailure)
        {
            _notices.Add(_progressMessage);
            _notices.AddRange(results);
            Save(true);
            string log = $"[Package Setup] {_progressMessage.GetText()}\n{string.Join("\n", results.Select(x => x.GetText()))}";
            if (hasFailure)
            {
                Debug.LogWarning(log);
            }
            else
            {
                Debug.Log(log);
            }
        }

        private void AddRecord(ApplyItemRecord record)
        {
            _applyRecords.Add(record);
            Save(true);
            NotifyStateChanged();
        }

        // 단계가 매 확인마다 같은 문구를 보내므로 문장이 같으면 갱신하지 않는다.
        private void SetProgress(LocalizedText text)
        {
            if (_progressMessage != null && _progressMessage.GetText() == text.GetText())
            {
                return;
            }
            _progressMessage = text;
            NotifyStateChanged();
        }

        // 진행 상태로 바꾸고 Tick 을 등록한다. 시작 처리가 끝난 뒤에 불러, 시작 중 예외가 나도 진행 상태로 남지 않게 한다.
        private void EnterBusyState(PackageSetupState state)
        {
            _state = state;
            RegisterUpdate();
            NotifyStateChanged();
        }

        // 마지막 프리셋 적용의 진행 기록(대상·결과·안내). 지우면 진행 오버레이가 사라진다.
        private void ClearApplyHistory()
        {
            _applyTargets.Clear();
            _applyRecords.Clear();
            _resultMessages.Clear();
        }

        private void RegisterUpdate()
        {
            if (_isUpdateRegistered)
            {
                return;
            }
            EditorApplication.update += Tick;
            _isUpdateRegistered = true;
        }

        private void UnregisterUpdate()
        {
            EditorApplication.update -= Tick;
            _isUpdateRegistered = false;
        }

        private void NotifyStateChanged()
        {
            OnStateChanged?.Invoke();
        }
    }
}
