using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    public class PackageElement : VisualElement
    {
        private const string UXML_FILE_NAME = "PackageElement.uxml";

        public Action<PackageEntry, bool> OnTriggerToggle;
        public Action<PackageEntry> OnTriggerDelete;
        public Action<PackageEntry> OnTriggerShowDetails;

        private readonly Toggle _toggle;
        private readonly Label _installBadge;

        public PackageEntry Entry { get; }
        public bool IsIncluded => _toggle.value;
        public bool IsActive { get; private set; } = true;

        // description: 카탈로그에 저장된 설명. 없으면 null
        // detailsUnavailableReason: 상세 보기를 할 수 없는 이유. 할 수 있으면 null
        public PackageElement(PackageEntry entry, bool isIncluded, bool isInCatalog, string description, LocalizedText detailsUnavailableReason)
        {
            var content = ResourceLoader.GetUXML(UXML_FILE_NAME);
            Add(content);

            Entry = entry;
            _toggle = content.Q<Toggle>("ElementToggle");
            _toggle.text = isInCatalog ? entry.displayName : LocalizationManager.Get("ui.package.notInCatalog", entry.displayName);
            _toggle.tooltip = TrimDescription(description);
            _toggle.SetValueWithoutNotify(isIncluded);
            _toggle.RegisterValueChangedCallback(OnChangedToggle);
            _installBadge = content.Q<Label>("InstallBadge");

            var detailsButton = content.Q<Button>("DetailsButton");
            bool isGit = entry.type == PackageType.Git;
            detailsButton.iconImage = Background.FromTexture2D((Texture2D)EditorGUIUtility.IconContent(isGit ? WEB_LINK_ICON_NAME : PACKAGE_MANAGER_ICON_NAME).image);
            detailsButton.tooltip = detailsUnavailableReason?.GetText()
                                    ?? LocalizationManager.Get(isGit ? "ui.details.git.tooltip" : "ui.details.packageManager.tooltip");
            detailsButton.SetEnabled(detailsUnavailableReason == null);
            detailsButton.clicked += () => OnTriggerShowDetails?.Invoke(Entry);

            var deleteButton = content.Q<Button>("DeleteButton");
            deleteButton.text = LocalizationManager.Get("ui.delete");
            deleteButton.style.display = entry.isManual ? DisplayStyle.Flex : DisplayStyle.None;
            deleteButton.clicked += () => OnTriggerDelete?.Invoke(Entry);
        }

        public bool Matches(string searchWord)
        {
            return Entry.displayName.Contains(searchWord, StringComparison.OrdinalIgnoreCase)
                   || Entry.id.Contains(searchWord, StringComparison.OrdinalIgnoreCase);
        }

        // 현재 프로젝트의 설치 상태 배지. 설치되지 않았으면 숨긴다. version 은 UPM 패키지의 설치 버전(그 밖은 null)
        public void SetInstallState(PackageInstallState state, string version)
        {
            _installBadge.style.display = state == PackageInstallState.NotInstalled ? DisplayStyle.None : DisplayStyle.Flex;
            _installBadge.EnableInClassList("package-row__install-badge--dependency", state is PackageInstallState.Dependency or PackageInstallState.BuiltIn);
            switch (state)
            {
                case PackageInstallState.NotInstalled:
                    break;
                case PackageInstallState.Installed:
                    bool isAssetStore = Entry.type == PackageType.MyAssets;
                    _installBadge.text = LocalizationManager.Get(isAssetStore ? "ui.installBadge.imported" : "ui.installBadge.installed");
                    _installBadge.tooltip = isAssetStore ? LocalizationManager.Get("ui.installBadge.imported.tooltip") : LocalizationManager.Get("ui.installBadge.installed.tooltip", version);
                    break;
                case PackageInstallState.Dependency:
                    _installBadge.text = LocalizationManager.Get("ui.installBadge.dependency");
                    _installBadge.tooltip = LocalizationManager.Get("ui.installBadge.dependency.tooltip", version);
                    break;
                case PackageInstallState.BuiltIn:
                    _installBadge.text = LocalizationManager.Get("ui.installBadge.dependency");
                    _installBadge.tooltip = LocalizationManager.Get("ui.installBadge.builtIn.tooltip", version);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        public void Activate(bool isActive)
        {
            IsActive = isActive;
            style.display = isActive ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private string TrimDescription(string description)
        {
            if (description == null || description.Length <= TOOLTIP_DESCRIPTION_MAX_LENGTH)
            {
                return description;
            }
            return description.Substring(0, TOOLTIP_DESCRIPTION_MAX_LENGTH).TrimEnd() + "…";
        }

        private void OnChangedToggle(ChangeEvent<bool> toggleEvent)
        {
            OnTriggerToggle?.Invoke(Entry, toggleEvent.newValue);
        }
    }
}
