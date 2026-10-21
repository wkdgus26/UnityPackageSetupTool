using System.Collections.Generic;

namespace PackageSetupTool
{
    // 목록 수집 소스. 새 패키지 종류는 PackageType 값과 이 클래스의 파생 클래스(매개변수 없는 생성자)를 추가해 확장한다.
    // PackageSetupSystem 이 TypeCache 로 파생 클래스를 찾아 PackageType 순서로 실행한다.
    public abstract class PackageSourceBase
    {
        public abstract PackageType Type { get; }

        public bool IsDone { get; private set; }
        public LocalizedText Error { get; private set; }
        public List<PackageEntry> Entries { get; } = new();
        // 항목 id → 설명. 설명을 얻지 못한 항목은 넣지 않는다.
        public Dictionary<string, string> Descriptions { get; } = new();
        // 수집은 성공했지만 결과와 함께 알릴 내용(설명 일부 누락 등)
        public List<LocalizedText> Notices { get; } = new();

        public abstract void Begin(CollectContext context);

        // 매 에디터 업데이트마다 호출된다. 콜백으로 완료되는 소스는 비워 둔다.
        public abstract void Tick();

        // 진행 줄에 보여줄 진행 중인 단계(끝나기 전에만 부른다). 예: 'MyAssets 설명 120/220'
        public abstract LocalizedText GetProgressText();

        protected void Complete()
        {
            IsDone = true;
        }

        protected void Fail(LocalizedText error)
        {
            Error = error;
            IsDone = true;
        }
    }
}
