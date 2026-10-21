using System;
using System.Linq;

namespace PackageSetupTool
{
    // git ls-remote 로 원격 저장소에서 리비전(HEAD·브랜치)의 최신 커밋을 조회한다.
    // 태그로 지정했거나 원격에 그 이름의 ref 가 없는 16진수 리비전(짧은 커밋)은 고정으로 판정한다.
    // UPM 은 원격의 최신 커밋을 알려주지 않아 git 을 직접 실행한다(GitProcess).
    // 프로세스는 비동기로 실행하고 Tick 에서 끝났는지 본다(URL 하나 약 1.4초, 6000.6 실측).
    public class GitRemoteQuery
    {
        private const int TimeoutSeconds = 30;
        // UPM 은 리비전 없이 설치한 git 패키지의 git.revision 을 HEAD 로 기록한다(6000.6 실측).
        public const string HeadRevision = "HEAD";
        private const string BranchRefPrefix = "refs/heads/";
        private const string TagRefPrefix = "refs/tags/";

        private readonly string _repositoryUrl;
        private readonly string _revision;
        private GitProcess _process;

        // revision: UPM 이 기록한 리비전(PackageInfo.git.revision). 리비전 없이 설치했으면 HEAD
        public GitRemoteQuery(string repositoryUrl, string revision)
        {
            _repositoryUrl = repositoryUrl;
            _revision = revision;
        }

        public bool IsDone { get; private set; }
        public bool IsPinned { get; private set; }
        // 원격의 최신 커밋. 고정이거나 실패하면 null
        public string RemoteCommit { get; private set; }
        // 실패 사유. 성공이면 null
        public LocalizedText Error { get; private set; }

        public void Begin()
        {
            string refs = _revision == HeadRevision ? HeadRevision : $"{BranchRefPrefix}{_revision} {TagRefPrefix}{_revision}";
            _process = new GitProcess($"ls-remote \"{_repositoryUrl}\" {refs}", TimeoutSeconds);
            _process.Begin();
            // git 을 실행하지 못하면 시작하자마자 끝난다.
            if (_process.IsDone)
            {
                Complete(_process.Error);
            }
        }

        public void Tick()
        {
            if (IsDone)
            {
                return;
            }
            _process.Tick();
            if (!_process.IsDone)
            {
                return;
            }
            if (_process.Error != null)
            {
                Complete(_process.Error);
                return;
            }
            if (_process.ExitCode != 0)
            {
                Complete(new LocalizedText("git.exitCode", _process.ExitCode, new RawText(_process.ErrorText)));
                return;
            }

            // 출력 형식: "<커밋>\t<ref>"
            var refs = _process.OutputLines
                .Select(x => x.Split('\t'))
                .Where(x => x.Length == 2)
                .ToList();
            if (refs.Any(x => x[1].StartsWith(TagRefPrefix, StringComparison.Ordinal)))
            {
                IsPinned = true;
                Complete(null);
                return;
            }
            var match = refs.FirstOrDefault();
            if (match == null)
            {
                // 브랜치·태그가 아닌 16진수 리비전은 커밋으로 설치한 것이다.
                if (GitRevision.IsHex(_revision))
                {
                    IsPinned = true;
                    Complete(null);
                    return;
                }
                Complete(new LocalizedText("git.refNotFound", _revision));
                return;
            }
            RemoteCommit = match[0];
            Complete(null);
        }

        // 확인을 버릴 때 아직 실행 중이면 끝낸다.
        public void Stop()
        {
            _process.Stop();
            Complete(null);
        }

        private void Complete(LocalizedText error)
        {
            Error = error;
            IsDone = true;
        }
    }
}
