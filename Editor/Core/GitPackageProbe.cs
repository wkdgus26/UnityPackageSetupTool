using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PackageSetupTool
{
    // git URL 로 UPM 패키지를 설치할 수 있는지 설치하지 않고 확인한다.
    // 파일 내용 없이 트리만 얕게 받아(clone --depth 1 --filter=blob:none --no-checkout) package.json 위치를 찾고, 지정 경로의 package.json 만 읽는다.
    // 경로 없이 입력했는데 루트에 package.json 이 없으면 저장소 안에서 찾는다. 하나면 그 경로를 쓰고, 여러 개면 후보로 돌려준다(UPM 자체는 루트만 본다).
    // URL 하나에 1.2~2.5초, 임시 폴더 약 100KB(git 2.45 실측). 단계마다 git 프로세스 하나를 띄우고 Tick 에서 끝났는지 본다.
    public class GitPackageProbe
    {
        private const int TimeoutSeconds = 30;
        private const string ManifestFileName = "package.json";
        private const int MaxCandidateCount = 5;
        // 여러 에디터가 같은 임시 폴더를 쓰므로, 다른 에디터가 확인 중인 폴더를 지우지 않도록 오래된 것만 정리한다.
        private const int StaleDirectoryMinutes = 10;

        private readonly GitPackageUrl _url;
        private readonly string _workDirectory;
        // 읽을 package.json 의 저장소 안 경로. 루트에 없어 안에서 찾으면 그 경로로 바뀐다.
        private string _manifestPath;
        // 루트에 package.json 이 없어 저장소 안에서 찾은 패키지 폴더. 찾지 않았으면 null
        private string _foundPath;
        private GitProbePhase _phase;
        private GitProcess _process;
        // 받은 커밋을 가리키는 이름. clone 은 HEAD, 커밋 해시를 fetch 하면 FETCH_HEAD
        private string _treeish;

        public GitPackageProbe(string url)
        {
            _url = new GitPackageUrl(url);
            _workDirectory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            _manifestPath = GetManifestPath(_url.Path);
        }

        public bool IsDone => Result != null;
        // 실행 중인(끝났으면 마지막으로 실행한) git 단계
        public GitProbePhase Phase => _phase;
        // 끝나기 전에는 null
        public GitUrlCheckResult Result { get; private set; }

        private string TempRoot => Path.Combine(Path.GetTempPath(), "PackageSetupTool", "GitCheck");

        public void Begin()
        {
            if (!_url.IsRecognizedByUpm)
            {
                Complete(GitUrlCheckOutcome.Invalid, new LocalizedText("gitCheck.notGitUrl"), null);
                return;
            }
            DeleteStaleDirectories();

            // clone --branch 는 브랜치·태그만 받으므로 전체 커밋 해시는 빈 저장소를 만들고 그 커밋만 fetch 한다.
            if (_url.Revision != null && GitRevision.IsFullCommitHash(_url.Revision))
            {
                _treeish = "FETCH_HEAD";
                Run(GitProbePhase.Init, $"init -q \"{_workDirectory}\"");
                return;
            }
            _treeish = "HEAD";
            string branchOption = _url.Revision != null ? $"--branch \"{_url.Revision}\" " : string.Empty;
            Run(GitProbePhase.Clone, $"clone -q --depth 1 --filter=blob:none --no-checkout {branchOption}\"{_url.RepositoryUrl}\" \"{_workDirectory}\"");
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
            // git 을 실행하지 못했거나 시간 상한을 넘김
            if (_process.Error != null)
            {
                Complete(GitUrlCheckOutcome.Unverified, _process.Error, null);
                return;
            }

            switch (_phase)
            {
                case GitProbePhase.Init:
                    OnInitFinished();
                    break;
                case GitProbePhase.Clone:
                case GitProbePhase.Fetch:
                    OnDownloadFinished();
                    break;
                case GitProbePhase.ListTree:
                    OnListTreeFinished();
                    break;
                case GitProbePhase.ReadManifest:
                    OnReadManifestFinished();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_phase), _phase, null);
            }
        }

        // 결과를 버릴 때 아직 실행 중이면 끝내고 임시 폴더를 지운다.
        public void Stop()
        {
            if (IsDone)
            {
                return;
            }
            _process.Stop();
            DeleteDirectory(_workDirectory);
        }

        private void OnInitFinished()
        {
            if (_process.ExitCode != 0)
            {
                Complete(GitUrlCheckOutcome.Unverified, GetExitCodeText(), null);
                return;
            }
            Run(GitProbePhase.Fetch, $"-C \"{_workDirectory}\" fetch -q --depth 1 --filter=blob:none \"{_url.RepositoryUrl}\" {_url.Revision}");
        }

        private void OnDownloadFinished()
        {
            if (_process.ExitCode == 0)
            {
                Run(GitProbePhase.ListTree, $"-C \"{_workDirectory}\" ls-tree -r --name-only {_treeish}");
                return;
            }

            // LC_ALL=C 로 고정한 영어 메시지에서 확실히 '없음'인 경우만 무효로 보고, 나머지(네트워크·인증 등)는 확인 불가로 둔다.
            string error = _process.ErrorText;
            if (error.Contains("Repository not found") || error.Contains("does not appear to be a git repository")
                || (error.Contains("repository '") && error.Contains("' not found")))
            {
                Complete(GitUrlCheckOutcome.Invalid, new LocalizedText("gitCheck.repositoryNotFound", new RawText(error)), null);
                return;
            }
            // clone --branch: "Remote branch <rev> not found in upstream origin", 커밋 fetch: "not our ref" 또는 "couldn't find remote ref"
            bool isRevisionMissing = (error.Contains("Remote branch") && error.Contains("not found"))
                || error.Contains("not our ref") || error.Contains("couldn't find remote ref");
            if (isRevisionMissing)
            {
                // 짧은 커밋 해시는 얕은 조회로 받을 수 없어(실측) 없는 리비전과 구분하지 못한다.
                bool isShortCommit = _phase == GitProbePhase.Clone && GitRevision.IsHex(_url.Revision);
                Complete(
                    isShortCommit ? GitUrlCheckOutcome.Unverified : GitUrlCheckOutcome.Invalid,
                    new LocalizedText(isShortCommit ? "gitCheck.shortCommit" : "gitCheck.revisionNotFound", _url.Revision),
                    null);
                return;
            }
            Complete(GitUrlCheckOutcome.Unverified, GetExitCodeText(), null);
        }

        private void OnListTreeFinished()
        {
            if (_process.ExitCode != 0)
            {
                Complete(GitUrlCheckOutcome.Unverified, GetExitCodeText(), null);
                return;
            }

            var manifestPaths = _process.OutputLines
                .Where(x => x == ManifestFileName || x.EndsWith($"/{ManifestFileName}", StringComparison.Ordinal))
                .ToList();
            if (manifestPaths.Contains(_manifestPath))
            {
                ReadManifest();
                return;
            }
            if (manifestPaths.Count == 0)
            {
                Complete(GitUrlCheckOutcome.Invalid, new LocalizedText("gitCheck.noManifest"), null);
                return;
            }

            List<string> directories = GetCandidateDirectories(manifestPaths);
            // 경로를 직접 적었으면 사용자가 고른 위치이므로 다른 패키지로 바꾸지 않고 후보만 안내한다.
            if (_url.Path.Length > 0)
            {
                Complete(GitUrlCheckOutcome.Invalid, new LocalizedText("gitCheck.manifestNotAtPath", _url.Path, GetCandidateUrlsText(directories)), null);
                return;
            }
            if (directories.Count > 1)
            {
                Complete(GitUrlCheckOutcome.MultipleCandidates, null, null, directories.Select(_url.WithPath).ToList());
                return;
            }
            _foundPath = directories[0];
            _manifestPath = GetManifestPath(_foundPath);
            ReadManifest();
        }

        private void ReadManifest()
        {
            Run(GitProbePhase.ReadManifest, $"-C \"{_workDirectory}\" show \"{_treeish}:{_manifestPath}\"");
        }

        private void OnReadManifestFinished()
        {
            // 트리만 받았으므로 package.json 내용은 이때 원격에서 받는다.
            if (_process.ExitCode != 0)
            {
                Complete(GitUrlCheckOutcome.Unverified, GetExitCodeText(), null);
                return;
            }

            string json = string.Join("\n", _process.OutputLines);
            UpmPackageManifest manifest;
            // 저장소의 package.json 은 값이 보장되지 않는 외부 입력이라 형식 오류를 결과로 바꿔 알린다.
            try
            {
                manifest = JsonUtility.FromJson<UpmPackageManifest>(json);
            }
            catch (ArgumentException exception)
            {
                Complete(GitUrlCheckOutcome.Invalid, new LocalizedText("gitCheck.invalidManifest", new RawText(exception.Message)), null);
                return;
            }
            // JsonUtility 는 빈 문자열을 null 로 돌려준다.
            if (manifest == null || string.IsNullOrEmpty(manifest.name) || string.IsNullOrEmpty(manifest.version))
            {
                Complete(GitUrlCheckOutcome.Invalid, new LocalizedText("gitCheck.manifestMissingFields"), null);
                return;
            }
            Complete(GitUrlCheckOutcome.Valid, null, manifest.name);
        }

        // 찾은 package.json 의 폴더. 얕은 경로부터
        private List<string> GetCandidateDirectories(List<string> manifestPaths)
        {
            return manifestPaths
                .Select(x => x == ManifestFileName ? string.Empty : x.Substring(0, x.Length - ManifestFileName.Length - 1))
                .OrderBy(x => x.Count(c => c == '/'))
                .ThenBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        // 찾은 폴더마다 입력한 URL 에서 경로만 바꾼 URL. 최대 MaxCandidateCount 개
        private LocalizedText GetCandidateUrlsText(List<string> directories)
        {
            var urls = new RawText(string.Join("\n", directories.Take(MaxCandidateCount).Select(_url.WithPath)));
            return directories.Count > MaxCandidateCount
                ? new LocalizedText("gitCheck.candidatesWithMore", urls, directories.Count - MaxCandidateCount)
                : urls;
        }

        private LocalizedText GetExitCodeText()
        {
            return new LocalizedText("git.exitCode", _process.ExitCode, new RawText(_process.ErrorText));
        }

        private void Run(GitProbePhase phase, string arguments)
        {
            _phase = phase;
            _process = new GitProcess(arguments, TimeoutSeconds);
            _process.Begin();
            // git 을 실행하지 못하면 시작하자마자 끝난다.
            if (_process.IsDone && _process.Error != null)
            {
                Complete(GitUrlCheckOutcome.Unverified, _process.Error, null);
            }
        }

        private string GetManifestPath(string directory)
        {
            return directory.Length > 0 ? $"{directory}/{ManifestFileName}" : ManifestFileName;
        }

        private void Complete(GitUrlCheckOutcome outcome, LocalizedText message, string packageName)
        {
            Complete(outcome, message, packageName, Array.Empty<string>());
        }

        private void Complete(GitUrlCheckOutcome outcome, LocalizedText message, string packageName, IReadOnlyList<string> candidateUrls)
        {
            string url = _foundPath != null ? _url.WithPath(_foundPath) : _url.Url;
            Result = new GitUrlCheckResult(outcome, message, packageName, url, _foundPath, candidateUrls);
            DeleteDirectory(_workDirectory);
        }

        private void DeleteStaleDirectories()
        {
            if (!Directory.Exists(TempRoot))
            {
                return;
            }
            DateTime threshold = DateTime.UtcNow.AddMinutes(-StaleDirectoryMinutes);
            foreach (string directory in Directory.GetDirectories(TempRoot).Where(x => Directory.GetLastWriteTimeUtc(x) < threshold))
            {
                DeleteDirectory(directory);
            }
        }

        // 임시 폴더 정리는 실패해도 확인 결과에 영향이 없다. git 이 아직 파일을 잡고 있으면 실패할 수 있고, 남은 폴더는 다음 확인 때 오래된 것으로 지운다.
        // 파일 입출력 실패가 정상 흐름의 일부라 예외를 받아 넘긴다.
        private void DeleteDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }
            try
            {
                // git 객체 파일은 읽기 전용이라 그대로는 지워지지 않는다(Windows).
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
