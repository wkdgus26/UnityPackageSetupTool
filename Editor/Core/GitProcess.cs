using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace PackageSetupTool
{
    // git 명령 하나를 비동기로 실행하고 Tick 에서 끝났는지 본다. 표준 출력·오류를 줄 단위로 모은다.
    // git 패키지를 설치하려면 UPM 도 git 이 필요하므로 같은 실행 파일(PATH 의 git)을 쓴다.
    public class GitProcess
    {
        private readonly string _arguments;
        private readonly int _timeoutSeconds;
        // 출력은 스레드 풀에서 들어오므로 잠그고 모은다.
        private readonly List<string> _outputLines = new();
        private readonly List<string> _errorLines = new();
        private Process _process;
        private DateTime _deadline;

        public GitProcess(string arguments, int timeoutSeconds)
        {
            _arguments = arguments;
            _timeoutSeconds = timeoutSeconds;
        }

        public bool IsDone { get; private set; }
        // 끝까지 실행됐을 때의 종료 코드
        public int ExitCode { get; private set; }
        // 실행하지 못했거나 시간 상한을 넘긴 사유. 끝까지 실행됐으면 null
        public LocalizedText Error { get; private set; }
        public IReadOnlyList<string> OutputLines => GetLines(_outputLines);
        public string ErrorText => string.Join(" ", GetLines(_errorLines));

        public void Begin()
        {
            var startInfo = new ProcessStartInfo("git", _arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // 인증이 필요한 저장소에서 입력을 기다리며 멈추지 않게 한다. 터미널 프롬프트와 Git Credential Manager 의 로그인 창을 모두 끈다.
            startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            // 오류 문장으로 실패 종류를 가려내므로, 시스템 언어와 관계없이 git 메시지를 영어로 고정한다.
            startInfo.EnvironmentVariables["LC_ALL"] = "C";

            _process = new Process { StartInfo = startInfo };
            _process.OutputDataReceived += (_, args) => AddLine(_outputLines, args.Data);
            _process.ErrorDataReceived += (_, args) => AddLine(_errorLines, args.Data);
            // git 이 설치돼 있지 않거나 PATH 에 없으면 Start 가 예외를 던진다. 외부 실행 파일이 없는 것은 정상 흐름의 실패라 사유로 바꿔 알린다.
            try
            {
                _process.Start();
            }
            catch (Win32Exception exception)
            {
                // Mono 의 예외 메시지는 시작 정보 전체와 NUL 문자가 섞여 있어(6000.6 실측) 오류 코드만 알린다.
                Complete(new LocalizedText("git.notFound", exception.NativeErrorCode));
                return;
            }
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _deadline = DateTime.UtcNow.AddSeconds(_timeoutSeconds);
        }

        public void Tick()
        {
            if (IsDone)
            {
                return;
            }
            if (!_process.HasExited)
            {
                if (DateTime.UtcNow > _deadline)
                {
                    _process.Kill();
                    Complete(new LocalizedText("check.update.timeoutSeconds", _timeoutSeconds));
                }
                return;
            }

            // 비동기 출력이 끝까지 들어오도록 기다린다(이미 끝난 프로세스라 바로 돌아온다).
            _process.WaitForExit();
            ExitCode = _process.ExitCode;
            Complete(null);
        }

        // 결과를 버릴 때 아직 실행 중이면 끝낸다.
        public void Stop()
        {
            if (IsDone)
            {
                return;
            }
            if (!_process.HasExited)
            {
                _process.Kill();
            }
            Complete(null);
        }

        private void Complete(LocalizedText error)
        {
            Error = error;
            IsDone = true;
            _process?.Dispose();
        }

        private void AddLine(List<string> lines, string line)
        {
            // 스트림이 끝나면 null 이 한 번 들어온다.
            if (line == null)
            {
                return;
            }
            lock (lines)
            {
                lines.Add(line);
            }
        }

        private List<string> GetLines(List<string> lines)
        {
            lock (lines)
            {
                return lines.ToList();
            }
        }
    }
}
