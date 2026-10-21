using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PackageSetupTool
{
    // 도구가 띄우는 모달 창의 위치. 도구 창이 열려 있으면 그 가운데에 놓는다.
    // 도구 창이 없을 때(메뉴에서 연 도구 제거 확인, 도구 창을 닫은 뒤의 제거 실패 알림)는 Unity 메인 창 가운데에 놓는다.
    public static class DialogPlacement
    {
        public static Rect GetCenteredRect(Vector2 size)
        {
            var toolWindow = Resources.FindObjectsOfTypeAll<PackageSetupToolWindow>().FirstOrDefault();
            Rect anchor = toolWindow != null ? toolWindow.position : EditorGUIUtility.GetMainWindowPosition();
            return new Rect(anchor.center - size / 2f, size);
        }
    }
}
