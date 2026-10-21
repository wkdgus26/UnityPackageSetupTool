using UnityEngine;

namespace PackageSetupTool
{
    public static class PackageSetupToolConventions
    {
        public const string MENU_ROOT = "Package Setup/";

        public const string MENU_PACKAGE_SETUP_TOOL = "Package Setup Tool";
        public const string MENU_REMOVE_TOOL = "Remove Package Setup Tool";
        // 가장 넓은 줄은 패키지 종류 탭 머리글(6000.6 실측 393px, 프리셋 툴바 영어 284px·한국어 239px)이다. 탭 라벨 숫자 자릿수가 늘어날 여유를 둔 값이다.
        // 프리셋 탭 줄은 줄바꿈되므로 폭을 정하지 않는다.
        public const float MIN_WINDOW_WIDTH = 422f;

        public const string ASSEMBLY_NAME = "PackageSetupTool.Editor";
        public const string UXML_FOLDER = "UXML";
        public const string PRESET_FOLDER = "Presets";
        public const string LOCALIZATION_FOLDER = "Localization";
        public const string DEFAULT_PRESET_NAME = "Default";
        // 프리셋·카탈로그를 두는 공용 폴더. 프로젝트 밖(기본: <내 문서>/PackageSetupTool)에 있어 같은 PC의 모든 프로젝트가 함께 쓴다.
        public const string SHARED_FOLDER_NAME = "PackageSetupTool";
        public const string CATALOG_FILE_NAME = "Catalog.json";
        // <공용 폴더>/Presets/<프리셋 이름>.json
        public const string PRESET_FILE_EXTENSION = ".json";

        // 목록 갱신 버튼 아이콘(에디터 내장 아이콘). 다크 스킨에서는 d_ 아이콘을 에디터가 고른다.
        public const string REFRESH_ICON_NAME = "Refresh";
        // 패키지 행의 상세 보기 버튼 아이콘. Registry·Asset Store 는 Package Manager 창 아이콘, Git 은 브라우저로 여는 웹 아이콘
        public const string PACKAGE_MANAGER_ICON_NAME = "Package Manager";
        public const string WEB_LINK_ICON_NAME = "BuildSettings.Web.Small";
        // 진행 줄의 스피너(에디터 내장 아이콘 WaitSpin00~11)와 진행 줄 갱신 주기(밀리초)
        public const string BUSY_SPINNER_ICON_NAME_FORMAT = "WaitSpin{0:00}";
        public const int BUSY_SPINNER_FRAME_COUNT = 12;
        public const long BUSY_REFRESH_INTERVAL_MS = 100;
        // 도구 제거 진행 바가 한 번 차는 시간(초). 진행률을 알 수 없어 반복해서 채운다.
        public const double REMOVE_PROGRESS_CYCLE_SECONDS = 2;
        // 패키지 행 툴팁에 보이는 설명의 최대 글자 수. Asset Store 설명은 수천 자라 앞부분만 보이고 뒤에 …를 붙인다.
        public const int TOOLTIP_DESCRIPTION_MAX_LENGTH = 300;
        // 창 알림 표시 시간(초) = 기본 + 줄 수 × 줄당, 최대값까지
        public const double NOTIFICATION_BASE_SECONDS = 4;
        public const double NOTIFICATION_SECONDS_PER_LINE = 1;
        public const double NOTIFICATION_MAX_SECONDS = 10;

        public const string STATE_FILE_PATH = "Library/PackageSetupTool/PackageSetupState.asset";
        public const string SELECTED_PRESET_PREF_KEY = "PackageSetupTool.SelectedPreset";
        // 공용 폴더 경로. 사용자 단위 설정이라 프로젝트를 구분하지 않는다. 고른 적이 없으면 기본 위치다.
        public const string SHARED_FOLDER_PREF_KEY = "PackageSetupTool.SharedFolder";
        // 도구 언어. 사용자 단위 설정이라 프로젝트를 구분하지 않는다.
        public const string LANGUAGE_PREF_KEY = "PackageSetupTool.Language";
        // Unity Preferences 의 에디터 언어(문서화되지 않은 키). 도구 언어를 고른 적이 없을 때 따른다.
        public const string EDITOR_LOCALE_PREF_KEY = "Editor.kEditorLocale";
        // 고른 언어도 에디터 언어도 언어 데이터에 없을 때 쓰는 언어
        public const SystemLanguage DEFAULT_LANGUAGE = SystemLanguage.English;
    }
}
