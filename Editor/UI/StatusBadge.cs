using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // 행 안의 작은 둥근 상태 표시(적용 진행 오버레이의 상태·결과, 적용 확인 창의 설치됨·업데이트 등).
    // 모양은 status-badge, 위치별 여백과 색은 classNames 의 USS 클래스로 정한다.
    public static class StatusBadge
    {
        private const string BadgeClass = "status-badge";

        public static Label Create(string text, params string[] classNames)
        {
            var badge = new Label(text);
            badge.AddToClassList(BadgeClass);
            foreach (string className in classNames)
            {
                badge.AddToClassList(className);
            }
            return badge;
        }
    }
}
