using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace PackageSetupTool
{
    // 현재 프로젝트에 설치된 git 패키지(공개 API)
    public class GitSource : PackageSourceBase
    {
        private ListRequest _listRequest;

        public override PackageType Type => PackageType.Git;

        public override void Begin(CollectContext context)
        {
            _listRequest = Client.List(true, false);
        }

        public override LocalizedText GetProgressText()
        {
            return new LocalizedText("busy.source.listing", Type);
        }

        public override void Tick()
        {
            if (!_listRequest.IsCompleted)
            {
                return;
            }
            if (_listRequest.Status == StatusCode.Failure)
            {
                Fail(new RawText(_listRequest.Error.message));
                return;
            }

            foreach (var packageInfo in _listRequest.Result)
            {
                if (packageInfo.source == PackageSource.Git && !packageInfo.IsThisTool())
                {
                    string url = packageInfo.GetGitUrl();
                    Entries.Add(new PackageEntry(Type, url, packageInfo.displayName));
                    Descriptions[url] = packageInfo.description;
                }
            }
            Complete();
        }
    }
}
