using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace PackageSetupTool
{
    // Unity Registry·Git 패키지를 공개 API(Client.AddAndRemove)로 하나씩 설치한다.
    // 여러 항목을 한 요청으로 보내면 하나만 실패해도 요청 전체가 거부되어(6000.6 실측) 정상 항목까지 설치되지 않기 때문이다.
    // Registry 는 이름만 넘겨 에디터 권장 버전이 설치되게 하고, 이미 프로젝트에 있는 패키지는 버전이 바뀌지 않도록 건너뛴다.
    // 단, 사용자가 재임포트로 고른 항목은 다시 요청한다(Registry 는 권장 버전으로 맞춰지고, Git 은 최신 커밋으로 갱신됨).
    // 요청이 실패한 항목은 한 바퀴를 마친 뒤 한 번 더 요청한다. 다른 Git 패키지에 의존하는 항목이 의존 대상보다 먼저 요청됐을 수 있기 때문이다.
    // UPM 요청은 취소할 수 없으므로, 응답이 없으면 요청마다 시간 상한으로 정리한다.
    [Serializable]
    public class UpmInstallStep : ApplyStepBase
    {
        private const int InstallTimeoutMinutes = 10;
        private const int ListTimeoutMinutes = 5;

        [SerializeField] private UpmInstallPhase _phase;
        [SerializeField] private List<PackageEntry> _reinstallTargets = new();
        [SerializeField] private List<PackageEntry> _installTargets = new();
        // _installTargets 와 같은 순서의 요청 전 버전. 새로 설치하는 항목은 빈 문자열
        [SerializeField] private List<string> _previousVersions = new();
        // _installTargets 와 같은 순서의 요청 실패 오류. 다시 요청할 항목만 값이 있고 나머지는 null
        [SerializeReference] private List<LocalizedText> _requestErrors = new();
        // 이번 바퀴에 요청할 _installTargets 의 인덱스와 그중 현재 위치
        [SerializeField] private List<int> _passIndices = new();
        [SerializeField] private int _passPosition;
        [SerializeField] private bool _isRetryPass;
        // 현재 항목의 응답 없음 오류. 없으면 null
        [SerializeReference] private LocalizedText _installErrorText;
        [SerializeField] private long _deadlineTicks;

        // 도메인 리로드로 사라지는 값. 리로드 뒤에는 목록 요청을 다시 보내 상한도 새로 잡는다.
        private ListRequest _listRequest;
        private long _listDeadlineTicks;
        private AddAndRemoveRequest _addRequest;

        public UpmInstallStep(List<PackageEntry> targets, List<PackageEntry> reinstallTargets) : base(targets)
        {
            _reinstallTargets = reinstallTargets;
        }

        public override string StepNameKey => "step.upmInstall";

        private int CurrentIndex => _passIndices[_passPosition];
        private PackageEntry CurrentTarget => _installTargets[CurrentIndex];

        protected override void OnBegin(ApplyContext context)
        {
            _phase = UpmInstallPhase.CheckInstalled;
            context.SetProgress(new LocalizedText("upm.progress.checkInstalled"));
            RequestList();
        }

        public override void OnResume(ApplyContext context)
        {
            // 설치 요청이 끝나면 도메인 리로드가 일어나 요청 객체가 사라진다. 리로드 뒤에는 현재 항목의 설치 결과를 다시 확인한다.
            if (_phase == UpmInstallPhase.Installing)
            {
                _phase = UpmInstallPhase.Verify;
                context.Save();
            }
            RequestList();
        }

        public override void Tick(ApplyContext context)
        {
            switch (_phase)
            {
                case UpmInstallPhase.CheckInstalled:
                    TickCheckInstalled(context);
                    break;
                case UpmInstallPhase.Installing:
                    TickInstalling(context);
                    break;
                case UpmInstallPhase.Verify:
                    TickVerify(context);
                    break;
            }
        }

        // 설치 상태 확인은 전체 항목을 한 번에 조회하므로 개별 항목의 처리로 보지 않는다.
        public override LocalizedText GetActivityText(PackageEntry package)
        {
            if (_phase == UpmInstallPhase.CheckInstalled || !CurrentTarget.HasKey(package.type, package.id))
            {
                return null;
            }
            if (_phase == UpmInstallPhase.Verify)
            {
                return new LocalizedText("applyItem.activity.verifying");
            }
            return new LocalizedText(_isRetryPass ? "applyItem.activity.retrying" : "applyItem.activity.installing");
        }

        // 첫 요청이 실패해 다시 요청할 항목
        public override LocalizedText GetWaitingText(PackageEntry package)
        {
            if (_phase == UpmInstallPhase.CheckInstalled)
            {
                return null;
            }
            int index = _installTargets.FindIndex(x => x.HasKey(package.type, package.id));
            return index >= 0 && _requestErrors[index] != null ? new LocalizedText("applyItem.waiting.retry") : null;
        }

        protected override void OnAbort(ApplyContext context)
        {
            // 설치 상태 확인 중에는 아직 설치를 요청하지 않았다.
            if (_phase == UpmInstallPhase.CheckInstalled)
            {
                ReportAll(_targets, context, ApplyOutcome.Skipped, new LocalizedText("apply.cancelled"));
                return;
            }

            // 현재 항목의 설치 요청은 응답을 기다리는 중이든 결과를 확인하는 중이든 취소할 수 없어 Unity가 마칠 수 있다.
            context.Report(CurrentTarget, StepNameKey, ApplyOutcome.Unverified, new LocalizedText("upm.cancelledInFlight"));
            var remainingIndices = _passIndices.Skip(_passPosition + 1).ToList();
            if (_isRetryPass)
            {
                // 다시 요청을 기다리던 항목은 첫 요청의 실패로 기록한다.
                foreach (int i in remainingIndices)
                {
                    context.Report(_installTargets[i], StepNameKey, ApplyOutcome.Failed, _requestErrors[i]);
                }
                return;
            }
            // 첫 바퀴에서 요청이 실패해 다시 요청을 기다리던 항목은 그 실패로, 아직 요청하지 않은 항목은 건너뜀으로 기록한다.
            for (int i = 0; i < _installTargets.Count; i++)
            {
                if (_requestErrors[i] != null)
                {
                    context.Report(_installTargets[i], StepNameKey, ApplyOutcome.Failed, _requestErrors[i]);
                }
            }
            ReportAll(remainingIndices.Select(i => _installTargets[i]), context, ApplyOutcome.Skipped, new LocalizedText("apply.cancelled"));
        }

        private void TickCheckInstalled(ApplyContext context)
        {
            if (!_listRequest.IsCompleted)
            {
                if (NowTicks > _listDeadlineTicks)
                {
                    ReportAll(_targets, context, ApplyOutcome.Failed, new LocalizedText("upm.listTimeout", ListTimeoutMinutes));
                    Finish(context);
                }
                return;
            }
            if (_listRequest.Status == StatusCode.Failure)
            {
                ReportAll(_targets, context, ApplyOutcome.Failed, new LocalizedText("upm.listFailed", _listRequest.Error.message));
                Finish(context);
                return;
            }

            foreach (var target in _targets)
            {
                var installed = _listRequest.Result.FindByKey(target.id);
                if (installed == null)
                {
                    _installTargets.Add(target);
                    _previousVersions.Add(string.Empty);
                }
                else if (_reinstallTargets.Exists(x => x.HasKey(target.type, target.id)))
                {
                    _installTargets.Add(target);
                    _previousVersions.Add(installed.GetVersionLabel());
                }
                else
                {
                    context.Report(target, StepNameKey, ApplyOutcome.Skipped, new LocalizedText("upm.alreadyInstalled"));
                }
            }

            if (_installTargets.Count == 0)
            {
                Finish(context);
                return;
            }

            _requestErrors = _installTargets.Select(_ => (LocalizedText)null).ToList();
            _passIndices = Enumerable.Range(0, _installTargets.Count).ToList();
            _passPosition = 0;
            _isRetryPass = false;
            RequestCurrent(context);
        }

        private void TickInstalling(ApplyContext context)
        {
            bool isTimedOut = NowTicks > _deadlineTicks;
            if (!_addRequest.IsCompleted && !isTimedOut)
            {
                return;
            }

            // 실패한 요청은 manifest 를 바꾸지 않고 리로드도 일으키지 않으므로(6000.6 실측) 목록을 확인하지 않고 바로 판정한다.
            if (_addRequest.IsCompleted && _addRequest.Status == StatusCode.Failure)
            {
                string message = _addRequest.Error.message;
                LocalizedText errorText = string.IsNullOrEmpty(message) ? new LocalizedText("upm.installFailedNoMessage") : new RawText(message);
                if (_isRetryPass)
                {
                    context.Report(CurrentTarget, StepNameKey, ApplyOutcome.Failed, errorText);
                }
                else
                {
                    _requestErrors[CurrentIndex] = errorText;
                }
                MoveNext(context);
                return;
            }

            // 응답이 없어도 설치는 진행됐을 수 있어 목록으로 확인한다.
            _installErrorText = _addRequest.IsCompleted ? null : new LocalizedText("upm.installTimeout", InstallTimeoutMinutes);
            _phase = UpmInstallPhase.Verify;
            context.SetProgress(new LocalizedText("upm.progress.verify"));
            context.Save();
            RequestList();
        }

        private void TickVerify(ApplyContext context)
        {
            bool hasInstallError = _installErrorText != null;
            if (!_listRequest.IsCompleted)
            {
                // 응답하지 않는 설치 요청 뒤에서 목록 요청도 기다릴 수 있어, 설치 상한과 별도로 요청마다 상한을 둔다.
                if (NowTicks > _listDeadlineTicks)
                {
                    var message = new LocalizedText("upm.verifyTimeout", ListTimeoutMinutes);
                    context.Report(CurrentTarget, StepNameKey, ApplyOutcome.Failed, hasInstallError ? new LocalizedText("common.joined", _installErrorText, message) : message);
                    MoveNext(context);
                }
                return;
            }
            if (_listRequest.Status == StatusCode.Failure)
            {
                context.Report(CurrentTarget, StepNameKey, ApplyOutcome.Failed, new LocalizedText("upm.verifyFailed", _listRequest.Error.message));
                MoveNext(context);
                return;
            }

            var target = CurrentTarget;
            var installed = _listRequest.Result.FindByKey(target.id);
            // 리로드 직후에는 설치가 아직 반영되지 않았을 수 있어 상한 시각까지 다시 확인한다.
            if (installed == null && !hasInstallError && NowTicks <= _deadlineTicks)
            {
                RequestList();
                return;
            }

            string previousVersion = _previousVersions[CurrentIndex];
            // 다시 요청한 항목은 요청 전부터 설치돼 있으므로, 응답이 없었으면 설치돼 있어도 실패로 본다.
            bool isReinstall = !string.IsNullOrEmpty(previousVersion);
            if (installed != null && !(isReinstall && hasInstallError))
            {
                context.Report(target, StepNameKey, ApplyOutcome.Success, GetReinstallMessage(previousVersion, installed));
            }
            else
            {
                context.Report(target, StepNameKey, ApplyOutcome.Failed, hasInstallError ? _installErrorText : new LocalizedText("upm.notInstalled", InstallTimeoutMinutes));
            }
            MoveNext(context);
        }

        // 현재 항목을 마치고 다음 항목을 요청한다. 첫 바퀴가 끝나면 요청이 실패한 항목만 한 번 더 요청한다.
        private void MoveNext(ApplyContext context)
        {
            _passPosition++;
            if (_passPosition < _passIndices.Count)
            {
                RequestCurrent(context);
                return;
            }

            if (!_isRetryPass)
            {
                _passIndices = Enumerable.Range(0, _installTargets.Count).Where(i => _requestErrors[i] != null).ToList();
                _passPosition = 0;
                _isRetryPass = true;
                if (_passIndices.Count > 0)
                {
                    RequestCurrent(context);
                    return;
                }
            }
            Finish(context);
        }

        private void RequestCurrent(ApplyContext context)
        {
            var target = CurrentTarget;
            string progressKey = _isRetryPass ? "upm.progress.retrying" : "upm.progress.installing";
            _phase = UpmInstallPhase.Installing;
            _installErrorText = null;
            _deadlineTicks = GetDeadlineTicks(TimeSpan.FromMinutes(InstallTimeoutMinutes));
            context.SetProgress(new LocalizedText(progressKey, _passPosition + 1, _passIndices.Count, target.displayName));
            context.Save();
            _addRequest = Client.AddAndRemove(new[] { target.id });
        }

        // 다시 요청한 항목의 버전 변화. 새로 설치한 항목은 null
        private LocalizedText GetReinstallMessage(string previousVersion, UnityEditor.PackageManager.PackageInfo installed)
        {
            if (string.IsNullOrEmpty(previousVersion))
            {
                return null;
            }
            string currentVersion = installed.GetVersionLabel();
            return currentVersion == previousVersion
                ? new LocalizedText("upm.reinstalledSameVersion", currentVersion)
                : new LocalizedText("upm.reinstalled", previousVersion, currentVersion);
        }

        private void RequestList()
        {
            _listDeadlineTicks = GetDeadlineTicks(TimeSpan.FromMinutes(ListTimeoutMinutes));
            _listRequest = Client.List(true, true);
        }
    }
}
