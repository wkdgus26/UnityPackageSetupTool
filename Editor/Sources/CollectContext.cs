namespace PackageSetupTool
{
    // 목록 수집 소스에 넘기는 데이터. 소스는 PackageSetupSystem 을 직접 알지 않는다.
    public class CollectContext
    {
        public CollectContext(AssetStoreInternalBridge bridge)
        {
            Bridge = bridge;
        }

        public AssetStoreInternalBridge Bridge { get; }
    }
}
