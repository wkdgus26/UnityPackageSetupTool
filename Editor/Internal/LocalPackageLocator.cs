using System;
using System.Collections.Generic;
using System.IO;

namespace PackageSetupTool
{
    // productId 로 다운로드된 .unitypackage 경로를 찾는다.
    // Package Manager 내부 정보(LocalInfo)를 쓸 수 있으면 그것만 쓰고,
    // 쓸 수 없는 버전에서는 캐시 폴더의 패키지 헤더를 직접 읽는다.
    public class LocalPackageLocator
    {
        private readonly AssetStoreInternalBridge _bridge;
        private Dictionary<long, string> _scannedPaths;
        private bool _isInternalInfoRefreshed;

        public LocalPackageLocator(AssetStoreInternalBridge bridge)
        {
            _bridge = bridge;
        }

        public bool UsesInternalInfo => _bridge.IsAvailable(AssetStoreFeature.LocalInfo);

        public string GetPath(long productId)
        {
            if (UsesInternalInfo)
            {
                if (!_isInternalInfoRefreshed)
                {
                    _bridge.RefreshLocalInfos();
                    _isInternalInfoRefreshed = true;
                }
                // LocalInfo 는 캐시라 파일이 지워진 뒤에도 남아 있을 수 있다.
                string internalPath = _bridge.GetLocalPackagePath(productId);
                return !string.IsNullOrEmpty(internalPath) && File.Exists(internalPath) ? internalPath : null;
            }

            _scannedPaths ??= AssetStorePackageFileReader.ScanCache(GetCacheRootPath());
            return _scannedPaths.TryGetValue(productId, out string path) ? path : null;
        }

        // 로컬 파일의 업로드 ID 와 버전 문자열. Package Manager 내부 정보로만 알 수 있어 UsesInternalInfo 이고 GetPath 가 경로를 돌려준 상품에만 부른다.
        public (long uploadId, string versionString) GetVersion(long productId)
        {
            return _bridge.GetLocalVersion(productId) ?? throw new InvalidOperationException($"No local info for product {productId}.");
        }

        // 다운로드 중에는 새 파일이 생기므로 다음 조회 때 캐시 폴더를 다시 읽게 한다.
        public void Invalidate()
        {
            _scannedPaths = null;
            _isInternalInfoRefreshed = false;
        }

        private string GetCacheRootPath()
        {
            return _bridge.IsAvailable(AssetStoreFeature.CachePath)
                ? _bridge.GetCacheRootPath()
                : AssetStorePackageFileReader.GetDefaultCacheRootPath();
        }
    }
}
