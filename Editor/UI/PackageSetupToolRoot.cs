using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    public class PackageSetupToolRoot : VisualElement
    {
        // 안내·결과를 창 알림으로 띄운다(EditorWindow.ShowNotification).
        private readonly Action<string> _showNotification;
        private readonly TabView _typeTabView;
        private readonly ToolbarSearchField _searchField;
        private readonly PresetBar _presetBar;
        // 공용 폴더 열기·변경 메뉴. 툴팁에 현재 경로를 보여준다.
        private readonly ToolbarMenu _sharedFolderMenu;
        private readonly Button _refreshButton;
        private readonly Button _registerInstalledButton;
        private readonly Button _applyButton;
        private readonly ToolbarMenu _languageMenu;
        private readonly VisualElement _dataErrorView;
        private readonly ApplyProgressOverlay _applyProgressOverlay;
        // 목록 갱신·확인 등 작업 중에만 보이는 진행 줄. 작업 중에는 주기적으로 문구와 스피너를 갱신한다.
        private readonly VisualElement _busyView;
        private readonly Image _busySpinner;
        private readonly Label _busyLabel;
        private readonly Texture2D[] _spinnerFrames;
        private readonly IVisualElementScheduledItem _busyRefresh;
        private int _spinnerFrameIndex;

        private readonly List<PackageTypeTab> _packageTabs = new();

        private PackageCatalog _catalog;
        private List<PackagePresetData> _presets = new();
        // 읽을 수 있는 프리셋이 없으면 null
        private PackagePresetData _currentPreset;
        // 공용 폴더에서 읽지 못한 파일·폴더의 사유. 패키지 툴바 아래 경고 줄에 보여준다.
        private readonly List<LocalizedText> _dataErrors = new();
        // 마지막으로 읽거나 저장한 때의 공용 폴더 파일 상태. 다른 에디터가 바꿨는지 비교한다.
        private string _dataStamp;
        // 현재 프리셋에서 해제한, 카탈로그에 없는 항목. 돌아갈 자리가 없어 프리셋을 바꿀 때까지 '선택 안 됨'에 남겨 둔다.
        private readonly List<PackageEntry> _releasedEntries = new();

        private PackageSetupSystem SetupSystem => PackageSetupSystem.instance;
        // git URL 추가 입력칸이 있는 탭
        private PackageTypeTab GitTab => _packageTabs.Find(x => x.PackageType == PackageType.Git);

        // 프로젝트마다 프리셋 구성이 달라 프로젝트별로 마지막 선택을 기억한다.
        private string SelectedPresetPrefKey => $"{SELECTED_PRESET_PREF_KEY}.{PlayerSettings.productGUID}";

        public PackageSetupToolRoot(Action<string> showNotification)
        {
            _showNotification = showNotification;
            style.flexGrow = 1;
            var content = ResourceLoader.GetUXML("PackageSetupToolRoot.uxml");
            content.style.flexGrow = 1;
            Add(content);

            _typeTabView = this.Q<TabView>("PackageTypeTabView");
            _searchField = this.Q<ToolbarSearchField>("PackageSearchField");
            _sharedFolderMenu = this.Q<ToolbarMenu>("SharedFolderMenu");
            _refreshButton = this.Q<Button>("RefreshButton");
            _registerInstalledButton = this.Q<Button>("RegisterInstalledButton");
            _applyButton = this.Q<Button>("ApplyButton");
            _languageMenu = this.Q<ToolbarMenu>("LanguageMenu");
            _dataErrorView = this.Q<VisualElement>("DataErrorView");
            _busyView = this.Q<VisualElement>("BusyView");
            _busySpinner = this.Q<Image>("BusySpinner");
            _busyLabel = this.Q<Label>("BusyLabel");
            _spinnerFrames = Enumerable.Range(0, BUSY_SPINNER_FRAME_COUNT)
                .Select(i => (Texture2D)EditorGUIUtility.IconContent(string.Format(BUSY_SPINNER_ICON_NAME_FORMAT, i)).image)
                .ToArray();
            _busyRefresh = schedule.Execute(RefreshBusyView).Every(BUSY_REFRESH_INTERVAL_MS);
            _busyRefresh.Pause();

            // 패키지 목록 영역 전체를 덮도록 같은 영역 위에 겹쳐 둔다.
            _applyProgressOverlay = new ApplyProgressOverlay();
            _applyProgressOverlay.OnTriggerStop += OnStopApply;
            _applyProgressOverlay.OnTriggerClose += OnCloseApplyProgress;
            this.Q<VisualElement>("PackageArea").Add(_applyProgressOverlay);

            _presetBar = new PresetBar();
            _presetBar.OnTriggerSelect += OnSelectPresetTab;
            _presetBar.OnTriggerCreate += CreatePreset;
            _presetBar.OnTriggerDuplicate += DuplicatePreset;
            _presetBar.OnTriggerRename += RenamePreset;
            _presetBar.OnTriggerNameError += ShowMessage;
            _presetBar.OnTriggerContextMenu += OnPopulatePresetMenu;
            _presetBar.ValidateName = PresetStore.ValidateName;
            this.Q<VisualElement>("PresetBarContainer").Add(_presetBar);

            foreach (PackageType type in Enum.GetValues(typeof(PackageType)))
            {
                AddTypeTab(type);
            }

            ReloadData(EditorPrefs.GetString(SelectedPresetPrefKey, DEFAULT_PRESET_NAME));

            _searchField.RegisterValueChangedCallback(OnSearchFieldChanged);
            _refreshButton.iconImage = Background.FromTexture2D((Texture2D)EditorGUIUtility.IconContent(REFRESH_ICON_NAME).image);
            _refreshButton.clicked += OnClickRefresh;
            _registerInstalledButton.clicked += OnClickRegisterInstalled;
            _applyButton.clicked += OnClickApply;
            // 언어 에셋을 추가·삭제했을 수 있어 메뉴를 열 때마다 목록을 다시 만든다. 메뉴를 여는 처리보다 먼저 받도록 TrickleDown 으로 등록한다.
            _languageMenu.RegisterCallback<PointerDownEvent>(_ => RebuildLanguageMenu(), TrickleDown.TrickleDown);

            // 창을 다른 도킹 영역으로 옮기면 패널에서 떨어졌다가 다시 붙으므로 붙을 때마다 구독한다.
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);

            ApplyTexts();
            RefreshStatus();
        }

        private void OnAttachToPanel(AttachToPanelEvent evt)
        {
            SetupSystem.OnStateChanged += OnSystemStateChanged;
            SetupSystem.OnCollectFinished += OnCollectFinished;
            SetupSystem.OnInstalledScanFinished += OnInstalledScanFinished;
            SetupSystem.OnRegisterInstalledFinished += OnRegisterInstalledFinished;
            SetupSystem.OnInstallCheckFinished += OnInstallCheckFinished;
            SetupSystem.OnGitUrlCheckFinished += OnGitUrlCheckFinished;
            SetupSystem.OnInstallStatesChanged += OnInstallStatesChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            LocalizationManager.OnLanguageChanged += OnLanguageChanged;
            // 설치 상태 배지: 창이 붙을 때(열기·리로드 뒤), UPM 패키지가 바뀔 때, 에셋 패키지 임포트가 끝날 때 다시 판정한다.
            UnityEditor.PackageManager.Events.registeredPackages += OnRegisteredPackages;
            AssetDatabase.importPackageCompleted += OnImportPackageCompleted;
            SetupSystem.RefreshInstallStates();
            // Play Mode 진입 리로드로 창이 다시 만들어지면 진입 이벤트를 받기 전에 붙는다.
            if (SetupSystem.IsPlayModeActive)
            {
                ShowMessage(new LocalizedText("message.playModeUnavailable"));
            }
            OnSystemStateChanged();
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            SetupSystem.OnStateChanged -= OnSystemStateChanged;
            SetupSystem.OnCollectFinished -= OnCollectFinished;
            SetupSystem.OnInstalledScanFinished -= OnInstalledScanFinished;
            SetupSystem.OnRegisterInstalledFinished -= OnRegisterInstalledFinished;
            SetupSystem.OnInstallCheckFinished -= OnInstallCheckFinished;
            SetupSystem.OnGitUrlCheckFinished -= OnGitUrlCheckFinished;
            SetupSystem.OnInstallStatesChanged -= OnInstallStatesChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            LocalizationManager.OnLanguageChanged -= OnLanguageChanged;
            UnityEditor.PackageManager.Events.registeredPackages -= OnRegisteredPackages;
            AssetDatabase.importPackageCompleted -= OnImportPackageCompleted;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // 진입 직전(ExitingEditMode)에는 수집·적용 중이라 시스템이 진입을 취소할 수 있어 갱신하지 않는다.
            // 실제로 진입하면 리로드로 창이 다시 만들어지거나(EnteredPlayMode) 이후 상태 변경 때 갱신된다.
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                return;
            }
            RefreshButtons();
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                ShowMessage(new LocalizedText("message.playModeUnavailable"));
            }
        }

        private void AddTypeTab(PackageType type)
        {
            var typeTab = new PackageTypeTab(type);
            typeTab.OnTriggerToggle += OnUpdatePackageSelection;
            typeTab.OnTriggerDelete += OnDeleteManualPackage;
            typeTab.OnTriggerShowDetails += entry => SetupSystem.ShowDetails(entry);
            typeTab.OnTriggerAddGitUrl += OnAddGitUrl;
            _typeTabView.Add(typeTab.Tab);
            _packageTabs.Add(typeTab);
        }

        // 탭을 바꿔도 같은 검색 결과를 보도록 모든 탭에 적용한다.
        private void OnSearchFieldChanged(ChangeEvent<string> evt)
        {
            _packageTabs.ForEach(x => x.UpdateElementsVisible(evt.newValue));
        }

        #region Shared data

        // 창에 포커스가 올 때 부른다. 다른 에디터가 공용 폴더를 바꿨을 때만 다시 읽어, 바뀐 것이 없으면 목록 스크롤을 그대로 둔다.
        public void ReloadIfChangedElsewhere()
        {
            if (_dataStamp != SharedDataFolder.GetModificationStamp())
            {
                ReloadData(_currentPreset?.name);
            }
        }

        private void ChangeSharedFolder()
        {
            // 진행 결과가 다른 폴더에 저장되지 않게 한다.
            if (SetupSystem.IsBusy)
            {
                ShowMessage(new LocalizedText("message.sharedFolderBusy"));
                return;
            }
            string currentPath = SharedDataFolder.FolderPath;
            string selectedPath = EditorUtility.OpenFolderPanel(LocalizationManager.Get("dialog.changeSharedFolder.title"), currentPath, string.Empty);
            // 폴더 창에서 취소
            if (string.IsNullOrEmpty(selectedPath) || SharedDataFolder.IsSameFolder(selectedPath, currentPath))
            {
                return;
            }

            // 새 폴더에 데이터가 있으면(다른 PC에서 동기화한 폴더 등) 그 데이터를 그대로 쓴다.
            if (SharedDataFolder.HasToolData(currentPath) && !SharedDataFolder.HasToolData(selectedPath))
            {
                var copyDialog = ScriptableObject.CreateInstance<ConfirmDialogWindow>();
                copyDialog.Initialize(
                    LocalizationManager.Get("dialog.copySharedData.title"),
                    LocalizationManager.Get("dialog.copySharedData.message", currentPath, selectedPath),
                    LocalizationManager.Get("dialog.copySharedData.ok"),
                    LocalizationManager.Get("dialog.cancel"),
                    LocalizationManager.Get("dialog.copySharedData.alt"));
                // Confirm: 복사, Cancel: 취소(창 닫기 포함), Alternative: 복사하지 않음
                ConfirmDialogChoice choice = copyDialog.ShowModalAndWait();
                if (choice == ConfirmDialogChoice.Cancel)
                {
                    return;
                }
                if (choice == ConfirmDialogChoice.Confirm)
                {
                    LocalizedText error = SharedDataFolder.CopyData(currentPath, selectedPath);
                    if (error != null)
                    {
                        ShowError(new LocalizedText("message.sharedFolderCopyFailed", error));
                        return;
                    }
                }
            }
            SharedDataFolder.SetFolderPath(selectedPath);
            ShowMessage(new LocalizedText("message.sharedFolderChanged", SharedDataFolder.FolderPath));
            ReloadData(_currentPreset?.name);
        }

        private void OpenSharedFolder()
        {
            LocalizedText error = SharedDataFolder.Open();
            if (error != null)
            {
                ShowError(error);
            }
        }

        // 공용 폴더에서 카탈로그·프리셋을 다시 읽고 다시 그린다. 그 이름의 프리셋이 없으면 첫 프리셋을 고른다.
        private void ReloadData(string presetNameToSelect)
        {
            _dataErrors.Clear();
            _presets = PresetStore.LoadAllOrCreateDefault(_dataErrors);
            LocalizedText catalogError = CatalogStore.Load(out _catalog);
            if (catalogError != null)
            {
                _dataErrors.Add(catalogError);
            }
            _dataStamp = SharedDataFolder.GetModificationStamp();
            RefreshSharedFolderTooltip();
            foreach (var error in _dataErrors)
            {
                Debug.LogWarning($"[Package Setup] {error.GetText()}");
            }

            _presetBar.SetPresets(_presets);
            SelectPreset(_presets.Find(x => x.name == presetNameToSelect) ?? _presets.FirstOrDefault());
            RefreshStatus();
        }

        // 메모리에 적용한 변경을 공용 폴더 파일 최신본에도 적용한다. 카탈로그를 먼저 쓴다.
        // 실패하면 사유를 알리고 파일 내용으로 다시 그린다. 다른 곳에서 바뀐 내용이 있었으면 그것까지 보이도록 다시 읽는다.
        private bool SaveData(Action<PackageCatalog> catalogChange, Action<PackagePresetData> presetChange)
        {
            bool isChangedElsewhere = _dataStamp != SharedDataFolder.GetModificationStamp();
            LocalizedText error = null;
            // 바꿀 것이 없는 쪽은 null 로 넘긴다.
            if (catalogChange != null)
            {
                error = CatalogStore.Update(catalogChange);
            }
            if (error == null && presetChange != null)
            {
                error = PresetStore.Update(_currentPreset.name, presetChange);
            }

            if (error != null)
            {
                ShowError(error);
                ReloadData(_currentPreset.name);
                return false;
            }
            if (isChangedElsewhere)
            {
                ReloadData(_currentPreset.name);
            }
            else
            {
                _dataStamp = SharedDataFolder.GetModificationStamp();
            }
            return true;
        }

        #endregion

        #region Preset

        private void SelectPreset(PackagePresetData preset)
        {
            // 다시 읽으면 객체가 바뀌므로 이름으로 같은 프리셋인지 본다.
            if (preset?.name != _currentPreset?.name)
            {
                _releasedEntries.Clear();
            }
            _currentPreset = preset;
            if (preset != null)
            {
                EditorPrefs.SetString(SelectedPresetPrefKey, preset.name);
            }
            _presetBar.SetSelected(preset);
            RebuildTabs();
            RefreshButtons();
        }

        // 이미 고른 탭을 다시 누르면 목록을 다시 만들지 않는다(스크롤 위치 유지).
        private void OnSelectPresetTab(PackagePresetData preset)
        {
            if (preset != _currentPreset)
            {
                SelectPreset(preset);
            }
        }

        // 프리셋 탭 우클릭 메뉴. 탭 줄이 메뉴를 열기 전에 그 프리셋을 선택해 둔다.
        private void OnPopulatePresetMenu(PackagePresetData preset, DropdownMenu menu)
        {
            menu.AppendAction(LocalizationManager.Get("menu.rename"), _ => _presetBar.BeginRename(preset));
            menu.AppendAction(LocalizationManager.Get("menu.duplicate"), _ => _presetBar.BeginDuplicate(preset));
            menu.AppendSeparator();
            menu.AppendAction(LocalizationManager.Get("menu.delete"), _ => OnClickDeletePreset());
        }

        private void CreatePreset(string presetName)
        {
            presetName = presetName.Trim();
            LocalizedText error = PresetStore.Create(presetName);
            if (error != null)
            {
                ShowError(new LocalizedText("message.presetCreateFailed", error));
                ReloadData(_currentPreset?.name);
                return;
            }
            ShowMessage(new LocalizedText("message.presetCreated", presetName));
            ReloadData(presetName);
        }

        // 원본 파일의 최신본을 복사한다. 그 사이 원본이 지워졌거나 읽을 수 없게 됐으면 실패를 알린다.
        private void DuplicatePreset(PackagePresetData source, string presetName)
        {
            presetName = presetName.Trim();
            LocalizedText error = PresetStore.Duplicate(source.name, presetName);
            if (error != null)
            {
                ShowError(new LocalizedText("message.presetDuplicateFailed", error));
                ReloadData(_currentPreset?.name);
                return;
            }
            ShowMessage(new LocalizedText("message.presetDuplicated", source.name, presetName));
            ReloadData(presetName);
        }

        private void RenamePreset(PackagePresetData preset, string presetName)
        {
            string oldName = preset.name;
            string newName = presetName.Trim();
            LocalizedText error = PresetStore.Rename(oldName, newName);
            if (error != null)
            {
                ShowError(new LocalizedText("message.presetRenameFailed", error));
                ReloadData(oldName);
                return;
            }
            ShowMessage(new LocalizedText("message.presetRenamed", oldName, newName));
            ReloadData(newName);
        }

        private void OnClickDeletePreset()
        {
            string presetName = _currentPreset.name;
            var deleteDialog = ScriptableObject.CreateInstance<ConfirmDialogWindow>();
            deleteDialog.Initialize(
                LocalizationManager.Get("dialog.deletePreset.title"),
                LocalizationManager.Get("dialog.deletePreset.message", presetName),
                LocalizationManager.Get("dialog.deletePreset.ok"),
                LocalizationManager.Get("dialog.cancel"));
            if (deleteDialog.ShowModalAndWait() != ConfirmDialogChoice.Confirm)
            {
                return;
            }
            LocalizedText error = PresetStore.Delete(presetName);
            if (error != null)
            {
                ShowError(new LocalizedText("message.presetDeleteFailed", error));
                ReloadData(presetName);
                return;
            }
            ShowMessage(new LocalizedText("message.presetDeleted", presetName));
            ReloadData(null);
        }

        #endregion

        #region Package list

        private void RebuildTabs()
        {
            // 프리셋이 없으면 목록은 보여주되 체크된 항목이 없다(종류 탭은 RefreshButtons 에서 비활성).
            var presetPackages = _currentPreset?.packages ?? new List<PackageEntry>();
            foreach (var tab in _packageTabs)
            {
                var items = _catalog.GetEntries(tab.PackageType)
                    .Select(x => (x, presetPackages.Exists(y => y.HasKey(x.type, x.id)), true, _catalog.GetDescription(x.type, x.id), SetupSystem.GetDetailsUnavailableReason(x)))
                    .ToList();
                // 다른 프로젝트에서 고른 항목처럼 카탈로그에 없는 프리셋 항목도 보여준다. 해제한 것은 '선택 안 됨'에 남긴다.
                items.AddRange(presetPackages.Where(x => x.type == tab.PackageType)
                    .Where(x => _catalog.Find(x.type, x.id) == null)
                    .Select(x => (x, true, false, (string)null, SetupSystem.GetDetailsUnavailableReason(x))));
                items.AddRange(_releasedEntries
                    .Where(x => x.type == tab.PackageType && _catalog.Find(x.type, x.id) == null)
                    .Select(x => (x, false, false, (string)null, SetupSystem.GetDetailsUnavailableReason(x))));
                tab.SetEntries(items);
                tab.UpdateElementsVisible(_searchField.value);
                tab.UpdateInstallStates(SetupSystem.GetInstallState);
            }
        }

        private void OnInstallStatesChanged()
        {
            _packageTabs.ForEach(x => x.UpdateInstallStates(SetupSystem.GetInstallState));
        }

        private void OnRegisteredPackages(UnityEditor.PackageManager.PackageRegistrationEventArgs args)
        {
            SetupSystem.RefreshInstallStates();
        }

        private void OnImportPackageCompleted(string packageName)
        {
            SetupSystem.RefreshInstallStates();
        }

        private void OnUpdatePackageSelection(PackageEntry entry, bool isIncluded)
        {
            _releasedEntries.RemoveAll(x => x.HasKey(entry.type, entry.id));
            if (!isIncluded && _catalog.Find(entry.type, entry.id) == null)
            {
                _releasedEntries.Add(entry.Clone());
            }
            _currentPreset.SetIncluded(entry, isIncluded);
            SaveData(null, x => x.SetIncluded(entry, isIncluded));
            RefreshButtons();
        }

        // 추가하지 않거나 확인하지 못한 결과는 입력칸 바로 아래에 보여준다.
        private void OnAddGitUrl(string url)
        {
            GitTab.ClearGitAddMessage();
            url = url.Trim();
            if (string.IsNullOrEmpty(url))
            {
                GitTab.ShowGitAddError(new LocalizedText("message.gitUrlRequired"));
                return;
            }
            if (IsGitUrlDuplicated(url))
            {
                GitTab.ShowGitAddError(new LocalizedText("message.gitUrlDuplicated", url));
                return;
            }
            // 설치할 수 있는 URL 인지 확인한 뒤 OnGitUrlCheckFinished 에서 추가한다.
            SetupSystem.StartGitUrlCheck(url);
        }

        private void OnGitUrlCheckFinished(string url, GitUrlCheckResult result)
        {
            if (result.Outcome == GitUrlCheckOutcome.Invalid)
            {
                // 고쳐서 다시 추가할 수 있도록 입력 칸은 비우지 않는다.
                GitTab.ShowGitAddError(new LocalizedText("message.gitUrlInvalid", url, result.Message));
                return;
            }
            // 저장소 루트에 package.json 이 없고 안에서 여러 개를 찾았으면 사용자가 고른 것만 추가한다.
            if (result.Outcome == GitUrlCheckOutcome.MultipleCandidates)
            {
                GitTab.ShowGitCandidateMenu(result.CandidateUrls);
                return;
            }
            // 저장소 안에서 찾았으면 입력한 URL 이 아니라 그 경로를 붙인 URL 을 추가한다.
            url = result.Url;
            // 확인하는 동안 다른 에디터가 같은 URL 을 추가했을 수 있다. 저장소 안에서 찾은 URL 은 여기서 처음 비교한다.
            if (IsGitUrlDuplicated(url))
            {
                GitTab.ShowGitAddError(new LocalizedText("message.gitUrlDuplicated", url));
                return;
            }

            var entry = new PackageEntry(PackageType.Git, url, url, true);
            _catalog.AddOrUpdate(entry);
            _currentPreset.SetIncluded(entry, true);
            if (!SaveData(x => x.AddOrUpdate(entry), x => x.SetIncluded(entry, true)))
            {
                return;
            }
            // 값을 바꿔도 알림이 가지 않으므로 아래 경고는 다음 입력 때까지 남는다.
            GitTab.ClearGitUrlField();
            if (result.Outcome == GitUrlCheckOutcome.Valid && result.FoundPath != null)
            {
                ShowMessage(new LocalizedText("message.gitUrlAddedFromSubfolder", result.FoundPath, _currentPreset.name, url, result.PackageName));
            }
            else if (result.Outcome == GitUrlCheckOutcome.Valid)
            {
                ShowMessage(new LocalizedText("message.gitUrlAddedWithName", _currentPreset.name, url, result.PackageName));
            }
            else
            {
                GitTab.ShowGitAddWarning(new LocalizedText("message.gitUrlAddedUnverified", _currentPreset.name, url, result.Message));
            }
            RebuildTabs();
            RefreshButtons();
        }

        private bool IsGitUrlDuplicated(string url)
        {
            return _catalog.Find(PackageType.Git, url) != null || _currentPreset.Contains(PackageType.Git, url);
        }

        // 직접 추가한 git 항목을 목록과 현재 프리셋에서 뺀다. 다른 프리셋에는 '목록에 없음'으로 남는다.
        private void OnDeleteManualPackage(PackageEntry entry)
        {
            _catalog.Remove(entry.type, entry.id);
            _releasedEntries.RemoveAll(x => x.HasKey(entry.type, entry.id));
            _currentPreset.SetIncluded(entry, false);
            if (!SaveData(x => x.Remove(entry.type, entry.id), x => x.SetIncluded(entry, false)))
            {
                return;
            }
            ShowMessage(new LocalizedText("message.packageRemoved", entry.id));
            RebuildTabs();
            RefreshButtons();
        }

        // 시스템이 공용 폴더의 카탈로그에 결과를 저장했으므로 다시 읽는다.
        private void OnCollectFinished()
        {
            ReloadData(_currentPreset?.name);
        }

        // 시스템이 공용 폴더의 카탈로그·프리셋에 결과를 저장했으므로 다시 읽는다. 다시 체크된 항목은 해제 목록에서 뺀다.
        private void OnRegisterInstalledFinished()
        {
            ReloadData(_currentPreset?.name);
            if (_currentPreset == null)
            {
                return;
            }
            _releasedEntries.RemoveAll(x => _currentPreset.Contains(x.type, x.id));
            RebuildTabs();
        }

        #endregion

        #region Actions

        // 결과를 저장할 수 없으면(카탈로그 파일을 읽을 수 없음) 시작하지 않는다.
        private void OnClickRefresh()
        {
            LocalizedText blockReason = CatalogStore.GetWriteBlockReason();
            if (blockReason != null)
            {
                ShowMessage(blockReason);
                return;
            }
            SetupSystem.StartCollect();
        }

        private void OnClickRegisterInstalled()
        {
            LocalizedText blockReason = CatalogStore.GetWriteBlockReason();
            if (blockReason != null)
            {
                ShowMessage(blockReason);
                return;
            }
            SetupSystem.StartRegisterInstalled(_currentPreset);
        }

        // 검사 결과에서 등록할 항목을 고르는 창을 띄운다. 창을 닫으면(X) 아무것도 저장하지 않는다.
        private void OnInstalledScanFinished(InstalledScanResult scanResult)
        {
            var selectWindow = ScriptableObject.CreateInstance<RegisterSelectWindow>();
            selectWindow.Initialize(scanResult);
            if (!selectWindow.ShowModalAndWait())
            {
                ShowMessage(new LocalizedText("message.registerCancelled", scanResult.Preset.name));
                return;
            }
            SetupSystem.RegisterInstalled(scanResult, selectWindow.SelectedPackages);
        }

        // 확인 창에 이미 설치된 항목을 표시하기 위해 설치 상태부터 확인한다. 창은 확인이 끝나면(OnInstallCheckFinished) 띄운다.
        private void OnClickApply()
        {
            SetupSystem.StartInstallCheck(_currentPreset);
        }

        // 적용할 목록 창 → (이미 설치된 항목이 있으면) 재임포트·제외 선택 창 → 적용 시작. 어느 창에서든 취소하면 적용하지 않는다.
        private void OnInstallCheckFinished(InstallCheckResult checkResult)
        {
            var preset = checkResult.Preset;
            var confirmWindow = ScriptableObject.CreateInstance<ApplyConfirmWindow>();
            confirmWindow.Initialize(checkResult);
            if (!confirmWindow.ShowModalAndWait())
            {
                ShowMessage(new LocalizedText("message.applyCancelled", preset.name));
                return;
            }

            var excludedPackages = new List<PackageEntry>();
            var reinstallPackages = new List<PackageEntry>();
            if (checkResult.InstalledPackages.Count > 0)
            {
                var reinstallWindow = ScriptableObject.CreateInstance<ReinstallSelectWindow>();
                reinstallWindow.Initialize(checkResult);
                if (!reinstallWindow.ShowModalAndWait())
                {
                    ShowMessage(new LocalizedText("message.applyCancelled", preset.name));
                    return;
                }
                excludedPackages = reinstallWindow.ExcludedPackages;
                reinstallPackages = reinstallWindow.ReinstallPackages;
            }
            SetupSystem.StartApply(checkResult, excludedPackages, reinstallPackages);
        }

        // 전부 설치된 적용의 결과를 닫으면 도구를 제거할지 묻는다. 기록을 지운 뒤 물으므로 남겨 두면 다시 묻지 않는다.
        private void OnCloseApplyProgress()
        {
            bool isFullyCompleted = SetupSystem.IsLastApplyFullyCompleted();
            SetupSystem.ClearApplyProgress();
            if (isFullyCompleted)
            {
                ToolRemoval.ConfirmAndRemove(true);
            }
        }

        private void OnStopApply()
        {
            var stopDialog = ScriptableObject.CreateInstance<ConfirmDialogWindow>();
            stopDialog.Initialize(
                LocalizationManager.Get("dialog.stopApply.title"),
                LocalizationManager.Get("dialog.stopApply.message"),
                LocalizationManager.Get("dialog.stopApply.ok"),
                LocalizationManager.Get("dialog.stopApply.cancel"));
            if (stopDialog.ShowModalAndWait() != ConfirmDialogChoice.Confirm)
            {
                return;
            }
            // 확인 창을 띄운 동안 적용이 끝났을 수 있다.
            if (SetupSystem.State == PackageSetupState.Applying)
            {
                SetupSystem.Cancel();
            }
        }

        #endregion

        #region Status

        // 시스템 상태가 바뀌면 다시 그리고, 시스템이 쌓아 둔 알림(목록 갱신·등록 결과, 확인 실패, 리로드로 중단)을 한 번에 띄운다.
        private void OnSystemStateChanged()
        {
            RefreshStatus();
            List<LocalizedText> notices = SetupSystem.TakeNotices();
            if (notices.Count > 0)
            {
                _showNotification(string.Join("\n", notices.Select(x => x.GetText())));
            }
        }

        private void RefreshStatus()
        {
            RefreshDataErrors();
            RefreshBusyView();
            _applyProgressOverlay.Refresh(
                SetupSystem.ProgressMessage,
                SetupSystem.GetApplyProgress(),
                SetupSystem.Messages,
                SetupSystem.State == PackageSetupState.Applying);
            RefreshButtons();
        }

        // 세부 진행은 상태 변경 알림 없이 바뀌므로 작업 중에는 주기적으로 다시 그리고, 작업이 없으면 숨기고 멈춘다.
        private void RefreshBusyView()
        {
            string busyText = SetupSystem.GetBusyProgressText();
            if (busyText == null)
            {
                _busyView.style.display = DisplayStyle.None;
                _busyRefresh.Pause();
                return;
            }
            _busyView.style.display = DisplayStyle.Flex;
            _busyLabel.text = busyText;
            _spinnerFrameIndex = (_spinnerFrameIndex + 1) % _spinnerFrames.Length;
            _busySpinner.image = _spinnerFrames[_spinnerFrameIndex];
            _busyRefresh.Resume();
        }

        // 공용 폴더에서 읽지 못한 파일은 고칠 때까지 패키지 툴바 아래에 계속 보여준다.
        private void RefreshDataErrors()
        {
            _dataErrorView.Clear();
            foreach (var error in _dataErrors)
            {
                // 파일 경로·사유를 복사할 수 있게 한다.
                var errorLabel = new Label(error.GetText());
                errorLabel.selection.isSelectable = true;
                _dataErrorView.Add(errorLabel);
            }
            _dataErrorView.style.display = _dataErrors.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RefreshButtons()
        {
            bool isIdle = !SetupSystem.IsBusy;
            bool canStart = isIdle && !SetupSystem.IsPlayModeActive;
            // 읽을 수 있는 프리셋이 없으면 새 프리셋 만들기(탭 줄의 +)만 할 수 있다.
            bool hasPreset = _currentPreset != null;
            _refreshButton.SetEnabled(canStart);
            _registerInstalledButton.SetEnabled(canStart && hasPreset);
            _applyButton.SetEnabled(canStart && hasPreset && _currentPreset.packages.Count > 0);
            _presetBar.SetEnabled(isIdle);
            // 적용 진행 오버레이가 덮고 있는 동안에는 목록을 조작하지 못하게 한다.
            _typeTabView.SetEnabled(hasPreset && !_applyProgressOverlay.IsShown);
            // git URL 추가는 확인(git 실행)을 거치므로 다른 작업 중이거나 Play Mode 에서는 막는다.
            GitTab.SetGitUrlInputEnabled(canStart);
        }

        private void ShowMessage(LocalizedText message)
        {
            _showNotification(message.GetText());
        }

        private void ShowError(LocalizedText message)
        {
            ShowMessage(message);
            Debug.LogError($"[Package Setup] {message.GetText()}");
        }

        #endregion

        #region Language

        // 창의 모든 문장을 현재 언어로 다시 그린다. 저장된 적용 진행 문구·결과와 공용 폴더 오류도 포함한다. 이미 떠 있는 창 알림은 그대로 둔다.
        private void OnLanguageChanged()
        {
            ApplyTexts();
            _presetBar.ApplyLanguage();
            _packageTabs.ForEach(x => x.ApplyLanguage());
            RebuildTabs();
            RefreshStatus();
        }

        // 이 요소가 직접 가진 고정 문장. 프리셋 탭 줄과 패키지 종류 탭은 각자 만들 때 적용한다.
        private void ApplyTexts()
        {
            _applyButton.text = LocalizationManager.Get("ui.apply");
            _sharedFolderMenu.text = LocalizationManager.Get("ui.sharedFolder");
            RefreshSharedFolderTooltip();
            RebuildSharedFolderMenu();
            _refreshButton.tooltip = LocalizationManager.Get("ui.refresh.tooltip");
            _registerInstalledButton.text = LocalizationManager.Get("ui.registerInstalled");
            _registerInstalledButton.tooltip = LocalizationManager.Get("ui.registerInstalled.tooltip");
            _languageMenu.text = LocalizationManager.CurrentLanguage.displayName;
            _languageMenu.tooltip = LocalizationManager.Get("ui.language.tooltip");
            RebuildLanguageMenu();
        }

        // 진행 중에는 결과가 다른 폴더에 저장되지 않도록 변경 항목을 비활성으로 보여준다.
        private void RebuildSharedFolderMenu()
        {
            _sharedFolderMenu.menu.ClearItems();
            _sharedFolderMenu.menu.AppendAction(LocalizationManager.Get("menu.openSharedFolder"), _ => OpenSharedFolder());
            _sharedFolderMenu.menu.AppendAction(
                LocalizationManager.Get("menu.changeSharedFolder"),
                _ => ChangeSharedFolder(),
                _ => SetupSystem.IsBusy ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
        }

        private void RefreshSharedFolderTooltip()
        {
            _sharedFolderMenu.tooltip = LocalizationManager.Get("ui.sharedFolder.tooltip", SharedDataFolder.FolderPath);
        }

        private void RebuildLanguageMenu()
        {
            var previousLanguage = LocalizationManager.CurrentLanguage;
            LocalizationManager.ReloadLanguages();
            // 쓰던 언어 에셋이 지워졌으면 다른 언어로 바뀌었으므로 창을 다시 그린다.
            if (LocalizationManager.CurrentLanguage != previousLanguage)
            {
                OnLanguageChanged();
                return;
            }

            _languageMenu.menu.ClearItems();
            foreach (var language in LocalizationManager.Languages)
            {
                _languageMenu.menu.AppendAction(
                    language.displayName,
                    _ => LocalizationManager.SetLanguage(language),
                    _ => language == LocalizationManager.CurrentLanguage ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
        }

        #endregion
    }
}
