using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace PackageSetupTool
{
    // 검색 가능한 레지스트리 패키지 전체(공개 API)
    public class UnityRegistrySource : PackageSourceBase
    {
        private SearchRequest _searchRequest;

        public override PackageType Type => PackageType.UnityRegistry;

        public override void Begin(CollectContext context)
        {
            _searchRequest = Client.SearchAll(false);
        }

        public override LocalizedText GetProgressText()
        {
            return new LocalizedText("busy.source.searching", Type);
        }

        public override void Tick()
        {
            if (!_searchRequest.IsCompleted)
            {
                return;
            }
            if (_searchRequest.Status == StatusCode.Failure)
            {
                Fail(new RawText(_searchRequest.Error.message));
                return;
            }

            foreach (var packageInfo in _searchRequest.Result)
            {
                Entries.Add(new PackageEntry(Type, packageInfo.name, packageInfo.displayName));
                Descriptions[packageInfo.name] = packageInfo.description;
            }
            Complete();
        }
    }
}
