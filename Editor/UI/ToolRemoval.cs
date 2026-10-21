using UnityEditor;
using UnityEngine;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    // 이 도구를 프로젝트에서 제거하는 메뉴와 확인 창.
    // 전부 설치된 프리셋 적용의 진행 오버레이를 닫을 때와 메뉴(Package Setup/Remove Package Setup Tool)에서 같은 확인 창을 쓴다.
    public static class ToolRemoval
    {
        // 진행 바를 띄운 시각. 반복해서 채우는 진행률의 기준이다.
        private static double _progressStartTime;

        [MenuItem(MENU_ROOT + MENU_REMOVE_TOOL)]
        private static void RemoveFromMenu()
        {
            ConfirmAndRemove(false);
        }

        // 수집·등록·적용 중이거나 Play Mode 이면 메뉴를 비활성으로 둔다.
        [MenuItem(MENU_ROOT + MENU_REMOVE_TOOL, true)]
        private static bool CanRemoveFromMenu()
        {
            var system = PackageSetupSystem.instance;
            return !system.IsBusy && !system.IsPlayModeActive;
        }

        // isAfterApply: 프리셋 적용이 끝난 뒤 묻는 경우(본문 첫 문장이 다르다)
        public static void ConfirmAndRemove(bool isAfterApply)
        {
            var dialog = ScriptableObject.CreateInstance<ConfirmDialogWindow>();
            dialog.Initialize(
                LocalizationManager.Get("dialog.removeTool.title"),
                LocalizationManager.Get(isAfterApply ? "dialog.removeTool.afterApply" : "dialog.removeTool.message"),
                LocalizationManager.Get("dialog.removeTool.ok"),
                LocalizationManager.Get("dialog.removeTool.cancel"));
            if (dialog.ShowModalAndWait() == ConfirmDialogChoice.Confirm)
            {
                Remove();
            }
        }

        // 확인 없이 제거한다. 코드가 사라진 뒤 빈 창이 남지 않도록 도구 창을 먼저 닫는다.
        // 창이 없어 진행 줄 대신 진행 바를 띄운다. 제거가 끝나거나(실패 포함) 도구 코드가 사라지는 리로드 직전에 닫는다.
        public static void Remove()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<PackageSetupToolWindow>())
            {
                window.Close();
            }
            var system = PackageSetupSystem.instance;
            system.OnToolRemovalFailed -= OnRemovalFailed;
            system.OnToolRemovalFailed += OnRemovalFailed;
            _progressStartTime = EditorApplication.timeSinceStartup;
            ShowProgressBar();
            system.RemoveTool();
            // Assets·Embedded 설치본은 휴지통으로 옮기는 동기 처리라 여기서 이미 끝났다(실패했으면 OnRemovalFailed 에서 닫았다).
            if (!system.IsBusy)
            {
                ClearProgressBar();
                return;
            }
            EditorApplication.update += UpdateProgressBar;
            AssemblyReloadEvents.beforeAssemblyReload += ClearProgressBar;
        }

        // Client.Remove 가 끝나면 닫는다. 성공하면 이어지는 리로드 직전에 beforeAssemblyReload 로도 닫힌다.
        private static void UpdateProgressBar()
        {
            if (!PackageSetupSystem.instance.IsBusy)
            {
                ClearProgressBar();
                return;
            }
            ShowProgressBar();
        }

        // 진행률을 알 수 없는 요청이라 막대를 반복해서 채워 진행 중임을 보여준다.
        private static void ShowProgressBar()
        {
            double elapsed = EditorApplication.timeSinceStartup - _progressStartTime;
            float progress = (float)(elapsed % REMOVE_PROGRESS_CYCLE_SECONDS / REMOVE_PROGRESS_CYCLE_SECONDS);
            EditorUtility.DisplayProgressBar(LocalizationManager.Get("dialog.removeTool.title"), LocalizationManager.Get("busy.removingTool"), progress);
        }

        private static void ClearProgressBar()
        {
            EditorApplication.update -= UpdateProgressBar;
            AssemblyReloadEvents.beforeAssemblyReload -= ClearProgressBar;
            EditorUtility.ClearProgressBar();
        }

        // 도구 창은 이미 닫혀 있어 알림 창(메인 창 가운데)으로 알린다.
        private static void OnRemovalFailed(LocalizedText error)
        {
            ClearProgressBar();
            PackageSetupSystem.instance.OnToolRemovalFailed -= OnRemovalFailed;
            var dialog = ScriptableObject.CreateInstance<ConfirmDialogWindow>();
            dialog.Initialize(
                LocalizationManager.Get("dialog.removeTool.title"),
                LocalizationManager.Get("dialog.removeTool.failed", error.GetText()),
                LocalizationManager.Get("dialog.removeTool.close"));
            dialog.ShowModalAndWait();
        }
    }
}
