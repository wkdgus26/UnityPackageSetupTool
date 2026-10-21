using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 설치된 패키지 중 프리셋에 등록할 것을 고르는 창.
    // 새 항목은 처음에 모두 해제돼 있다. 이미 등록된 항목은 체크된 채 비활성이고 '등록됨' 배지를 붙여, 여기서 프리셋에서 뺄 수 없다.
    // scoped registry 에서 설치한 항목에는 그 레지스트리가 필요하다는 배지를 붙인다(대상 프로젝트 manifest 에 없으면 적용할 때 실패한다).
    public class RegisterSelectWindow : PackageDialogWindowBase
    {
        // 고를 수 있는(아직 등록되지 않은) 행
        private readonly List<(PackageEntry package, Toggle toggle)> _rows = new();
        private InstalledScanResult _scanResult;
        private int _registeredCount;

        public List<PackageEntry> SelectedPackages => _rows.Where(x => x.toggle.value).Select(x => x.package).ToList();

        public void Initialize(InstalledScanResult scanResult)
        {
            _scanResult = scanResult;
            var packages = scanResult.InstalledPackages;
            _registeredCount = packages.Count(scanResult.IsRegistered);
            BuildLayout(
                LocalizationManager.Get("dialog.register.title"),
                LocalizationManager.Get("dialog.register.message", scanResult.Preset.name),
                scanResult.Notices,
                packages,
                true);

            AddToolbarButton(LocalizationManager.Get("dialog.register.all"), () => SetAllSelected(true));
            AddToolbarButton(LocalizationManager.Get("dialog.register.none"), () => SetAllSelected(false));
            ConfirmButton.text = LocalizationManager.Get("dialog.register.ok");
            RefreshSummary();
        }

        protected override VisualElement CreateRow(PackageEntry package)
        {
            var row = CreateRowContainer();
            var toggle = new Toggle { text = package.displayName, tooltip = package.id };
            toggle.AddToClassList("package-row__toggle");
            row.Add(toggle);
            if (_scanResult.IsRegistered(package))
            {
                toggle.SetValueWithoutNotify(true);
                toggle.SetEnabled(false);
                AddBadge(row, LocalizationManager.Get("dialog.badge.registered"));
            }
            else
            {
                toggle.RegisterValueChangedCallback(_ => RefreshSummary());
                _rows.Add((package, toggle));
            }

            var registry = _scanResult.FindScopedRegistry(package);
            if (registry != null)
            {
                var badge = AddBadge(row, LocalizationManager.Get("dialog.badge.scopedRegistry", registry.name), "apply-dialog__badge--warning");
                badge.tooltip = LocalizationManager.Get("dialog.badge.scopedRegistry.tooltip", registry.name, registry.url);
            }
            return row;
        }

        private void SetAllSelected(bool isSelected)
        {
            foreach (var (_, toggle) in _rows)
            {
                toggle.SetValueWithoutNotify(isSelected);
            }
            RefreshSummary();
        }

        private void RefreshSummary()
        {
            int selectedCount = _rows.Count(x => x.toggle.value);
            SetSummary(LocalizationManager.Get("dialog.register.summary", selectedCount, _registeredCount));
            ConfirmButton.SetEnabled(selectedCount > 0);
        }
    }
}
