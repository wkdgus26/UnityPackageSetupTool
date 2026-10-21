using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 프리셋 적용의 진행을 패키지 목록 위에 띄우는 오버레이. 적용 기록이 있을 때만 보인다.
    // 목록 영역 전체를 어둡게 덮어 적용 중에는 목록을 조작할 수 없게 하고, 가운데 카드에 진행 문구·전체 진행 바·개수·안내·항목별 행을 보여준다.
    // 적용 중에는 중단 버튼(OnTriggerStop)으로 멈추고, 적용이 끝나면 닫기 버튼으로 닫는다(OnTriggerClose).
    public class ApplyProgressOverlay : VisualElement
    {
        private const string UXML_FILE_NAME = "ApplyProgressOverlay.uxml";
        private const string ItemBadgeClass = "apply-item__badge";

        public Action OnTriggerStop;
        public Action OnTriggerClose;

        private readonly Label _progressLabel;
        private readonly ProgressBar _progressBar;
        private readonly Label _countLabel;
        private readonly VisualElement _noticeView;
        private readonly ScrollView _itemView;
        private readonly Button _stopButton;
        private readonly Button _closeButton;
        // 마지막으로 스크롤해 보여준 처리 중 항목. 처리 중 항목이 바뀔 때만 스크롤해 사용자의 스크롤을 덮지 않는다.
        private PackageEntry _scrolledActivePackage;

        public ApplyProgressOverlay()
        {
            AddToClassList("apply-overlay-host");
            var content = ResourceLoader.GetUXML(UXML_FILE_NAME);
            content.style.flexGrow = 1;
            Add(content);

            _progressLabel = content.Q<Label>("ProgressLabel");
            // 진행 문구·안내·사유(UPM 오류 원문 등)는 복사할 수 있게 한다.
            _progressLabel.selection.isSelectable = true;
            _progressBar = content.Q<ProgressBar>("ProgressBar");
            _countLabel = content.Q<Label>("CountLabel");
            _noticeView = content.Q<VisualElement>("NoticeView");
            _itemView = content.Q<ScrollView>("ItemView");
            _stopButton = content.Q<Button>("StopButton");
            _stopButton.clicked += () => OnTriggerStop?.Invoke();
            _closeButton = content.Q<Button>("CloseButton");
            _closeButton.clicked += () => OnTriggerClose?.Invoke();
        }

        public bool IsShown { get; private set; }

        // items 가 비어 있으면(적용한 적이 없거나 기록을 지웠으면) 숨긴다. 중단은 적용 중에만, 닫기는 적용이 끝난 뒤에만 할 수 있다.
        public void Refresh(LocalizedText progressMessage, List<ApplyItemProgress> items, IReadOnlyList<LocalizedText> notices, bool isApplying)
        {
            IsShown = items.Count > 0;
            style.display = IsShown ? DisplayStyle.Flex : DisplayStyle.None;
            _itemView.Clear();
            _noticeView.Clear();
            if (items.Count == 0)
            {
                _scrolledActivePackage = null;
                return;
            }

            _progressLabel.text = progressMessage.GetText();
            _stopButton.text = LocalizationManager.Get("ui.cancel");
            _stopButton.SetEnabled(isApplying);
            _closeButton.text = LocalizationManager.Get("ui.applyOverlay.close");
            _closeButton.SetEnabled(!isApplying);

            var doneItems = items.FindAll(x => x.State == ApplyItemState.Done);
            int CountOutcome(ApplyOutcome outcome) => doneItems.Count(x => x.Outcome == outcome);
            _progressBar.lowValue = 0;
            _progressBar.highValue = items.Count;
            _progressBar.value = doneItems.Count;
            // 모두 끝나기 전에 100%로 보이지 않도록 내림한다.
            _progressBar.title = LocalizationManager.Get("applyProgress.title", doneItems.Count, items.Count, doneItems.Count * 100 / items.Count);
            _countLabel.text = LocalizationManager.Get(
                "applyProgress.counts",
                CountOutcome(ApplyOutcome.Success),
                CountOutcome(ApplyOutcome.Skipped),
                CountOutcome(ApplyOutcome.Failed),
                CountOutcome(ApplyOutcome.Unverified),
                items.Count(x => x.State == ApplyItemState.InProgress),
                items.Count(x => x.State == ApplyItemState.Waiting));

            foreach (var notice in notices)
            {
                var noticeLabel = new Label(notice.GetText());
                noticeLabel.selection.isSelectable = true;
                _noticeView.Add(noticeLabel);
            }
            _noticeView.style.display = notices.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            VisualElement activeRow = null;
            PackageEntry activePackage = null;
            foreach (var item in items)
            {
                var row = CreateItemRow(item);
                _itemView.Add(row);
                if (activeRow == null && item.State == ApplyItemState.InProgress)
                {
                    activeRow = row;
                    activePackage = item.Package;
                }
            }

            bool isActiveChanged = activePackage != null
                && (_scrolledActivePackage == null || !_scrolledActivePackage.HasKey(activePackage.type, activePackage.id));
            if (isActiveChanged)
            {
                // 새로 추가한 행은 배치가 끝나야 위치가 정해진다. 그 사이 상태가 바뀌어 목록을 다시 그렸으면 행이 빠져 있으므로,
                // 스크롤하지 않고 기록도 남기지 않아 다시 그린 목록이 새로 예약하게 한다.
                _itemView.schedule.Execute(() =>
                {
                    if (activeRow.parent != _itemView.contentContainer)
                    {
                        return;
                    }
                    _itemView.ScrollTo(activeRow);
                    _scrolledActivePackage = activePackage;
                });
            }
        }

        private VisualElement CreateItemRow(ApplyItemProgress item)
        {
            var row = new VisualElement { tooltip = item.Package.id };
            row.AddToClassList("apply-item");
            switch (item.State)
            {
                case ApplyItemState.Waiting:
                    row.Add(StatusBadge.Create(LocalizationManager.Get("applyItem.state.waiting"), ItemBadgeClass, "apply-item__badge--waiting"));
                    break;
                case ApplyItemState.InProgress:
                    row.Add(StatusBadge.Create(LocalizationManager.Get("applyItem.state.inProgress"), ItemBadgeClass, "apply-item__badge--in-progress"));
                    break;
                case ApplyItemState.Done:
                    string outcomeClass = $"apply-item__badge--{GetOutcomeModifier(item.Outcome)}";
                    row.Add(StatusBadge.Create(LocalizationManager.Get("applyItem.state.done"), ItemBadgeClass, outcomeClass));
                    row.Add(StatusBadge.Create(item.Records[item.Records.Count - 1].GetOutcomeText(), ItemBadgeClass, outcomeClass));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item), item.State, null);
            }

            var nameLabel = new Label(item.Package.displayName);
            nameLabel.AddToClassList("apply-item__name");
            row.Add(nameLabel);
            var typeLabel = new Label(item.Package.type.ToString());
            typeLabel.AddToClassList("apply-item__type");
            row.Add(typeLabel);
            // UPM 오류 원문은 줄바꿈으로 끝나 빈 줄이 생긴다.
            var detailLabel = new Label(item.GetDetailText().TrimEnd());
            detailLabel.AddToClassList("apply-item__detail");
            detailLabel.selection.isSelectable = true;
            if (item.State == ApplyItemState.Done)
            {
                detailLabel.AddToClassList($"apply-item__detail--{GetOutcomeModifier(item.Outcome)}");
            }
            row.Add(detailLabel);
            return row;
        }

        // 결과별 배지·사유 색을 정하는 USS modifier(apply-item__badge--*, apply-item__detail--*)
        private string GetOutcomeModifier(ApplyOutcome outcome) => outcome switch
        {
            ApplyOutcome.Success => "success",
            ApplyOutcome.Skipped => "skipped",
            ApplyOutcome.Failed => "failed",
            ApplyOutcome.Unverified => "unverified",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }
}
