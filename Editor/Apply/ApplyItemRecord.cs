using System;
using UnityEngine;

namespace PackageSetupTool
{
    // 적용 결과 한 줄(항목 × 단계). 언어를 바꾸면 다시 그리도록 단계명은 키로, 사유는 LocalizedText 로 저장한다.
    [Serializable]
    public class ApplyItemRecord
    {
        public PackageEntry package;
        public string stepNameKey;
        public ApplyOutcome outcome;
        // 사유가 없으면 null
        [SerializeReference] public LocalizedText message;

        public ApplyItemRecord(PackageEntry package, string stepNameKey, ApplyOutcome outcome, LocalizedText message)
        {
            this.package = package.Clone();
            this.stepNameKey = stepNameKey;
            this.outcome = outcome;
            this.message = message;
        }

        public string GetStepName()
        {
            return LocalizationManager.Get(stepNameKey);
        }

        public string GetOutcomeText()
        {
            return LocalizationManager.Get(outcome switch
            {
                ApplyOutcome.Success => "outcome.success",
                ApplyOutcome.Skipped => "outcome.skipped",
                ApplyOutcome.Failed => "outcome.failed",
                ApplyOutcome.Unverified => "outcome.unverified",
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
            });
        }

        public string GetMessage()
        {
            return message == null ? string.Empty : message.GetText();
        }
    }
}
