using System;
using System.Linq;

namespace PackageSetupTool
{
    // UPM git 의존성 URL 을 저장소 주소·패키지 경로·리비전으로 나눈다.
    // 형식: [git+]<저장소>[?path=<저장소 안 패키지 폴더>][#<브랜치|태그|커밋>]
    public class GitPackageUrl
    {
        private const string GitPlusPrefix = "git+";
        private const string GitSuffix = ".git";
        private const string PathQueryPrefix = "path=";

        // "git+" 를 포함한, 경로·리비전을 뗀 주소
        private readonly string _baseUrl;
        private readonly bool _hasGitPlusPrefix;

        public GitPackageUrl(string url)
        {
            Url = url;
            string rest = url;
            int fragmentIndex = rest.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                string revision = rest.Substring(fragmentIndex + 1);
                Revision = revision.Length > 0 ? revision : null;
                rest = rest.Substring(0, fragmentIndex);
            }
            Path = string.Empty;
            int queryIndex = rest.IndexOf('?');
            if (queryIndex >= 0)
            {
                string pathQuery = rest.Substring(queryIndex + 1).Split('&').FirstOrDefault(x => x.StartsWith(PathQueryPrefix, StringComparison.Ordinal));
                Path = pathQuery == null ? string.Empty : pathQuery.Substring(PathQueryPrefix.Length).Trim('/');
                rest = rest.Substring(0, queryIndex);
            }
            _baseUrl = rest;
            _hasGitPlusPrefix = rest.StartsWith(GitPlusPrefix, StringComparison.OrdinalIgnoreCase);
            RepositoryUrl = _hasGitPlusPrefix ? rest.Substring(GitPlusPrefix.Length) : rest;
        }

        public string Url { get; }
        // git 명령에 넘길 저장소 주소. "git+" 접두어, "?path=", "#리비전" 을 뗀다.
        public string RepositoryUrl { get; }
        // 저장소 루트 기준 패키지 폴더. 앞뒤 '/' 를 뗀다. 루트면 빈 문자열
        public string Path { get; }
        // 없으면 null
        public string Revision { get; }

        // UPM 은 "git+" 로 시작하거나 저장소 주소가 ".git" 으로 끝날 때만 git URL 로 본다. 그 밖은 패키지 이름으로 읽어 거부한다(6000.6 실측).
        public bool IsRecognizedByUpm => _hasGitPlusPrefix || RepositoryUrl.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase);

        // 같은 저장소·리비전에서 패키지 폴더만 바꾼 URL. path 가 빈 문자열이면 루트
        public string WithPath(string path)
        {
            string pathQuery = path.Length > 0 ? $"?{PathQueryPrefix}{path}" : string.Empty;
            string fragment = Revision != null ? $"#{Revision}" : string.Empty;
            return $"{_baseUrl}{pathQuery}{fragment}";
        }
    }
}
