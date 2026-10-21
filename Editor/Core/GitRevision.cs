using System;
using System.Linq;

namespace PackageSetupTool
{
    // git 리비전(브랜치·태그·커밋) 문자열 판정과 결과 표시용 짧은 커밋 해시
    public static class GitRevision
    {
        // 결과 로그·배지에 보여주는 커밋 해시 길이
        private const int ShortCommitLength = 7;
        private const int FullCommitLength = 40;

        // 16진수로만 된 브랜치·태그 이름도 있어 전체 길이(40자)만 커밋으로 본다.
        public static bool IsFullCommitHash(string revision)
        {
            return revision.Length == FullCommitLength && IsHex(revision);
        }

        // 16진수로만 된 리비전. 브랜치·태그가 아니면 짧은 커밋 해시로 본다.
        public static bool IsHex(string revision)
        {
            return revision.All(Uri.IsHexDigit);
        }

        public static string ToShortCommit(string commitHash)
        {
            return commitHash.Substring(0, ShortCommitLength);
        }
    }
}
