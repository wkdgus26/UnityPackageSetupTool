using System;
using UnityEditor;
using UnityEngine;
using static PackageSetupTool.PackageSetupToolConventions;

namespace PackageSetupTool
{
    public class PackageSetupToolWindow : EditorWindow
    {
        private PackageSetupToolRoot _root;

        [MenuItem(MENU_ROOT + MENU_PACKAGE_SETUP_TOOL)]
        public static void ShowWindow()
        {
            var window = GetWindow<PackageSetupToolWindow>();
            window.titleContent = new GUIContent(MENU_PACKAGE_SETUP_TOOL);
        }

        private void OnEnable()
        {
            var root = rootVisualElement;
            _root = new PackageSetupToolRoot(ShowToolNotification);
            root.Add(_root);
            minSize = new Vector2(MIN_WINDOW_WIDTH, minSize.y);
        }

        // 줄이 많을수록 읽을 시간을 더 준다.
        private void ShowToolNotification(string text)
        {
            int lineCount = text.Split('\n').Length;
            double seconds = Math.Min(NOTIFICATION_BASE_SECONDS + lineCount * NOTIFICATION_SECONDS_PER_LINE, NOTIFICATION_MAX_SECONDS);
            ShowNotification(new GUIContent(text), seconds);
        }

        private void OnDisable()
        {
            var root = rootVisualElement;
            root.Clear();
            _root = null;
        }

        // 다른 에디터(프로젝트)가 공용 폴더의 프리셋·카탈로그를 바꿨을 수 있다.
        private void OnFocus()
        {
            // 에디터 시작 때 창 배치를 복원하면 OnEnable 보다 OnFocus 가 먼저 불린다(6000.6 실측).
            // 이때는 이어지는 OnEnable 이 루트를 만들면서 공용 폴더를 새로 읽으므로 넘어간다.
            if (_root == null)
            {
                return;
            }
            _root.ReloadIfChangedElsewhere();
        }
    }
}
