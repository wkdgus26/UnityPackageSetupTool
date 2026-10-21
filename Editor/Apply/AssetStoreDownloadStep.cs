using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PackageSetupTool
{
    // 로컬 .unitypackage 가 없거나 최신이 아닌 구매 에셋을 Package Manager 내부 다운로드 관리자로 받는다.
    // 완료는 이벤트와 로컬 파일 생성(최신이 아니던 파일은 로컬 정보의 업로드 ID 가 권장 업로드로 바뀐 것)으로 판단하고,
    // 진행이 멈추면 시간 상한으로 실패 처리한다.
    [Serializable]
    public class AssetStoreDownloadStep : ApplyStepBase
    {
        private const int NoProgressTimeoutSeconds = 120;
        private const double LocalCheckIntervalSeconds = 1.0;
        // 이벤트를 놓쳐도 새 파일을 찾을 수 있도록 로컬 정보를 주기적으로 다시 읽는다(캐시 폴더 전체 스캔이라 길게 둔다).
        private const double LocalRescanIntervalSeconds = 10.0;

        [SerializeField] private List<PackageEntry> _pending = new();
        // 로컬 파일이 최신이 아니라 다시 받을 상품과 받아야 할 업로드 ID(같은 순서). 받기 전 버전은 시작할 때 기록한다.
        [SerializeField] private List<long> _refreshProductIds = new();
        [SerializeField] private List<long> _refreshUploadIds = new();
        [SerializeField] private List<string> _refreshPreviousVersions = new();
        [SerializeField] private long _lastProgressTicks;

        // 도메인 리로드로 사라지는 값. 리로드 뒤에는 다운로드 요청을 다시 보내 이벤트를 새로 받는다.
        private Dictionary<long, LocalizedText> _failedMessages;
        private HashSet<long> _completedIds;
        private bool _isSubscribed;
        private double _nextLocalCheckTime;
        private double _nextLocalRescanTime;

        public AssetStoreDownloadStep(List<PackageEntry> targets, IReadOnlyDictionary<long, long> outdatedLocalUploadIds) : base(targets)
        {
            FillRefreshTargets(outdatedLocalUploadIds, _refreshProductIds, _refreshUploadIds);
        }

        public override string StepNameKey => "step.assetStoreDownload";

        private Dictionary<long, LocalizedText> FailedMessages => _failedMessages ??= new Dictionary<long, LocalizedText>();
        private HashSet<long> CompletedIds => _completedIds ??= new HashSet<long>();

        protected override void OnBegin(ApplyContext context)
        {
            foreach (var package in _targets)
            {
                long productId = long.Parse(package.id);
                bool hasLocalFile = context.LocalPackages.GetPath(productId) != null;
                if (_refreshProductIds.Contains(productId))
                {
                    _refreshPreviousVersions.Add(hasLocalFile ? context.LocalPackages.GetVersion(productId).versionString : string.Empty);
                    _pending.Add(package);
                }
                else if (hasLocalFile)
                {
                    context.Report(package, StepNameKey, ApplyOutcome.Skipped, new LocalizedText("download.alreadyDownloaded"));
                }
                else
                {
                    _pending.Add(package);
                }
            }

            if (_pending.Count == 0)
            {
                Finish(context);
                return;
            }
            if (!context.Bridge.IsAvailable(AssetStoreFeature.Download))
            {
                FailPending(context, context.Bridge.GetMissingMessage(AssetStoreFeature.Download));
                return;
            }
            if (string.IsNullOrEmpty(CloudProjectSettings.userId))
            {
                FailPending(context, new LocalizedText("download.loginRequired"));
                return;
            }

            RequestDownload(context);
        }

        // 다운로드는 취소할 수 있으므로 받는 중인 항목을 모두 취소한다. 취소로 오는 종료 이벤트는 받지 않도록 구독을 먼저 끊는다.
        protected override void OnAbort(ApplyContext context)
        {
            Unsubscribe(context);
            foreach (var package in _pending)
            {
                context.Bridge.AbortDownload(long.Parse(package.id));
            }
            ReportAll(_pending, context, ApplyOutcome.Skipped, new LocalizedText("download.cancelled"));
            _pending.Clear();
        }

        public override void OnResume(ApplyContext context)
        {
            // 리로드 동안 끝난 다운로드는 다시 요청하지 않는다.
            ReportDownloadedPackages(context);
            if (_pending.Count > 0)
            {
                RequestDownload(context);
            }
        }

        public override void Tick(ApplyContext context)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextLocalCheckTime)
            {
                return;
            }
            _nextLocalCheckTime = now + LocalCheckIntervalSeconds;
            if (now >= _nextLocalRescanTime)
            {
                _nextLocalRescanTime = now + LocalRescanIntervalSeconds;
                context.LocalPackages.Invalidate();
            }

            ReportDownloadedPackages(context);
            foreach (var package in _pending.ToList())
            {
                if (FailedMessages.TryGetValue(long.Parse(package.id), out LocalizedText message))
                {
                    _pending.Remove(package);
                    context.Report(package, StepNameKey, ApplyOutcome.Failed, message);
                }
            }

            bool isTimedOut = NowTicks - _lastProgressTicks > TimeSpan.FromSeconds(NoProgressTimeoutSeconds).Ticks;
            if (_pending.Count > 0 && isTimedOut)
            {
                foreach (var package in _pending)
                {
                    long productId = long.Parse(package.id);
                    // 다시 받기 항목은 예전 파일이 남아 있으므로, 받았는데 최신 업로드로 바뀌지 않은 경우를 구분한다.
                    string completedKey = _refreshProductIds.Contains(productId) ? "download.refreshNotUpdated" : "download.localFileMissing";
                    var message = CompletedIds.Contains(productId)
                        ? new LocalizedText(completedKey)
                        : new LocalizedText("download.noProgress", NoProgressTimeoutSeconds);
                    context.Report(package, StepNameKey, ApplyOutcome.Failed, message);
                }
                _pending.Clear();
            }

            if (_pending.Count > 0)
            {
                context.SetProgress(new LocalizedText("download.progress", _pending.Count));
                return;
            }

            Unsubscribe(context);
            Finish(context);
        }

        // 다운로드는 대기 항목 전체를 한 번에 요청하므로 결과가 나오지 않은 항목은 모두 처리 중이다.
        public override LocalizedText GetActivityText(PackageEntry package)
        {
            return _pending.Exists(x => x.HasKey(package.type, package.id)) ? new LocalizedText("applyItem.activity.downloading") : null;
        }

        private void ReportDownloadedPackages(ApplyContext context)
        {
            foreach (var package in _pending.ToList())
            {
                long productId = long.Parse(package.id);
                if (context.LocalPackages.GetPath(productId) == null)
                {
                    continue;
                }
                // 최신이 아니던 파일은 이미 있으므로 권장 업로드로 바뀌었을 때 받은 것으로 본다.
                int refreshIndex = _refreshProductIds.IndexOf(productId);
                LocalizedText message = null;
                if (refreshIndex >= 0)
                {
                    var (uploadId, versionString) = context.LocalPackages.GetVersion(productId);
                    if (uploadId != _refreshUploadIds[refreshIndex])
                    {
                        continue;
                    }
                    message = new LocalizedText("download.refreshed", _refreshPreviousVersions[refreshIndex], versionString);
                }
                _pending.Remove(package);
                _lastProgressTicks = NowTicks;
                context.Report(package, StepNameKey, ApplyOutcome.Success, message);
            }
        }

        private void FailPending(ApplyContext context, LocalizedText message)
        {
            ReportAll(_pending, context, ApplyOutcome.Failed, message);
            _pending.Clear();
            Finish(context);
        }

        private void RequestDownload(ApplyContext context)
        {
            Subscribe(context);
            _lastProgressTicks = NowTicks;
            context.SetProgress(new LocalizedText("download.progress", _pending.Count));
            context.Save();
            context.Bridge.RequestDownload(_pending.Select(x => long.Parse(x.id)).ToList());
        }

        private void Subscribe(ApplyContext context)
        {
            if (_isSubscribed)
            {
                return;
            }
            context.Bridge.SubscribeDownloadEvents(
                (productId, isCompleted, failureMessage) => OnDownloadFinalized(context, productId, isCompleted, failureMessage),
                OnDownloadError,
                OnDownloadProgress);
            _isSubscribed = true;
        }

        private void Unsubscribe(ApplyContext context)
        {
            if (!_isSubscribed)
            {
                return;
            }
            context.Bridge.UnsubscribeDownloadEvents();
            _isSubscribed = false;
        }

        // 다운로드 관리자 이벤트에는 Package Manager 창에서 따로 시작한 다운로드나 이미 끝난 항목도 섞여 온다.
        // 그런 이벤트가 남은 항목의 정체 판정 시각을 늘리지 않도록 아직 기다리는 항목의 이벤트만 처리한다.
        private bool IsPending(long productId)
        {
            return _pending.Any(x => long.Parse(x.id) == productId);
        }

        private void OnDownloadFinalized(ApplyContext context, long productId, bool isCompleted, LocalizedText failureMessage)
        {
            if (!IsPending(productId))
            {
                return;
            }
            _lastProgressTicks = NowTicks;
            if (isCompleted)
            {
                CompletedIds.Add(productId);
                // 새 파일을 다음 확인 때 바로 찾도록 로컬 정보를 다시 읽게 한다.
                context.LocalPackages.Invalidate();
            }
            else
            {
                FailedMessages[productId] = failureMessage;
            }
        }

        private void OnDownloadError(long productId, string message)
        {
            if (!IsPending(productId))
            {
                return;
            }
            _lastProgressTicks = NowTicks;
            FailedMessages[productId] = new RawText(message);
        }

        private void OnDownloadProgress(long productId)
        {
            if (!IsPending(productId))
            {
                return;
            }
            _lastProgressTicks = NowTicks;
        }
    }
}
