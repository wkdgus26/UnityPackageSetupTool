using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PackageSetupTool
{
    // Package Manager 내부 API 접근을 이 클래스 한 곳에 모은다.
    // 버전마다 바뀌는 private 필드와 구현 클래스는 쓰지 않고, 내부 인터페이스(IAssetStore*, ILocalInfoHandler, IDelayedSelectionHandler)와 ServicesContainer.Resolve<T>()만 쓴다.
    // 사용하는 멤버는 Unity 6000.3~6000.6에서 공통으로 존재함을 확인했다(업데이트 확인 UpdateInfo 는 6000.6 에서만 실측). 없는 멤버는 기능별 누락 목록으로 보고한다.
    public class AssetStoreInternalBridge
    {
        private const string InternalNamespace = "UnityEditor.PackageManager.UI.Internal.";
        private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string CompletedDownloadState = "Completed";
        // 내부 MyAssetsPage.k_Id (6000.0~6000.6 동일)
        private const string MyAssetsPageId = "MyAssets";

        private readonly Assembly _editorAssembly = typeof(EditorWindow).Assembly;
        private readonly Dictionary<AssetStoreFeature, List<string>> _missingMembers = new();
        private readonly Dictionary<Type, object> _services = new();
        private readonly List<(object target, EventInfo eventInfo, Delegate handler)> _downloadEventHandlers = new();

        private object _servicesContainer;
        private MethodInfo _resolveMethod;

        private Type _restApiType;
        private MethodInfo _getPurchasesMethod;
        private ConstructorInfo _queryArgsConstructor;
        private FieldInfo _queryStartIndexField;
        private FieldInfo _queryLimitField;
        private FieldInfo _purchasesTotalField;
        private FieldInfo _purchasesListField;
        private FieldInfo _purchaseProductIdField;
        private FieldInfo _purchaseDisplayNameField;
        private PropertyInfo _purchasesErrorMessageProperty;
        private MethodInfo _getProductDetailMethod;
        private FieldInfo _productDescriptionField;
        private PropertyInfo _productDetailErrorMessageProperty;

        private Type _downloadManagerType;
        private MethodInfo _downloadMethod;
        private MethodInfo _abortDownloadMethod;
        private EventInfo _downloadFinalizedEvent;
        private EventInfo _downloadErrorEvent;
        private EventInfo _downloadProgressEvent;
        private PropertyInfo _operationProductIdProperty;
        private PropertyInfo _operationStateProperty;
        private PropertyInfo _operationErrorMessageProperty;
        private PropertyInfo _downloadErrorMessageProperty;

        private Type _cacheType;
        private MethodInfo _getLocalInfoMethod;
        private FieldInfo _localInfoPackagePathField;
        private Type _localInfoHandlerType;
        private MethodInfo _getParsedLocalInfosMethod;
        private MethodInfo _setLocalInfosMethod;
        private MethodInfo _getImportedPackageMethod;
        private Type _assetStoreClientType;
        private MethodInfo _fullScanImportedAssetsMethod;
        // 이 객체(도메인 리로드 전까지)에서 임포트 기록을 채웠는지. 채운 뒤의 기록은 PM이 갱신한다.
        private bool _isImportedPackagesFilled;
        private PropertyInfo _importedAssetsProperty;

        private Type _installerType;
        private MethodInfo _installMethod;

        private Type _cachePathProxyType;
        private MethodInfo _getCachePathConfigMethod;
        private PropertyInfo _cachePathProperty;

        private Type _selectionHandlerType;
        private MethodInfo _selectPackageMethod;

        private MethodInfo _getUpdateDetailMethod;
        private ConstructorInfo _updateArgsConstructor;
        private FieldInfo _updateProductIdField;
        private FieldInfo _updateRecommendedUploadIdField;
        private PropertyInfo _updateErrorMessageProperty;
        private FieldInfo _localInfoUploadIdField;
        private FieldInfo _localInfoVersionStringField;
        private PropertyInfo _importedUploadIdProperty;
        private PropertyInfo _importedVersionStringProperty;
        private FieldInfo _productVersionStringField;

        public AssetStoreInternalBridge()
        {
            BindServicesContainer();
            BindPurchases();
            BindDownload();
            BindLocalInfo();
            BindImportedInfo();
            BindInstall();
            BindCachePath();
            BindSelectInMyAssets();
            BindProductDetail();
            BindUpdateInfo();
        }

        public bool IsAvailable(AssetStoreFeature feature)
        {
            return GetMissingMembers(feature).Count == 0;
        }

        public LocalizedText GetMissingMessage(AssetStoreFeature feature)
        {
            return new LocalizedText("bridge.missingMembers", Application.unityVersion, string.Join(", ", GetMissingMembers(feature)));
        }

        // 구매 목록 한 페이지를 요청한다. 결과는 에디터 메인 스레드 콜백으로 온다.
        public void RequestPurchases(
            int startIndex,
            int limit,
            Action<List<(long productId, string displayName)>, long> onSuccess,
            Action<string> onError)
        {
            object queryArgs = _queryArgsConstructor.Invoke(_queryArgsConstructor.GetParameters().Select(x => x.DefaultValue).ToArray());
            _queryStartIndexField.SetValue(queryArgs, Convert.ChangeType(startIndex, _queryStartIndexField.FieldType));
            _queryLimitField.SetValue(queryArgs, Convert.ChangeType(limit, _queryLimitField.FieldType));

            ParameterInfo[] parameters = _getPurchasesMethod.GetParameters();
            Delegate successCallback = new InternalCallback(result =>
                    onSuccess(ReadPurchases(result), Convert.ToInt64(_purchasesTotalField.GetValue(result))))
                .CreateDelegate(parameters[1].ParameterType);
            Delegate errorCallback = new InternalCallback(error =>
                    onError((string)_purchasesErrorMessageProperty.GetValue(error)))
                .CreateDelegate(parameters[2].ParameterType);

            _getPurchasesMethod.Invoke(GetService(_restApiType), new[] { queryArgs, successCallback, errorCallback });
        }

        // 상품 하나의 상세 정보(설명)를 요청한다. 구매 목록에는 설명이 없어 상품마다 따로 요청해야 한다. 결과는 에디터 메인 스레드 콜백으로 온다.
        public void RequestProductDescription(long productId, Action<string> onSuccess, Action<string> onError)
        {
            ParameterInfo[] parameters = _getProductDetailMethod.GetParameters();
            Delegate successCallback = new InternalCallback(productInfo =>
                    onSuccess((string)_productDescriptionField.GetValue(productInfo)))
                .CreateDelegate(parameters[1].ParameterType);
            Delegate errorCallback = new InternalCallback(error =>
                    onError((string)_productDetailErrorMessageProperty.GetValue(error)))
                .CreateDelegate(parameters[2].ParameterType);

            _getProductDetailMethod.Invoke(GetService(_restApiType), new object[] { productId, successCallback, errorCallback });
        }

        // 상품 하나의 스토어 최신 버전 문자열을 요청한다. 결과는 에디터 메인 스레드 콜백으로 온다.
        public void RequestProductVersion(long productId, Action<string> onSuccess, Action<string> onError)
        {
            ParameterInfo[] parameters = _getProductDetailMethod.GetParameters();
            Delegate successCallback = new InternalCallback(productInfo =>
                    onSuccess((string)_productVersionStringField.GetValue(productInfo)))
                .CreateDelegate(parameters[1].ParameterType);
            Delegate errorCallback = new InternalCallback(error =>
                    onError((string)_productDetailErrorMessageProperty.GetValue(error)))
                .CreateDelegate(parameters[2].ParameterType);

            _getProductDetailMethod.Invoke(GetService(_restApiType), new object[] { productId, successCallback, errorCallback });
        }

        // 상품마다 현재 Unity 에 권장되는 업로드 ID 를 요청한다(Package Manager 의 업데이트 확인과 같은 요청). 결과는 productId → 업로드 ID
        // 응답에 없는 상품(구매하지 않았거나 내려간 상품)은 결과에 없다. 결과는 에디터 메인 스레드 콜백으로 온다.
        public void RequestRecommendedUploadIds(IReadOnlyCollection<long> productIds, Action<Dictionary<long, long>> onSuccess, Action<string> onError)
        {
            object args = _updateArgsConstructor.Invoke(new object[] { productIds.ToList() });
            ParameterInfo[] parameters = _getUpdateDetailMethod.GetParameters();
            Delegate successCallback = new InternalCallback(updateInfos =>
            {
                var result = new Dictionary<long, long>();
                foreach (object updateInfo in (IEnumerable)updateInfos)
                {
                    result[Convert.ToInt64(_updateProductIdField.GetValue(updateInfo))] = Convert.ToInt64(_updateRecommendedUploadIdField.GetValue(updateInfo));
                }
                onSuccess(result);
            }).CreateDelegate(parameters[1].ParameterType);
            Delegate errorCallback = new InternalCallback(error =>
                    onError((string)_updateErrorMessageProperty.GetValue(error)))
                .CreateDelegate(parameters[2].ParameterType);

            _getUpdateDetailMethod.Invoke(GetService(_restApiType), new[] { args, successCallback, errorCallback });
        }

        // 로컬 .unitypackage 의 업로드 ID 와 버전 문자열. 로컬 정보가 없으면 null
        public (long uploadId, string versionString)? GetLocalVersion(long productId)
        {
            object localInfo = _getLocalInfoMethod.Invoke(GetService(_cacheType), new object[] { productId });
            if (localInfo == null)
            {
                return null;
            }
            return (Convert.ToInt64(_localInfoUploadIdField.GetValue(localInfo)), (string)_localInfoVersionStringField.GetValue(localInfo));
        }

        // 프로젝트에 임포트된 버전의 업로드 ID 와 버전 문자열. 임포트 기록이 없으면 null
        public (long uploadId, string versionString)? GetImportedVersion(long productId)
        {
            object importedPackage = _getImportedPackageMethod.Invoke(GetService(_cacheType), new object[] { productId });
            if (importedPackage == null)
            {
                return null;
            }
            return (Convert.ToInt64(_importedUploadIdProperty.GetValue(importedPackage)), (string)_importedVersionStringProperty.GetValue(importedPackage));
        }

        // 반환값은 이용약관 확인이 비동기로 진행 중일 때도 false 라 성공 여부 판단에 쓰지 않는다.
        // 진행은 이벤트와 로컬 파일 생성으로 판단한다.
        public void RequestDownload(IReadOnlyCollection<long> productIds)
        {
            _downloadMethod.Invoke(GetService(_downloadManagerType), new object[] { productIds.ToList() });
        }

        // 받는 중인 다운로드를 취소한다. 받고 있지 않은 상품이면 아무것도 하지 않는다.
        public void AbortDownload(long productId)
        {
            _abortDownloadMethod.Invoke(GetService(_downloadManagerType), new object[] { productId });
        }

        // onFinalized(productId, 완료 여부, 실패 사유), onError(productId, 오류 메시지), onProgress(productId)
        public void SubscribeDownloadEvents(Action<long, bool, LocalizedText> onFinalized, Action<long, string> onError, Action<long> onProgress)
        {
            object downloadManager = GetService(_downloadManagerType);
            AddDownloadEventHandler(downloadManager, _downloadFinalizedEvent, new InternalCallback(operation =>
            {
                // 내부 DownloadState 이름 해석은 브리지에서만 한다. Completed 외의 종료(Aborted, Error 등)는 실패로 본다.
                string state = _operationStateProperty.GetValue(operation).ToString();
                string errorMessage = (string)_operationErrorMessageProperty.GetValue(operation);
                bool isCompleted = state == CompletedDownloadState;
                onFinalized(ReadOperationProductId(operation), isCompleted,
                    isCompleted ? null : string.IsNullOrEmpty(errorMessage) ? new LocalizedText("download.failedWithState", state) : new RawText(errorMessage));
            }));
            AddDownloadEventHandler(downloadManager, _downloadErrorEvent, new InternalCallback((operation, error) =>
                onError(ReadOperationProductId(operation), (string)_downloadErrorMessageProperty.GetValue(error))));
            AddDownloadEventHandler(downloadManager, _downloadProgressEvent, new InternalCallback(operation =>
                onProgress(ReadOperationProductId(operation))));
        }

        public void UnsubscribeDownloadEvents()
        {
            foreach (var (target, eventInfo, handler) in _downloadEventHandlers)
            {
                eventInfo.RemoveEventHandler(target, handler);
            }
            _downloadEventHandlers.Clear();
        }

        // Package Manager 창을 연 적이 없는 에디터 세션에서는 LocalInfo 가 비어 있어
        // 캐시 폴더를 다시 읽어 채운다. PM의 FullScanLocalInfos(6000.5+)와 같은 처리를 6000.3+ 공통 멤버로 한다.
        public void RefreshLocalInfos()
        {
            object localInfos = _getParsedLocalInfosMethod.Invoke(GetService(_localInfoHandlerType), null);
            _setLocalInfosMethod.Invoke(GetService(_cacheType), new[] { localInfos });
        }

        // Package Manager가 알고 있는 로컬 .unitypackage 경로. 없으면 null
        public string GetLocalPackagePath(long productId)
        {
            object localInfo = _getLocalInfoMethod.Invoke(GetService(_cacheType), new object[] { productId });
            return localInfo == null ? null : (string)_localInfoPackagePathField.GetValue(localInfo);
        }

        // 내부 Install 로 임포트해 AssetOrigin 이 기록된 패키지인지
        public bool IsImported(long productId)
        {
            return _getImportedPackageMethod.Invoke(GetService(_cacheType), new object[] { productId }) != null;
        }

        // 임포트 기록도 에디터 세션 메모리에만 있어 비어 있을 수 있다. 비어 있을 때만 프로젝트의 AssetOrigin 을 읽어 채운다.
        // 전체 스캔 멤버가 없는 버전(6000.0~6000.4)이면 채우지 않고 false 를 돌려준다. 이때는 PM이 가진 기록만으로 판정하게 된다
        // (6000.0.0·6000.0.85·6000.1·6000.2·6000.3·6000.4 실측에서는 새 세션에서도 이전 세션의 임포트 기록이 있었다).
        // 기록이 이미 있을 때 FullScanImportedAssets 를 부르면 Unity 내부(AssetStoreCache.UpdateImportedAssets)에서
        // 컬렉션 수정 예외가 난다(6000.6 실측). 같은 세션의 기록은 PM이 에셋 변경 때마다 갱신하므로, 이 객체가 한 번 채운 뒤에는
        // 다시 스캔하지 않는다(임포트한 에셋이 없는 프로젝트에서 판정할 때마다 전체 스캔하지 않도록).
        public bool TryFillImportedPackages()
        {
            if (!IsAvailable(AssetStoreFeature.ImportedInfoScan))
            {
                return false;
            }
            if (_isImportedPackagesFilled)
            {
                return true;
            }
            var importedAssets = (IEnumerable)_importedAssetsProperty.GetValue(GetService(_cacheType));
            if (!importedAssets.GetEnumerator().MoveNext())
            {
                _fullScanImportedAssetsMethod.Invoke(GetService(_assetStoreClientType), null);
            }
            _isImportedPackagesFilled = true;
            return true;
        }

        // Package Manager와 같은 방식(AssetOrigin 기록)으로 비대화식 임포트한다.
        public void Install(long productId)
        {
            _installMethod.Invoke(GetService(_installerType), new object[] { productId, false });
        }

        public string GetCacheRootPath()
        {
            object config = _getCachePathConfigMethod.Invoke(GetService(_cachePathProxyType), null);
            return (string)_cachePathProperty.GetValue(config);
        }

        // Package Manager 창의 My Assets 페이지에서 제품을 선택해 상세를 보여준다.
        // 공개 Window.Open(productId)는 페이지를 지정할 수 없어, My Assets 를 불러오지 않은 창에서는 제품을 찾지 못한다(6000.6 실측).
        // My Assets 페이지를 지정하면 페이지가 제품 정보를 불러온 뒤 선택한다. 구매하지 않은 제품이면 선택이 바뀌지 않는다.
        public void SelectInMyAssets(long productId)
        {
            UnityEditor.PackageManager.UI.Window.Open(null);
            _selectPackageMethod.Invoke(GetService(_selectionHandlerType), new object[] { productId.ToString(), MyAssetsPageId });
        }

        private void BindServicesContainer()
        {
            // ScriptableSingleton<ServicesContainer> 이어야 instance 로 얻을 수 있다. 구조가 바뀌면 모든 기능을 누락으로 보고한다.
            Type containerType = FindInternalType("ServicesContainer");
            bool isSingleton = containerType != null
                               && typeof(ScriptableObject).IsAssignableFrom(containerType)
                               && typeof(ScriptableSingleton<>).MakeGenericType(containerType).IsAssignableFrom(containerType);
            _resolveMethod = isSingleton
                ? containerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(x => x.Name == "Resolve" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0)
                : null;
            if (_resolveMethod == null)
            {
                foreach (AssetStoreFeature feature in Enum.GetValues(typeof(AssetStoreFeature)))
                {
                    GetMissingList(feature).Add("ScriptableSingleton<ServicesContainer>.Resolve<T>()");
                }
                return;
            }

            _servicesContainer = typeof(ScriptableSingleton<>).MakeGenericType(containerType)
                .GetProperty("instance", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null);
        }

        private void BindPurchases()
        {
            const AssetStoreFeature feature = AssetStoreFeature.Purchases;
            _restApiType = Require(feature, FindInternalType("IAssetStoreRestAPI"), "IAssetStoreRestAPI");
            _getPurchasesMethod = Require(feature, FindMethodByParameterCount(_restApiType, "GetPurchases", 3), "IAssetStoreRestAPI.GetPurchases");

            ParameterInfo[] parameters = _getPurchasesMethod?.GetParameters();
            Type queryArgsType = parameters?[0].ParameterType;
            Type purchasesType = GetCallbackArgumentType(parameters?[1].ParameterType, 0);
            Type errorType = GetCallbackArgumentType(parameters?[2].ParameterType, 0);

            _queryArgsConstructor = Require(feature,
                queryArgsType?.GetConstructors(MemberFlags).FirstOrDefault(x => x.GetParameters().All(p => p.HasDefaultValue)),
                "PurchasesQueryArgs..ctor");
            _queryStartIndexField = Require(feature, queryArgsType?.GetField("startIndex", MemberFlags), "PurchasesQueryArgs.startIndex");
            _queryLimitField = Require(feature, queryArgsType?.GetField("limit", MemberFlags), "PurchasesQueryArgs.limit");
            _purchasesTotalField = Require(feature, purchasesType?.GetField("total", MemberFlags), "AssetStorePurchases.total");
            _purchasesListField = Require(feature, purchasesType?.GetField("list", MemberFlags), "AssetStorePurchases.list");

            Type purchaseInfoType = GetCollectionElementType(_purchasesListField?.FieldType);
            _purchaseProductIdField = Require(feature, purchaseInfoType?.GetField("productId", MemberFlags), "AssetStorePurchaseInfo.productId");
            _purchaseDisplayNameField = Require(feature, purchaseInfoType?.GetField("displayName", MemberFlags), "AssetStorePurchaseInfo.displayName");
            _purchasesErrorMessageProperty = Require(feature, errorType?.GetProperty("message", MemberFlags), "UIError.message");
        }

        // GetProductDetail 은 6000.6 에서만 실측했다. 없으면 설명만 빠지고 구매 목록 수집은 계속한다.
        private void BindProductDetail()
        {
            const AssetStoreFeature feature = AssetStoreFeature.ProductDetail;
            _getProductDetailMethod = Require(feature, FindMethodByParameterCount(_restApiType, "GetProductDetail", 3), "IAssetStoreRestAPI.GetProductDetail");

            ParameterInfo[] parameters = _getProductDetailMethod?.GetParameters();
            Type productInfoType = GetCallbackArgumentType(parameters?[1].ParameterType, 0);
            Type errorType = GetCallbackArgumentType(parameters?[2].ParameterType, 0);
            _productDescriptionField = Require(feature, productInfoType?.GetField("description", MemberFlags), "AssetStoreProductInfo.description");
            _productDetailErrorMessageProperty = Require(feature, errorType?.GetProperty("message", MemberFlags), "UIError.message");
        }

        // 업데이트 확인(6000.6 실측). 로컬·임포트 기록의 버전과 상품 상세의 최신 버전 문자열도 함께 쓰므로 그 멤버도 이 기능에 묶는다.
        private void BindUpdateInfo()
        {
            const AssetStoreFeature feature = AssetStoreFeature.UpdateInfo;
            _getUpdateDetailMethod = Require(feature, FindMethodByParameterCount(_restApiType, "GetUpdateDetail", 3), "IAssetStoreRestAPI.GetUpdateDetail");

            ParameterInfo[] parameters = _getUpdateDetailMethod?.GetParameters();
            _updateArgsConstructor = Require(feature,
                parameters?[0].ParameterType.GetConstructors(MemberFlags).FirstOrDefault(x => x.GetParameters().Length == 1
                                                                                         && x.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(List<long>))),
                "CheckUpdateInfoArgs..ctor(IReadOnlyCollection<long>)");
            Type updateInfoType = GetCollectionElementType(GetCallbackArgumentType(parameters?[1].ParameterType, 0));
            Type errorType = GetCallbackArgumentType(parameters?[2].ParameterType, 0);
            _updateProductIdField = Require(feature, updateInfoType?.GetField("productId", MemberFlags), "AssetStoreUpdateInfo.productId");
            _updateRecommendedUploadIdField = Require(feature, updateInfoType?.GetField("recommendedUploadId", MemberFlags), "AssetStoreUpdateInfo.recommendedUploadId");
            _updateErrorMessageProperty = Require(feature, errorType?.GetProperty("message", MemberFlags), "UIError.message");

            Type localInfoType = _getLocalInfoMethod?.ReturnType;
            _localInfoUploadIdField = Require(feature, localInfoType?.GetField("uploadId", MemberFlags), "AssetStoreLocalInfo.uploadId");
            _localInfoVersionStringField = Require(feature, localInfoType?.GetField("versionString", MemberFlags), "AssetStoreLocalInfo.versionString");
            Type importedPackageType = _getImportedPackageMethod?.ReturnType;
            _importedUploadIdProperty = Require(feature, importedPackageType?.GetProperty("uploadId", MemberFlags), "AssetStoreImportedPackage.uploadId");
            _importedVersionStringProperty = Require(feature, importedPackageType?.GetProperty("versionString", MemberFlags), "AssetStoreImportedPackage.versionString");
            Type productInfoType = GetCallbackArgumentType(_getProductDetailMethod?.GetParameters()[1].ParameterType, 0);
            _productVersionStringField = Require(feature, productInfoType?.GetField("versionString", MemberFlags), "AssetStoreProductInfo.versionString");
        }

        private void BindDownload()
        {
            const AssetStoreFeature feature = AssetStoreFeature.Download;
            _downloadManagerType = Require(feature, FindInternalType("IAssetStoreDownloadManager"), "IAssetStoreDownloadManager");
            _downloadMethod = Require(feature, FindMethod(_downloadManagerType, "Download", typeof(IEnumerable<long>)),
                "IAssetStoreDownloadManager.Download(IEnumerable<long>)");
            _abortDownloadMethod = Require(feature, FindMethod(_downloadManagerType, "AbortDownload", typeof(long?)),
                "IAssetStoreDownloadManager.AbortDownload(long?)");
            _downloadFinalizedEvent = Require(feature, _downloadManagerType?.GetEvent("onDownloadFinalized"), "IAssetStoreDownloadManager.onDownloadFinalized");
            _downloadErrorEvent = Require(feature, _downloadManagerType?.GetEvent("onDownloadError"), "IAssetStoreDownloadManager.onDownloadError");
            _downloadProgressEvent = Require(feature, _downloadManagerType?.GetEvent("onDownloadProgress"), "IAssetStoreDownloadManager.onDownloadProgress");

            Type operationType = GetCallbackArgumentType(_downloadFinalizedEvent?.EventHandlerType, 0);
            Type errorType = GetCallbackArgumentType(_downloadErrorEvent?.EventHandlerType, 1);
            _operationProductIdProperty = Require(feature, operationType?.GetProperty("productId", MemberFlags), "AssetStoreDownloadOperation.productId");
            _operationStateProperty = Require(feature, operationType?.GetProperty("state", MemberFlags), "AssetStoreDownloadOperation.state");
            _operationErrorMessageProperty = Require(feature, operationType?.GetProperty("errorMessage", MemberFlags), "AssetStoreDownloadOperation.errorMessage");
            _downloadErrorMessageProperty = Require(feature, errorType?.GetProperty("message", MemberFlags), "UIError.message");
        }

        private void BindLocalInfo()
        {
            const AssetStoreFeature feature = AssetStoreFeature.LocalInfo;
            _cacheType = Require(feature, FindInternalType("IAssetStoreCache"), "IAssetStoreCache");
            _getLocalInfoMethod = Require(feature, FindMethod(_cacheType, "GetLocalInfo", typeof(long?)), "IAssetStoreCache.GetLocalInfo(long?)");
            _localInfoPackagePathField = Require(feature, _getLocalInfoMethod?.ReturnType.GetField("packagePath", MemberFlags), "AssetStoreLocalInfo.packagePath");
            _localInfoHandlerType = Require(feature, FindInternalType("ILocalInfoHandler"), "ILocalInfoHandler");
            _getParsedLocalInfosMethod = Require(feature, FindMethod(_localInfoHandlerType, "GetParsedLocalInfos"), "ILocalInfoHandler.GetParsedLocalInfos()");
            _setLocalInfosMethod = Require(feature, FindMethodByParameterCount(_cacheType, "SetLocalInfos", 1), "IAssetStoreCache.SetLocalInfos");
        }

        private void BindImportedInfo()
        {
            const AssetStoreFeature feature = AssetStoreFeature.ImportedInfo;
            Type cacheType = Require(feature, FindInternalType("IAssetStoreCache"), "IAssetStoreCache");
            _getImportedPackageMethod = Require(feature, FindMethod(cacheType, "GetImportedPackage", typeof(long?)), "IAssetStoreCache.GetImportedPackage(long?)");

            // 6000.5 부터 인터페이스에 공개된 전체 스캔. 6000.0~6000.4 에는 없다.
            const AssetStoreFeature scanFeature = AssetStoreFeature.ImportedInfoScan;
            _assetStoreClientType = Require(scanFeature, FindInternalType("IAssetStoreClient"), "IAssetStoreClient");
            _fullScanImportedAssetsMethod = Require(scanFeature, FindMethod(_assetStoreClientType, "FullScanImportedAssets"), "IAssetStoreClient.FullScanImportedAssets()");
            _importedAssetsProperty = Require(scanFeature, cacheType?.GetProperty("importedAssets", MemberFlags), "IAssetStoreCache.importedAssets");
        }

        private void BindInstall()
        {
            const AssetStoreFeature feature = AssetStoreFeature.Install;
            _installerType = Require(feature, FindInternalType("IAssetStorePackageInstaller"), "IAssetStorePackageInstaller");
            _installMethod = Require(feature, FindMethod(_installerType, "Install", typeof(long), typeof(bool)), "IAssetStorePackageInstaller.Install(long, bool)");
        }

        private void BindCachePath()
        {
            const AssetStoreFeature feature = AssetStoreFeature.CachePath;
            _cachePathProxyType = Require(feature, FindInternalType("IAssetStoreCachePathProxy"), "IAssetStoreCachePathProxy");
            _getCachePathConfigMethod = Require(feature, FindMethod(_cachePathProxyType, "GetConfig"), "IAssetStoreCachePathProxy.GetConfig()");
            _cachePathProperty = Require(feature, _getCachePathConfigMethod?.ReturnType.GetProperty("path", MemberFlags), "CachePathConfig.path");
        }

        private void BindSelectInMyAssets()
        {
            const AssetStoreFeature feature = AssetStoreFeature.SelectInMyAssets;
            _selectionHandlerType = Require(feature, FindInternalType("IDelayedSelectionHandler"), "IDelayedSelectionHandler");
            _selectPackageMethod = Require(feature, FindMethod(_selectionHandlerType, "SelectPackage", typeof(string), typeof(string)),
                "IDelayedSelectionHandler.SelectPackage(string, string)");
        }

        private object GetService(Type interfaceType)
        {
            if (!_services.TryGetValue(interfaceType, out object service))
            {
                service = _resolveMethod.MakeGenericMethod(interfaceType).Invoke(_servicesContainer, null);
                _services[interfaceType] = service;
            }
            return service;
        }

        private void AddDownloadEventHandler(object target, EventInfo eventInfo, InternalCallback callback)
        {
            Delegate handler = callback.CreateDelegate(eventInfo.EventHandlerType);
            eventInfo.AddEventHandler(target, handler);
            _downloadEventHandlers.Add((target, eventInfo, handler));
        }

        private List<(long productId, string displayName)> ReadPurchases(object purchases)
        {
            var result = new List<(long productId, string displayName)>();
            foreach (object purchaseInfo in (IEnumerable)_purchasesListField.GetValue(purchases))
            {
                result.Add((Convert.ToInt64(_purchaseProductIdField.GetValue(purchaseInfo)),
                    (string)_purchaseDisplayNameField.GetValue(purchaseInfo)));
            }
            return result;
        }

        private long ReadOperationProductId(object operation)
        {
            return Convert.ToInt64(_operationProductIdProperty.GetValue(operation));
        }

        private IReadOnlyList<string> GetMissingMembers(AssetStoreFeature feature)
        {
            return _missingMembers.TryGetValue(feature, out var members) ? members : Array.Empty<string>();
        }

        private T Require<T>(AssetStoreFeature feature, T member, string memberName) where T : class
        {
            if (member == null)
            {
                GetMissingList(feature).Add(memberName);
            }
            return member;
        }

        private List<string> GetMissingList(AssetStoreFeature feature)
        {
            if (!_missingMembers.TryGetValue(feature, out var members))
            {
                members = new List<string>();
                _missingMembers[feature] = members;
            }
            return members;
        }

        private Type FindInternalType(string typeName)
        {
            return _editorAssembly.GetType(InternalNamespace + typeName);
        }

        // 오버로드가 추가돼도 모호성 예외가 나지 않도록 이름과 매개변수 타입을 모두 비교한다.
        private MethodInfo FindMethod(Type type, string name, params Type[] parameterTypes)
        {
            return type?.GetMethods(MemberFlags).FirstOrDefault(x => x.Name == name
                                                                     && !x.IsGenericMethodDefinition
                                                                     && x.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes));
        }

        // 매개변수 타입이 내부 타입이라 이름으로 비교할 수 없을 때 사용한다.
        private MethodInfo FindMethodByParameterCount(Type type, string name, int parameterCount)
        {
            return type?.GetMethods(MemberFlags).FirstOrDefault(x => x.Name == name
                                                                     && !x.IsGenericMethodDefinition
                                                                     && x.GetParameters().Length == parameterCount);
        }

        // Action<T1, ...> 의 index 번째 타입 인자
        private Type GetCallbackArgumentType(Type delegateType, int index)
        {
            Type[] arguments = delegateType?.GetGenericArguments();
            return arguments != null && arguments.Length > index ? arguments[index] : null;
        }

        private Type GetCollectionElementType(Type collectionType)
        {
            if (collectionType == null)
            {
                return null;
            }
            return collectionType.IsArray ? collectionType.GetElementType() : collectionType.GetGenericArguments().FirstOrDefault();
        }
    }
}
