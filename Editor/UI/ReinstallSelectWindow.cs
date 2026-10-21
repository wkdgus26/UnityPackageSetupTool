using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 이미 설치·임포트된 항목 중 다시 설치·임포트할 것을 고르는 창(두 번째).
    // 체크한 항목은 재임포트, 체크하지 않은 항목은 이번 적용에서 제외한다.
    // 처음에는 업데이트가 있는 항목만 체크(재임포트)하고 나머지는 제외(덮어쓰지 않음)다.
    public class ReinstallSelectWindow : PackageDialogWindowBase
    {
        private readonly List<(PackageEntry package, Toggle toggle)> _rows = new();
        private InstallCheckResult _checkResult;

        public List<PackageEntry> ReinstallPackages => _rows.Where(x => x.toggle.value).Select(x => x.package).ToList();
        public List<PackageEntry> ExcludedPackages => _rows.Where(x => !x.toggle.value).Select(x => x.package).ToList();

        public void Initialize(InstallCheckResult checkResult)
        {
            _checkResult = checkResult;
            var installedPackages = checkResult.InstalledPackages;
            // 목록에 있는 종류의 경고만 보여준다.
            var warnings = Enum.GetValues(typeof(PackageType)).Cast<PackageType>()
                .Where(type => installedPackages.Any(x => x.type == type))
                .Select(type => new LocalizedText(GetWarningKey(type)))
                .ToList();
            string description = LocalizationManager.Get("dialog.reinstall.message", installedPackages.Count);
            if (checkResult.UpdateCount > 0)
            {
                description += "\n" + LocalizationManager.Get("dialog.reinstall.updatesPreselected");
            }
            BuildLayout(
                LocalizationManager.Get("dialog.reinstall.title"),
                description,
                warnings,
                installedPackages,
                false);

            AddToolbarButton(LocalizationManager.Get("dialog.reinstall.all"), () => SetAllReinstall(true));
            AddToolbarButton(LocalizationManager.Get("dialog.reinstall.none"), () => SetAllReinstall(false));
            ConfirmButton.text = LocalizationManager.Get("dialog.applyPreset.ok");
            RefreshSummary();
        }

        protected override VisualElement CreateRow(PackageEntry package)
        {
            var row = CreateRowContainer();
            var update = _checkResult.FindUpdate(package);
            var toggle = new Toggle { text = package.displayName, tooltip = package.id };
            toggle.AddToClassList("package-row__toggle");
            toggle.SetValueWithoutNotify(update != null && update.HasUpdate);
            toggle.RegisterValueChangedCallback(_ => RefreshSummary());
            row.Add(toggle);
            if (update != null)
            {
                AddUpdateBadge(row, update);
            }
            _rows.Add((package, toggle));
            return row;
        }

        private void SetAllReinstall(bool isReinstall)
        {
            foreach (var (_, toggle) in _rows)
            {
                toggle.SetValueWithoutNotify(isReinstall);
            }
            RefreshSummary();
        }

        private void RefreshSummary()
        {
            int reinstallCount = _rows.Count(x => x.toggle.value);
            int excludedCount = _rows.Count - reinstallCount;
            int updateCount = _checkResult.UpdateCount;
            SetSummary(updateCount > 0
                ? LocalizationManager.Get("dialog.reinstall.summaryWithUpdates", reinstallCount, excludedCount, updateCount)
                : LocalizationManager.Get("dialog.reinstall.summary", reinstallCount, excludedCount));
        }

        private string GetWarningKey(PackageType type) => type switch
        {
            PackageType.UnityRegistry => "dialog.reinstall.warning.unityRegistry",
            PackageType.MyAssets => "dialog.reinstall.warning.myAssets",
            PackageType.Git => "dialog.reinstall.warning.git",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
    }
}
