using UnityEditor;

namespace PackageSetupTool
{
    // 도메인 리로드·에디터 재시작 뒤 진행 중이던 프리셋 적용을 이어서 시작한다.
    [InitializeOnLoad]
    public static class PackageSetupBootstrap
    {
        static PackageSetupBootstrap()
        {
            // delayCall 은 에디터가 비활성(포커스 없음)일 때 처리되지 않아,
            // 적용 Tick 과 같은 update 의 첫 호출에서 재개한다.
            EditorApplication.update += ResumeOnFirstUpdate;
        }

        private static void ResumeOnFirstUpdate()
        {
            EditorApplication.update -= ResumeOnFirstUpdate;
            PackageSetupSystem.instance.ResumeAfterReload();
        }
    }
}
