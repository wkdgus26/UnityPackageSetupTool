using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 패키지 목록을 종류별로 보여주는 모달 창(프리셋 적용 확인·재임포트 선택·설치된 패키지 등록)의 공통 틀.
    // 안내 문구, 경고, 종류별 세로 구역(PackageType 순서), 하단 요약·버튼을 만든다.
    // 파생 창은 행 모양과 요약만 정한다. 확인 버튼을 눌러 닫았는지는 ShowModalAndWait 의 반환값으로 받는다.
    // 취소 버튼은 두지 않고 창의 닫기(X)로 취소한다(false).
    public abstract class PackageDialogWindowBase : EditorWindow
    {
        private const string UXML_FILE_NAME = "PackageDialogWindow.uxml";
        private const float MIN_WIDTH = 420f;
        private const float MIN_HEIGHT = 360f;
        private const float DEFAULT_WIDTH = 480f;
        private const float DEFAULT_HEIGHT = 520f;

        private Label _summaryLabel;
        private VisualElement _toolbarContainer;
        private bool _isConfirmed;

        protected Button ConfirmButton { get; private set; }

        // 도구 창 가운데에 모달로 띄우고 닫힐 때까지 기다린다. 확인 버튼으로 닫았으면 true
        public bool ShowModalAndWait()
        {
            position = DialogPlacement.GetCenteredRect(new Vector2(DEFAULT_WIDTH, DEFAULT_HEIGHT));
            // ShowModal 은 같은 종류의 창이 마지막으로 닫힌 위치·크기(EditorPrefs)를 불러와 위 위치를 덮어쓴다(6000.6 실측).
            // 창이 뜬 뒤 첫 레이아웃에서 불러온 크기는 두고 도구 창 가운데로 다시 옮긴다.
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(OnFirstLayout);
            ShowModal();
            return _isConfirmed;
        }

        private void OnFirstLayout(GeometryChangedEvent evt)
        {
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnFirstLayout);
            position = DialogPlacement.GetCenteredRect(position.size);
        }

        // showsEmptySections: 항목이 없는 종류도 구역을 만들고 '없음'을 표시할지
        protected void BuildLayout(string title, string description, IEnumerable<LocalizedText> notices, IReadOnlyList<PackageEntry> packages, bool showsEmptySections)
        {
            titleContent = new GUIContent(title);
            minSize = new Vector2(MIN_WIDTH, MIN_HEIGHT);

            var content = ResourceLoader.GetUXML(UXML_FILE_NAME);
            content.style.flexGrow = 1;
            rootVisualElement.Add(content);

            content.Q<Label>("DescriptionLabel").text = description;
            var noticeContainer = content.Q<VisualElement>("NoticeContainer");
            foreach (var notice in notices)
            {
                var label = new Label(notice.GetText());
                label.AddToClassList("apply-dialog__notice");
                // 실패 사유 원문을 복사할 수 있게 한다.
                label.selection.isSelectable = true;
                noticeContainer.Add(label);
            }
            noticeContainer.style.display = noticeContainer.childCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _toolbarContainer = content.Q<VisualElement>("ToolbarContainer");

            var sectionView = content.Q<ScrollView>("SectionView");
            foreach (PackageType type in Enum.GetValues(typeof(PackageType)))
            {
                var typePackages = packages
                    .Where(x => x.type == type)
                    .OrderBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (typePackages.Count == 0 && !showsEmptySections)
                {
                    continue;
                }

                // 종류 탭 라벨과 같은 이름을 쓴다.
                var header = new Label($"{type} ({typePackages.Count})");
                header.AddToClassList("package-section__header");
                header.AddToClassList("apply-dialog__section-header");
                sectionView.Add(header);
                if (typePackages.Count == 0)
                {
                    var emptyLabel = new Label(LocalizationManager.Get("dialog.section.empty"));
                    emptyLabel.AddToClassList("package-list__empty");
                    sectionView.Add(emptyLabel);
                    continue;
                }
                foreach (var package in typePackages)
                {
                    sectionView.Add(CreateRow(package));
                }
            }

            _summaryLabel = content.Q<Label>("SummaryLabel");
            ConfirmButton = content.Q<Button>("ConfirmButton");
            ConfirmButton.clicked += () =>
            {
                _isConfirmed = true;
                Close();
            };
        }

        protected abstract VisualElement CreateRow(PackageEntry package);

        // 패키지 목록 행과 같은 높이·여백의 빈 행
        protected VisualElement CreateRowContainer()
        {
            var row = new VisualElement();
            row.AddToClassList("package-row");
            return row;
        }

        // 행 끝에 배지를 붙인다. modifierClasses 는 기본(초록) 대신 색을 바꾸는 USS 클래스
        protected Label AddBadge(VisualElement row, string text, params string[] modifierClasses)
        {
            var badge = StatusBadge.Create(text, modifierClasses.Prepend("apply-dialog__badge").ToArray());
            row.Add(badge);
            return badge;
        }

        // 업데이트가 있으면 '업데이트 현재 → 최신', 고정된 Git 리비전이면 '고정됨(리비전)' 배지를 붙인다.
        protected void AddUpdateBadge(VisualElement row, PackageUpdateInfo update)
        {
            if (update.IsPinned)
            {
                AddBadge(row, LocalizationManager.Get("dialog.badge.pinned", update.CurrentVersion), "apply-dialog__badge--pinned");
                return;
            }
            AddBadge(row, LocalizationManager.Get("dialog.badge.update", update.CurrentVersion, update.LatestVersion), "apply-dialog__badge--update");
        }

        // 목록 위 도구 줄에 버튼을 추가한다. 버튼이 없으면 도구 줄을 보여주지 않는다.
        protected void AddToolbarButton(string text, Action onClick)
        {
            _toolbarContainer.Add(new Button(onClick) { text = text });
            _toolbarContainer.style.display = DisplayStyle.Flex;
        }

        protected void SetSummary(string text)
        {
            _summaryLabel.text = text;
        }
    }
}
