using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 패키지 종류 하나의 탭. 한 목록 안에서 '선택됨' 구역(위)과 '선택 안 됨' 구역(아래)으로 나눠 보여준다.
    // 두 구역 모두 표시명 순이고, 체크·해제하면 항목이 상대 구역의 원래 순서 위치로 옮겨간다.
    public class PackageTypeTab : VisualElement
    {
        private const string UXML_FILE_NAME = "PackageTypeTab.uxml";

        public Action<PackageEntry, bool> OnTriggerToggle;
        public Action<PackageEntry> OnTriggerDelete;
        public Action<PackageEntry> OnTriggerShowDetails;
        public Action<string> OnTriggerAddGitUrl;

        public Tab Tab => _tab;
        private readonly Tab _tab;
        public PackageType PackageType => _packageType;
        private readonly PackageType _packageType;

        private readonly Label _selectedHeader;
        private readonly VisualElement _selectedSection;
        private readonly Label _unselectedHeader;
        private readonly VisualElement _unselectedSection;
        private readonly Label _emptyLabel;
        private readonly TextField _gitUrlField;
        private readonly Button _gitAddButton;
        private readonly Label _gitAddMessageLabel;
        // 언어를 바꾸면 다시 그리도록 입력칸 아래 메시지를 문장 대신 보관한다. 없으면 null
        private LocalizedText _gitAddMessage;
        // 표시명 순. 구역을 옮길 때 이 순서로 원래 위치를 정한다.
        private readonly List<PackageElement> _packageElementList = new();

        public PackageTypeTab(PackageType packageType)
        {
            var content = ResourceLoader.GetUXML(UXML_FILE_NAME);

            _packageType = packageType;
            _tab = content.Q<Tab>("PackageTypeTab");
            _tab.label = packageType.ToString();
            // TabView 는 선택한 탭을 탭의 viewDataKey 로 기억했다가 리로드 뒤 되살린다(6000.6 실측).
            _tab.viewDataKey = $"PackageTypeTab.{packageType}";

            _selectedHeader = content.Q<Label>("SelectedHeader");
            _selectedSection = content.Q<VisualElement>("SelectedSection");
            _unselectedHeader = content.Q<Label>("UnselectedHeader");
            _unselectedSection = content.Q<VisualElement>("UnselectedSection");
            _emptyLabel = content.Q<Label>("EmptyLabel");

            // URL 직접 추가는 Git 탭에만 있다.
            var gitAddRow = content.Q<VisualElement>("GitAddRow");
            gitAddRow.style.display = packageType == PackageType.Git ? DisplayStyle.Flex : DisplayStyle.None;
            _gitUrlField = content.Q<TextField>("GitUrlField");
            _gitAddButton = content.Q<Button>("GitAddButton");
            _gitAddButton.clicked += () => OnTriggerAddGitUrl?.Invoke(_gitUrlField.value);
            // 입력칸이 내부에서 Enter 를 처리하기 전에 받는다. 확인 중·Play Mode 에는 입력칸과 버튼이 함께 비활성이라 키를 받지 않는다.
            _gitUrlField.RegisterCallback<KeyDownEvent>(OnGitUrlKeyDown, TrickleDown.TrickleDown);
            _gitAddMessageLabel = content.Q<Label>("GitAddMessage");
            // 오류 원문을 복사할 수 있게 한다.
            _gitAddMessageLabel.selection.isSelectable = true;
            // 메시지는 입력한 값에 대한 것이므로 입력을 고치면 지운다.
            _gitUrlField.RegisterValueChangedCallback(_ => ClearGitAddMessage());
            ApplyLanguage();
        }

        // 항목 행의 문장은 SetEntries 로 목록을 다시 만들 때 바뀐다.
        public void ApplyLanguage()
        {
            _gitUrlField.textEdition.placeholder = LocalizationManager.Get("ui.gitUrl.placeholder");
            _gitAddButton.text = LocalizationManager.Get("ui.gitUrl.add");
            if (_gitAddMessage != null)
            {
                _gitAddMessageLabel.text = _gitAddMessage.GetText();
            }
            RefreshSummary();
        }

        // 표시명 순으로 목록을 다시 만든다. description 은 툴팁 설명(없으면 null), detailsUnavailableReason 은 상세 보기를 할 수 없는 이유(할 수 있으면 null)
        public void SetEntries(IEnumerable<(PackageEntry entry, bool isIncluded, bool isInCatalog, string description, LocalizedText detailsUnavailableReason)> items)
        {
            _selectedSection.Clear();
            _unselectedSection.Clear();
            _packageElementList.Clear();
            foreach (var (entry, isIncluded, isInCatalog, description, detailsUnavailableReason) in items.OrderBy(x => x.entry.displayName, StringComparer.OrdinalIgnoreCase))
            {
                var element = new PackageElement(entry, isIncluded, isInCatalog, description, detailsUnavailableReason);
                element.OnTriggerToggle += (package, isChecked) => OnElementToggled(element, package, isChecked);
                element.OnTriggerDelete += package => OnTriggerDelete?.Invoke(package);
                element.OnTriggerShowDetails += package => OnTriggerShowDetails?.Invoke(package);
                GetSection(isIncluded).Add(element);
                _packageElementList.Add(element);
            }
        }

        // 행마다 설치 상태 배지만 바꾼다. 목록을 다시 만들지 않아 스크롤 위치가 유지된다.
        public void UpdateInstallStates(Func<PackageEntry, (PackageInstallState state, string version)> getInstallState)
        {
            foreach (var element in _packageElementList)
            {
                var (state, version) = getInstallState(element.Entry);
                element.SetInstallState(state, version);
            }
        }

        public void UpdateElementsVisible(string searchWord)
        {
            _packageElementList.ForEach(x => x.Activate(string.IsNullOrEmpty(searchWord) || x.Matches(searchWord)));
            RefreshSummary();
        }

        public void ClearGitUrlField()
        {
            _gitUrlField.SetValueWithoutNotify(string.Empty);
        }

        public void SetGitUrlInputEnabled(bool isEnabled)
        {
            _gitUrlField.SetEnabled(isEnabled);
            _gitAddButton.SetEnabled(isEnabled);
        }

        // 추가하지 않은 URL 의 사유(빨강)
        public void ShowGitAddError(LocalizedText message)
        {
            ShowGitAddMessage(message, "git-add-message--error");
        }

        // 확인하지 못하고 추가한 URL 의 경고(노랑)
        public void ShowGitAddWarning(LocalizedText message)
        {
            ShowGitAddMessage(message, "git-add-message--warning");
        }

        // 저장소 안에서 찾은 패키지가 여러 개면 추가 버튼 아래에 경로 메뉴를 띄운다. 고르면 그 URL 로 추가를 다시 요청하고, 닫으면 아무것도 하지 않는다.
        // 에디터 메뉴(GenericMenu)는 '/' 를 하위 메뉴로 나눠 경로가 쪼개지므로 UI Toolkit 메뉴를 쓴다.
        public void ShowGitCandidateMenu(IReadOnlyList<string> candidateUrls)
        {
            var menu = new GenericDropdownMenu();
            foreach (string url in candidateUrls)
            {
                menu.AddItem(new GitPackageUrl(url).Path, false, () => OnTriggerAddGitUrl?.Invoke(url));
            }
            // DropdownMenuSizeMode 는 6000.3 에서 추가됐고, 그 뒤로 bool 오버로드는 obsolete 다(6000.0~6000.2 에는 bool 오버로드만 있음).
#if UNITY_6000_3_OR_NEWER
            menu.DropDown(_gitAddButton.worldBound, _gitAddButton, DropdownMenuSizeMode.Auto);
#else
            menu.DropDown(_gitAddButton.worldBound, _gitAddButton, false);
#endif
        }

        public void ClearGitAddMessage()
        {
            _gitAddMessage = null;
            _gitAddMessageLabel.text = string.Empty;
            _gitAddMessageLabel.style.display = DisplayStyle.None;
        }

        private void ShowGitAddMessage(LocalizedText message, string severityClass)
        {
            _gitAddMessage = message;
            _gitAddMessageLabel.text = message.GetText();
            _gitAddMessageLabel.RemoveFromClassList("git-add-message--error");
            _gitAddMessageLabel.RemoveFromClassList("git-add-message--warning");
            _gitAddMessageLabel.AddToClassList(severityClass);
            _gitAddMessageLabel.style.display = DisplayStyle.Flex;
        }

        // Enter 는 추가 버튼과 같다.
        private void OnGitUrlKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
            {
                return;
            }
            evt.StopPropagation();
            OnTriggerAddGitUrl?.Invoke(_gitUrlField.value);
        }

        private void OnElementToggled(PackageElement element, PackageEntry package, bool isIncluded)
        {
            MoveToSection(element, isIncluded);
            RefreshSummary();
            OnTriggerToggle?.Invoke(package, isIncluded);
        }

        // 대상 구역에서 표시명 순서상 원래 자리에 넣는다.
        private void MoveToSection(PackageElement element, bool isIncluded)
        {
            var section = GetSection(isIncluded);
            int order = _packageElementList.IndexOf(element);
            int insertIndex = section.Children().Count(x => _packageElementList.IndexOf((PackageElement)x) < order);
            section.Insert(insertIndex, element);
        }

        private VisualElement GetSection(bool isIncluded)
        {
            return isIncluded ? _selectedSection : _unselectedSection;
        }

        // 탭 라벨에 보이는 항목 중 체크 수/보이는 항목 수를, 구역 머리글에 구역별로 보이는 항목 수를 표시한다.
        // 보이는 항목이 없으면 머리글 대신 안내 문구를 띄운다.
        private void RefreshSummary()
        {
            var activeElements = _packageElementList.Where(x => x.IsActive).ToList();
            int selectedCount = activeElements.Count(x => x.IsIncluded);
            _tab.label = $"{_packageType} ({selectedCount}/{activeElements.Count})";
            _selectedHeader.text = LocalizationManager.Get("ui.section.selected", selectedCount);
            _unselectedHeader.text = LocalizationManager.Get("ui.section.unselected", activeElements.Count - selectedCount);

            bool isEmpty = activeElements.Count == 0;
            _emptyLabel.text = GetEmptyMessage();
            _emptyLabel.style.display = isEmpty ? DisplayStyle.Flex : DisplayStyle.None;
            _selectedHeader.style.display = isEmpty ? DisplayStyle.None : DisplayStyle.Flex;
            _unselectedHeader.style.display = isEmpty ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private string GetEmptyMessage()
        {
            if (_packageElementList.Count > 0)
            {
                return LocalizationManager.Get("ui.empty.noSearchMatch");
            }
            return LocalizationManager.Get(_packageType == PackageType.Git ? "ui.empty.git" : "ui.empty.default");
        }
    }
}
