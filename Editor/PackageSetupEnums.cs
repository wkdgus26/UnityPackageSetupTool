namespace PackageSetupTool
{
    public enum PackageType { UnityRegistry, MyAssets, Git }

    // Library 상태 파일에 정수로 저장되므로 새 값은 끝에 추가한다.
    // CheckingInstalled: 프리셋 적용 전 설치 상태 확인, CheckingGitUrl: 추가할 git URL 확인, RemovingTool: 이 도구를 manifest 에서 빼는 요청 중
    public enum PackageSetupState { Idle, Collecting, Applying, RegisteringInstalled, CheckingInstalled, CheckingGitUrl, RemovingTool }

    // Unverified: 확인 자체를 할 수 없음(git 없음, 네트워크·인증 실패, 시간 초과, 짧은 커밋 해시)
    // MultipleCandidates: 저장소 루트에 package.json 이 없고 안에서 여러 개를 찾음(사용자가 고른다)
    public enum GitUrlCheckOutcome { Valid, Invalid, Unverified, MultipleCandidates }

    public enum GitProbePhase { Clone, Init, Fetch, ListTree, ReadManifest }

    // Unverified: 완료 여부를 확인할 수 없음(성공·실패 어느 쪽으로도 집계하지 않음)
    public enum ApplyOutcome { Success, Skipped, Failed, Unverified }

    // 프리셋 적용 중 항목 하나의 진행 상태. 저장하지 않고 단계 상태·결과 기록에서 매번 계산한다.
    public enum ApplyItemState { Waiting, InProgress, Done }

    // 메인 목록 배지용 현재 프로젝트의 설치 상태. 저장하지 않는다.
    // Installed: 직접 설치(Asset Store 는 임포트됨), Dependency: 다른 패키지의 의존성으로만 설치됨, BuiltIn: 에디터에 포함된 패키지(출처 BuiltIn)
    public enum PackageInstallState { NotInstalled, Installed, Dependency, BuiltIn }

    public enum UpmInstallPhase { CheckInstalled, Installing, Verify }

    // 확인 창(ConfirmDialogWindow)에서 고른 버튼. 창 닫기(X)·Esc 는 Cancel
    public enum ConfirmDialogChoice { Confirm, Cancel, Alternative }

    public enum AssetStoreFeature { Purchases, Download, LocalInfo, ImportedInfo, ImportedInfoScan, Install, CachePath, SelectInMyAssets, ProductDetail, UpdateInfo }
}
