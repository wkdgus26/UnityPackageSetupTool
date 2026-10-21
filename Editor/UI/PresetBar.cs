using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 프리셋 탭 줄. 프리셋마다 이름만 보이는 탭을 두고, 끝의 '+'로 새 프리셋을 만든다.
    // 클릭은 선택, 더블클릭은 이름 입력칸, 우클릭은 관리 메뉴다.
    // 생성·복제·이름 변경·선택의 실제 처리는 이벤트로 넘기고, 여기서는 이름 입력칸의 확정·취소만 다룬다.
    public class PresetBar : VisualElement
    {
        private const string UXML_FILE_NAME = "PresetBar.uxml";
        private const string TabClass = "preset-tab";
        private const string SelectedTabClass = "preset-tab--selected";
        private const string EditingTabClass = "preset-tab--editing";
        private const string TabLabelClass = "preset-tab__label";
        private const string TabFieldClass = "preset-tab__field";

        public Action<PackagePresetData> OnTriggerSelect;
        public Action<PackagePresetData, string> OnTriggerRename;
        public Action<string> OnTriggerCreate;
        // (원본 프리셋, 새 이름)
        public Action<PackagePresetData, string> OnTriggerDuplicate;
        // 입력한 이름을 쓸 수 없을 때 그 사유
        public Action<LocalizedText> OnTriggerNameError;
        public Action<PackagePresetData, DropdownMenu> OnTriggerContextMenu;
        // (이름, 이름을 바꾸는 프리셋 또는 새로 만들면 null) → 쓸 수 없으면 사유, 쓸 수 있으면 null
        public Func<string, PackagePresetData, LocalizedText> ValidateName;

        private readonly VisualElement _tabRow;
        private readonly Button _addButton;
        private readonly Dictionary<PackagePresetData, VisualElement> _tabs = new();

        // 이름 입력 중인 탭. 새 프리셋이면 _editingPreset 이 null 이다.
        private VisualElement _editingTab;
        private TextField _editingField;
        private PackagePresetData _editingPreset;
        // 새 탭이 복제면 그 원본. 새 프리셋이면 null
        private PackagePresetData _duplicateSource;

        public PresetBar()
        {
            var content = ResourceLoader.GetUXML(UXML_FILE_NAME);
            Add(content);
            _tabRow = content.Q<VisualElement>("PresetTabRow");
            _addButton = content.Q<Button>("AddPresetButton");
            _addButton.clicked += BeginCreate;
            ApplyLanguage();
        }

        public void ApplyLanguage()
        {
            _addButton.tooltip = LocalizationManager.Get("ui.newPreset.tooltip");
        }

        // 탭을 다시 만든다. 입력 중이던 이름은 버린다.
        public void SetPresets(IEnumerable<PackagePresetData> presets)
        {
            CloseEditor();
            foreach (var tab in _tabs.Values)
            {
                tab.RemoveFromHierarchy();
            }
            _tabs.Clear();
            foreach (var preset in presets)
            {
                var tab = CreateTab(preset);
                _tabRow.Insert(_tabRow.IndexOf(_addButton), tab);
                _tabs.Add(preset, tab);
            }
        }

        public void SetSelected(PackagePresetData preset)
        {
            foreach (var pair in _tabs)
            {
                pair.Value.EnableInClassList(SelectedTabClass, pair.Key == preset);
            }
        }

        public void BeginRename(PackagePresetData preset)
        {
            CloseEditor();
            OpenEditor(_tabs[preset], preset, preset.name);
        }

        // 끝에 새 탭을 열고 '<이름> 복사본'을 채운 이름 입력칸을 띄운다. 확정하면 OnTriggerDuplicate 로 넘긴다.
        public void BeginDuplicate(PackagePresetData source)
        {
            OpenNewTabEditor(source, LocalizationManager.Get("ui.preset.copyName", source.name));
        }

        private void BeginCreate()
        {
            OpenNewTabEditor(null, string.Empty);
        }

        private void OpenNewTabEditor(PackagePresetData duplicateSource, string initialName)
        {
            CloseEditor();
            var tab = new VisualElement();
            tab.AddToClassList(TabClass);
            _tabRow.Insert(_tabRow.IndexOf(_addButton), tab);
            OpenEditor(tab, null, initialName);
            _duplicateSource = duplicateSource;
        }

        private VisualElement CreateTab(PackagePresetData preset)
        {
            var tab = new VisualElement { tooltip = preset.name };
            tab.AddToClassList(TabClass);
            var label = new Label(preset.name);
            label.AddToClassList(TabLabelClass);
            tab.Add(label);
            tab.RegisterCallback<ClickEvent>(evt => OnClickTab(evt, tab, preset));
            tab.AddManipulator(new ContextualMenuManipulator(evt => OnPopulateTabMenu(evt, preset)));
            return tab;
        }

        private void OnClickTab(ClickEvent evt, VisualElement tab, PackagePresetData preset)
        {
            // 입력칸 안의 클릭은 탭 선택으로 보지 않는다.
            if (evt.button != 0 || tab == _editingTab)
            {
                return;
            }
            if (evt.clickCount == 2)
            {
                BeginRename(preset);
            }
            else
            {
                OnTriggerSelect?.Invoke(preset);
            }
        }

        // 우클릭한 프리셋을 먼저 선택해 메뉴 항목이 그 프리셋을 대상으로 하게 한다.
        private void OnPopulateTabMenu(ContextualMenuPopulateEvent evt, PackagePresetData preset)
        {
            OnTriggerSelect?.Invoke(preset);
            OnTriggerContextMenu?.Invoke(preset, evt.menu);
        }

        private void OpenEditor(VisualElement tab, PackagePresetData preset, string initialName)
        {
            _editingTab = tab;
            _editingPreset = preset;
            _editingField = new TextField();
            _editingField.SetValueWithoutNotify(initialName);
            _editingField.AddToClassList(TabFieldClass);
            _editingField.RegisterCallback<KeyDownEvent>(OnEditorKeyDown, TrickleDown.TrickleDown);
            _editingField.RegisterCallback<FocusOutEvent>(OnEditorFocusOut);
            _editingField.RegisterCallback<GeometryChangedEvent>(FocusEditorOnce);
            tab.AddToClassList(EditingTabClass);
            if (preset != null)
            {
                tab.Q<Label>(className: TabLabelClass).style.display = DisplayStyle.None;
            }
            tab.Add(_editingField);
        }

        // 패널에 붙어 첫 레이아웃이 끝난 뒤에야 포커스를 줄 수 있다.
        private void FocusEditorOnce(GeometryChangedEvent evt)
        {
            var field = (TextField)evt.currentTarget;
            field.UnregisterCallback<GeometryChangedEvent>(FocusEditorOnce);
            field.Focus();
            field.SelectAll();
        }

        private void OnEditorKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    evt.StopPropagation();
                    CommitEditor(true);
                    break;
                case KeyCode.Escape:
                    evt.StopPropagation();
                    CloseEditor();
                    break;
            }
        }

        // 다른 곳을 누르면 확정한다. 쓸 수 없는 이름이면 입력을 버리고 사유만 알린다.
        private void OnEditorFocusOut(FocusOutEvent evt)
        {
            CommitEditor(false);
        }

        private void CommitEditor(bool keepOpenOnError)
        {
            // Enter 로 확정하며 입력칸을 치우면 포커스 해제가 한 번 더 들어온다.
            if (_editingTab == null)
            {
                return;
            }
            string presetName = _editingField.value;
            var preset = _editingPreset;
            var duplicateSource = _duplicateSource;
            // 새 프리셋인데 이름이 비었거나, 이름이 그대로면 아무것도 하지 않는다.
            bool isUnchanged = preset == null ? string.IsNullOrWhiteSpace(presetName) : presetName.Trim() == preset.name;
            if (isUnchanged)
            {
                CloseEditor();
                return;
            }
            LocalizedText error = ValidateName(presetName, preset);
            if (error != null)
            {
                OnTriggerNameError?.Invoke(error);
                if (!keepOpenOnError)
                {
                    CloseEditor();
                }
                return;
            }
            CloseEditor();
            if (duplicateSource != null)
            {
                OnTriggerDuplicate?.Invoke(duplicateSource, presetName);
            }
            else if (preset == null)
            {
                OnTriggerCreate?.Invoke(presetName);
            }
            else
            {
                OnTriggerRename?.Invoke(preset, presetName);
            }
        }

        private void CloseEditor()
        {
            if (_editingTab == null)
            {
                return;
            }
            // 입력칸을 치우는 중에 들어오는 포커스 해제가 닫힌 상태를 보도록 먼저 비운다.
            var tab = _editingTab;
            var field = _editingField;
            var preset = _editingPreset;
            _editingTab = null;
            _editingField = null;
            _editingPreset = null;
            _duplicateSource = null;

            field.RemoveFromHierarchy();
            tab.RemoveFromClassList(EditingTabClass);
            if (preset == null)
            {
                tab.RemoveFromHierarchy();
            }
            else
            {
                tab.Q<Label>(className: TabLabelClass).style.display = DisplayStyle.Flex;
            }
        }
    }
}
