using System.Linq;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 프리셋 적용 확인 창(첫 번째). 적용할 항목을 종류별 구역으로 나눠 모두 보여주고, 이미 설치·임포트된 항목에 배지를 붙인다.
    // 설치된 항목에 업데이트가 있으면 '업데이트 현재 → 최신' 배지를, Git 리비전이 고정돼 있으면 '고정됨' 배지를 함께 붙인다.
    // 이미 설치된 항목이 있으면 확인 버튼이 '다음'이 되고, 이어서 ReinstallSelectWindow 에서 재임포트·제외를 고른다.
    public class ApplyConfirmWindow : PackageDialogWindowBase
    {
        private InstallCheckResult _checkResult;

        public void Initialize(InstallCheckResult checkResult)
        {
            _checkResult = checkResult;
            var preset = checkResult.Preset;
            BuildLayout(
                LocalizationManager.Get("dialog.applyPreset.title"),
                LocalizationManager.Get("dialog.applyPreset.message", preset.name, preset.packages.Count),
                checkResult.Notices,
                preset.packages,
                true);

            int installedCount = checkResult.InstalledPackages.Count;
            int newCount = preset.packages.Count - installedCount;
            int updateCount = checkResult.UpdateCount;
            SetSummary(updateCount > 0
                ? LocalizationManager.Get("dialog.applyPreset.summaryWithUpdates", newCount, installedCount, updateCount)
                : LocalizationManager.Get("dialog.applyPreset.summary", newCount, installedCount));
            ConfirmButton.text = LocalizationManager.Get(installedCount > 0 ? "dialog.next" : "dialog.applyPreset.ok");
        }

        protected override VisualElement CreateRow(PackageEntry package)
        {
            var row = CreateRowContainer();
            var nameLabel = new Label(package.displayName) { tooltip = package.id };
            nameLabel.AddToClassList("apply-dialog__row-name");
            row.Add(nameLabel);

            if (!_checkResult.InstalledPackages.Any(x => x.HasKey(package.type, package.id)))
            {
                return row;
            }
            var update = _checkResult.FindUpdate(package);
            if (update != null)
            {
                AddUpdateBadge(row, update);
            }
            AddBadge(row, LocalizationManager.Get(package.type == PackageType.MyAssets ? "dialog.badge.imported" : "dialog.badge.installed"));
            return row;
        }
    }
}
