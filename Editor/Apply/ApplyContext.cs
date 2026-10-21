using System;

namespace PackageSetupTool
{
    // 적용 단계에 넘기는 데이터와 보고 경로. 단계는 PackageSetupSystem 을 직접 알지 않는다.
    public class ApplyContext
    {
        private readonly Action<ApplyItemRecord> _report;
        private readonly Action<LocalizedText> _setProgress;
        private readonly Action _save;

        public ApplyContext(
            AssetStoreInternalBridge bridge,
            LocalPackageLocator localPackages,
            Action<ApplyItemRecord> report,
            Action<LocalizedText> setProgress,
            Action save)
        {
            Bridge = bridge;
            LocalPackages = localPackages;
            _report = report;
            _setProgress = setProgress;
            _save = save;
        }

        public AssetStoreInternalBridge Bridge { get; }
        public LocalPackageLocator LocalPackages { get; }

        public void Report(PackageEntry package, string stepNameKey, ApplyOutcome outcome, LocalizedText message = null)
        {
            _report(new ApplyItemRecord(package, stepNameKey, outcome, message));
        }

        public void SetProgress(LocalizedText text)
        {
            _setProgress(text);
        }

        // 진행 상태를 Library 파일에 기록한다. 도메인 리로드·에디터 재시작 뒤 이어서 진행하기 위함
        public void Save()
        {
            _save();
        }
    }
}
