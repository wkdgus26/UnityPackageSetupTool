using System.Collections.Generic;

namespace PackageSetupTool
{
    // 추가할 git URL 의 확인 결과
    public class GitUrlCheckResult
    {
        public GitUrlCheckResult(GitUrlCheckOutcome outcome, LocalizedText message, string packageName, string url, string foundPath, IReadOnlyList<string> candidateUrls)
        {
            Outcome = outcome;
            Message = message;
            PackageName = packageName;
            Url = url;
            FoundPath = foundPath;
            CandidateUrls = candidateUrls;
        }

        public GitUrlCheckOutcome Outcome { get; }
        // 무효·확인 불가의 사유. 유효하거나 후보가 여러 개면 null
        public LocalizedText Message { get; }
        // package.json 의 name. 유효할 때만 있고 그 밖에는 null
        public string PackageName { get; }
        // 추가할 URL. 저장소 루트에 package.json 이 없어 안에서 찾았으면 그 경로를 붙인 URL, 그 밖에는 입력한 URL
        public string Url { get; }
        // 저장소 루트에 package.json 이 없어 안에서 찾은 경로. 찾지 않았으면 null
        public string FoundPath { get; }
        // 저장소 안에서 package.json 을 여러 개 찾았을 때 그 경로를 붙인 URL(얕은 경로부터). 그 밖에는 빈 목록
        public IReadOnlyList<string> CandidateUrls { get; }
    }
}
