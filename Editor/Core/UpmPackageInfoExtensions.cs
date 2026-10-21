using System.Collections.Generic;
using UnityEditor.PackageManager;

namespace PackageSetupTool
{
    public static class UpmPackageInfoExtensions
    {
        // 이 도구가 UPM 패키지(git URL 등)로 설치돼 있으면 그 패키지 이름. Assets 폴더에 있으면(.unitypackage 임포트) 패키지가 아니라 null 이다.
        private static readonly string ThisToolPackageName = PackageInfo.FindForAssembly(typeof(UpmPackageInfoExtensions).Assembly)?.name;

        // 도구 자신은 목록 수집·설치된 패키지 등록에서 뺀다. 도구를 쓰려면 이미 설치돼 있어야 하므로 프리셋에 담을 이유가 없다.
        public static bool IsThisTool(this PackageInfo packageInfo)
        {
            return packageInfo.name == ThisToolPackageName;
        }

        // git 패키지의 packageId 는 "이름@manifest에 쓴 URL" 형식이다.
        public static string GetGitUrl(this PackageInfo packageInfo)
        {
            string packageId = packageInfo.packageId;
            return packageId.Substring(packageId.IndexOf('@') + 1);
        }

        // 설치 여부 비교용 키: 모든 패키지의 이름 + git 패키지의 URL
        public static HashSet<string> GetInstalledKeys(this PackageCollection packages)
        {
            var result = new HashSet<string>();
            foreach (var packageInfo in packages)
            {
                result.Add(packageInfo.name);
                if (packageInfo.source == PackageSource.Git)
                {
                    result.Add(packageInfo.GetGitUrl());
                }
            }
            return result;
        }

        // GetInstalledKeys 와 같은 키(이름 또는 git URL)로 패키지를 찾는다. 없으면 null
        public static PackageInfo FindByKey(this PackageCollection packages, string key)
        {
            foreach (var packageInfo in packages)
            {
                if (packageInfo.name == key || (packageInfo.source == PackageSource.Git && packageInfo.GetGitUrl() == key))
                {
                    return packageInfo;
                }
            }
            return null;
        }

        // 결과 로그에 남기는 버전. git 패키지는 버전이 그대로여도 커밋이 바뀌므로 커밋 해시 앞 7자를 붙인다.
        public static string GetVersionLabel(this PackageInfo packageInfo)
        {
            if (packageInfo.source != PackageSource.Git)
            {
                return packageInfo.version;
            }
            return $"{packageInfo.version} ({GitRevision.ToShortCommit(packageInfo.git.hash)})";
        }
    }
}
