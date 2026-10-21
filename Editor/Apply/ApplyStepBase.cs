using System;
using System.Collections.Generic;
using UnityEngine;

namespace PackageSetupTool
{
    // 프리셋 적용 단계. [SerializeReference] 로 저장되어 도메인 리로드·에디터 재시작 뒤 이어서 진행된다.
    // 새 단계는 이 클래스를 상속하고 PackageSetupSystem 의 단계 목록에 추가한다.
    [Serializable]
    public abstract class ApplyStepBase
    {
        [SerializeField] protected List<PackageEntry> _targets = new();
        [SerializeField] private bool _isStarted;
        [SerializeField] private bool _isFinished;

        protected ApplyStepBase(List<PackageEntry> targets)
        {
            _targets = targets;
        }

        // 단계명의 문장 키. 결과 기록에 키로 저장해 언어를 바꾸면 다시 그린다.
        public abstract string StepNameKey { get; }

        public bool IsStarted => _isStarted;
        public bool IsFinished => _isFinished;
        public IReadOnlyList<PackageEntry> Targets => _targets;

        protected long NowTicks => DateTime.UtcNow.Ticks;

        public void Begin(ApplyContext context)
        {
            // 내부 호출이 예외로 끝나도 같은 요청을 매 프레임 반복하지 않도록 시작 표시를 먼저 저장한다.
            _isStarted = true;
            context.Save();
            if (_targets.Count == 0)
            {
                Finish(context);
                return;
            }
            OnBegin(context);
        }

        public abstract void Tick(ApplyContext context);

        // 진행 중인 단계가 지금 package 를 처리하고 있으면 처리 내용(예: 설치 중), 아니면 null
        public abstract LocalizedText GetActivityText(PackageEntry package);

        // 진행 중인 단계 안에서 package 가 다음 처리를 기다리는 사유(예: 재시도 대기). 없으면 null
        public virtual LocalizedText GetWaitingText(PackageEntry package)
        {
            return null;
        }

        // 도메인 리로드로 사라진 요청 객체·이벤트 구독을 복구한다.
        public abstract void OnResume(ApplyContext context);

        // 적용을 강제로 멈춘다. 진행 중인 처리를 정리하고 아직 결과가 없는 대상을 모두 기록한 뒤 단계를 끝낸다.
        public void Abort(ApplyContext context)
        {
            OnAbort(context);
            Finish(context);
        }

        protected abstract void OnBegin(ApplyContext context);

        // 이미 Unity에 보내 취소할 수 없는 처리(UPM 설치 요청·임포트)의 대상은 확인 필요로, 시작하지 않은 대상은 건너뜀으로 기록한다.
        protected abstract void OnAbort(ApplyContext context);

        protected void Finish(ApplyContext context)
        {
            _isFinished = true;
            context.Save();
        }

        protected void ReportAll(IEnumerable<PackageEntry> packages, ApplyContext context, ApplyOutcome outcome, LocalizedText message)
        {
            foreach (var package in packages)
            {
                context.Report(package, StepNameKey, outcome, message);
            }
        }

        // 대상 중 로컬 .unitypackage 가 최신이 아닌 구매 에셋과 받아야 할 업로드 ID 를 같은 순서로 채운다(Asset Store 단계용).
        // outdatedLocalUploadIds: 로컬 파일이 최신이 아닌 상품 → 받아야 할 업로드 ID. 대상이 아닌 상품은 무시한다.
        protected void FillRefreshTargets(IReadOnlyDictionary<long, long> outdatedLocalUploadIds, List<long> productIds, List<long> uploadIds)
        {
            foreach (var target in _targets)
            {
                long productId = long.Parse(target.id);
                if (outdatedLocalUploadIds.TryGetValue(productId, out long uploadId))
                {
                    productIds.Add(productId);
                    uploadIds.Add(uploadId);
                }
            }
        }

        protected long GetDeadlineTicks(TimeSpan duration)
        {
            return NowTicks + duration.Ticks;
        }
    }
}
