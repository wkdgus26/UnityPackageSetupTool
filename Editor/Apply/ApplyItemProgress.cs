using System.Collections.Generic;
using System.Linq;

namespace PackageSetupTool
{
    // 프리셋 적용 중 항목 하나의 진행 정보. 저장하지 않고 단계 상태·결과 기록에서 매번 만든다(PackageSetupSystem.GetApplyProgress).
    public class ApplyItemProgress
    {
        // statusText: 처리 중이면 처리 내용, 대기 중이면 대기 사유. 완료됐거나 사유가 없으면 null
        public ApplyItemProgress(PackageEntry package, ApplyItemState state, IReadOnlyList<ApplyItemRecord> records, LocalizedText statusText)
        {
            Package = package;
            State = state;
            Records = records;
            StatusText = statusText;
        }

        public PackageEntry Package { get; }
        public ApplyItemState State { get; }
        // 이 항목의 단계별 결과 기록(기록된 순서)
        public IReadOnlyList<ApplyItemRecord> Records { get; }
        public LocalizedText StatusText { get; }

        // 마지막으로 기록된 단계 결과를 최종 결과로 본다(PackageSetupSystem.GetPackageOutcomes 와 같은 기준). 완료된 항목에서만 쓴다.
        public ApplyOutcome Outcome => Records[Records.Count - 1].outcome;

        // 완료: 기록의 사유. 단계가 여럿이면(Asset Store 다운로드 → 임포트) 앞 단계의 사유가 가려지지 않도록 단계명을 붙여 모두 보여준다.
        // 그 밖: 처리 내용·대기 사유
        public string GetDetailText()
        {
            if (State != ApplyItemState.Done)
            {
                return StatusText == null ? string.Empty : StatusText.GetText();
            }
            if (Records.Count == 1)
            {
                return Records[0].GetMessage();
            }
            return string.Join(" / ", Records
                .Where(x => x.message != null)
                .Select(x => LocalizationManager.Get("applyItem.detail.step", x.GetStepName(), x.GetMessage())));
        }
    }
}
