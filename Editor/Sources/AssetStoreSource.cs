using System.Collections.Generic;
using UnityEditor;

namespace PackageSetupTool
{
    // 로그인한 계정의 Asset Store 구매 목록(My Assets). 내부 REST API를 페이지 단위로 반복 요청한다.
    // 구매 목록에는 설명이 없어, 목록을 다 받은 뒤 상품마다 상세 정보를 병렬로 요청해 설명을 모은다.
    public class AssetStoreSource : PackageSourceBase
    {
        private const int PageSize = 100;
        private const int MaxConcurrentDescriptionRequests = 10;

        private readonly Queue<long> _pendingDescriptionIds = new();

        private AssetStoreInternalBridge _bridge;
        private int _receivedCount;
        // 구매 목록 전체 수. 첫 페이지를 받기 전에는 -1
        private long _totalCount = -1;
        private bool _isRequestingDescriptions;
        private int _runningDescriptionCount;
        private int _failedDescriptionCount;
        private string _firstDescriptionError;

        public override PackageType Type => PackageType.MyAssets;

        public override void Begin(CollectContext context)
        {
            _bridge = context.Bridge;
            if (!_bridge.IsAvailable(AssetStoreFeature.Purchases))
            {
                Fail(_bridge.GetMissingMessage(AssetStoreFeature.Purchases));
                return;
            }
            if (string.IsNullOrEmpty(CloudProjectSettings.userId))
            {
                Fail(new LocalizedText("collect.assetStoreLoginRequired"));
                return;
            }

            RequestPage();
        }

        public override LocalizedText GetProgressText()
        {
            if (_isRequestingDescriptions)
            {
                int finishedCount = Entries.Count - _pendingDescriptionIds.Count - _runningDescriptionCount;
                return new LocalizedText("busy.source.descriptions", Type, finishedCount, Entries.Count);
            }
            if (_totalCount < 0)
            {
                return new LocalizedText("busy.source.purchasesRequesting", Type);
            }
            return new LocalizedText("busy.source.purchases", Type, _receivedCount, _totalCount);
        }

        // 설명 요청은 콜백 안에서 다음 요청을 보내지 않고 여기서 채운다. 콜백이 동기로 와도 중첩되지 않게 하기 위함이다.
        public override void Tick()
        {
            if (!_isRequestingDescriptions)
            {
                return;
            }

            while (_runningDescriptionCount < MaxConcurrentDescriptionRequests && _pendingDescriptionIds.Count > 0)
            {
                long productId = _pendingDescriptionIds.Dequeue();
                _runningDescriptionCount++;
                _bridge.RequestProductDescription(productId, description => OnDescriptionReceived(productId, description), OnDescriptionFailed);
            }
            if (_runningDescriptionCount > 0 || _pendingDescriptionIds.Count > 0)
            {
                return;
            }

            if (_failedDescriptionCount > 0)
            {
                Notices.Add(new LocalizedText("collect.descriptionFailed", Type, _failedDescriptionCount, new RawText(_firstDescriptionError)));
            }
            _isRequestingDescriptions = false;
            Complete();
        }

        private void RequestPage()
        {
            _bridge.RequestPurchases(_receivedCount, PageSize, OnPageReceived, error => Fail(new RawText(error)));
        }

        private void OnPageReceived(List<(long productId, string displayName)> purchases, long total)
        {
            foreach (var (productId, displayName) in purchases)
            {
                Entries.Add(new PackageEntry(Type, productId.ToString(), displayName));
            }
            _receivedCount += purchases.Count;
            _totalCount = total;

            if (purchases.Count == 0 || _receivedCount >= total)
            {
                BeginDescriptionRequests();
                return;
            }
            RequestPage();
        }

        private void BeginDescriptionRequests()
        {
            // 설명을 얻는 내부 멤버가 없어도 구매 목록은 쓸 수 있으므로 실패로 처리하지 않고 알리기만 한다.
            if (!_bridge.IsAvailable(AssetStoreFeature.ProductDetail))
            {
                Notices.Add(new LocalizedText("collect.descriptionUnavailable", Type, _bridge.GetMissingMessage(AssetStoreFeature.ProductDetail)));
                Complete();
                return;
            }

            foreach (var entry in Entries)
            {
                _pendingDescriptionIds.Enqueue(long.Parse(entry.id));
            }
            _isRequestingDescriptions = true;
        }

        private void OnDescriptionReceived(long productId, string description)
        {
            Descriptions[productId.ToString()] = description;
            _runningDescriptionCount--;
        }

        private void OnDescriptionFailed(string error)
        {
            _failedDescriptionCount++;
            _firstDescriptionError ??= error;
            _runningDescriptionCount--;
        }
    }
}
