using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Sync;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Platform.Abstractions.Security;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SyncServiceEndToEndTests
{
    [Fact]
    public async Task MissingPollingSettingUsesDefaultWithoutPersistingValue()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        MemorySyncRemote remote = new();
        DictionarySecretStore secrets = new();
        await using SyncService service = CreateService(context, secrets, remote);
        try
        {
            await service.InitializePollingSettingsAsync(CancellationToken.None);

            Assert.Equal(SyncPollingSettings.Default, service.PollingSettings);
            Assert.Null(await context.Store.GetSettingAsync(
                SyncSettingKeys.PollInterval,
                CancellationToken.None));
        }
        finally
        {
            secrets.Clear();
            remote.Clear();
        }
    }

    [Fact]
    public async Task ConcurrentManualRunsAreSingleFlightAndPauseDrainsActiveRequest()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        MemorySyncRemote remote = new();
        DictionarySecretStore secrets = new();
        await using SyncService service = CreateService(context, secrets, remote);
        byte[] password = Encoding.UTF8.GetBytes("webdav-test-password");
        byte[] recoveryCode = Encoding.UTF8.GetBytes("correct horse battery staple");
        try
        {
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(new SyncRemoteConfiguration(
                    new Uri("https://dav.example.test/"),
                    "SnapBoard/v1",
                    "sync-user")),
                password,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);

            MemorySyncRemote.EnsureBlock firstBlock = remote.BlockNextEnsure();
            Task<SyncStatusSnapshot> firstRun = service
                .SynchronizeNowAsync(CancellationToken.None)
                .AsTask();
            await firstBlock.Entered;
            Task<SyncStatusSnapshot> secondRun = service
                .SynchronizeNowAsync(CancellationToken.None)
                .AsTask();
            await Task.Delay(50);
            Assert.False(secondRun.IsCompleted);
            Assert.Equal(1, remote.MaximumConcurrentEnsures);
            firstBlock.Release();
            Assert.Equal(SyncServiceState.Idle, (await firstRun).State);
            Assert.Equal(SyncServiceState.Idle, (await secondRun).State);
            Assert.Equal(1, remote.MaximumConcurrentEnsures);

            MemorySyncRemote.EnsureBlock pauseBlock = remote.BlockNextEnsure();
            Task<SyncStatusSnapshot> activeRun = service
                .SynchronizeNowAsync(CancellationToken.None)
                .AsTask();
            await pauseBlock.Entered;
            await service.PauseAndDrainAsync(CancellationToken.None);
            Assert.Equal(SyncServiceState.Paused, (await activeRun).State);
            Assert.Equal(SyncServiceState.Paused, service.Status.State);
            service.ResumeAfterPause();
            Assert.Equal(SyncServiceState.Idle, service.Status.State);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(recoveryCode);
            secrets.Clear();
            remote.Clear();
        }
    }

    [Fact]
    public async Task TwoDevicesJoinUploadBlobDownloadAndApplyTombstone()
    {
        await using HistoryStoreTestContext firstContext =
            await HistoryStoreTestContext.CreateAsync();
        await using HistoryStoreTestContext secondContext =
            await HistoryStoreTestContext.CreateAsync();
        MemorySyncRemote remote = new();
        DictionarySecretStore firstSecrets = new();
        DictionarySecretStore secondSecrets = new();
        await using SyncService first = CreateService(firstContext, firstSecrets, remote);
        await using SyncService second = CreateService(secondContext, secondSecrets, remote);
        byte[] password = Encoding.UTF8.GetBytes("webdav-test-password");
        byte[] recoveryCode = Encoding.UTF8.GetBytes("correct horse battery staple");
        byte[]? recoveryEnvelope = null;
        byte[] html = Enumerable.Range(0, 70 * 1024)
            .Select(index => (byte)('a' + (index % 26)))
            .ToArray();
        try
        {
            SyncSetupRequest request = new(new SyncRemoteConfiguration(
                new Uri("https://dav.example.test/base/"),
                "SnapBoard/v1",
                "sync-user"));
            SyncSetupResult created = await first.CreateSpaceAsync(
                request,
                password,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            Assert.NotNull(created.SpaceId);
            Assert.NotNull(created.RecoveryMaterialPath);
            Assert.True(File.Exists(created.RecoveryMaterialPath));
            await AssertRemoteConfigurationIsNotPersistedAsync(firstContext);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath);

            SyncSetupResult joined = await second.JoinSpaceAsync(
                created.SpaceId.Value,
                keyVersion: 1,
                request,
                password,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, joined.Status);
            Assert.NotEqual(created.DeviceId, joined.DeviceId);

            ClipboardCapturedItem item = CreateHtmlItem(html);
            await firstContext.Store.SaveAsync(item, CancellationToken.None);
            SyncStatusSnapshot firstUpload = await first.SynchronizeNowAsync(
                CancellationToken.None);
            Assert.Equal(SyncServiceState.Idle, firstUpload.State);
            Assert.Equal(2, firstUpload.UploadedEvents);
            Assert.Single(remote.Blobs);
            Assert.Collection(remote.Events, _ => { }, _ => { });

            SyncStatusSnapshot secondDownload = await second.SynchronizeNowAsync(
                CancellationToken.None);
            Assert.Equal(SyncServiceState.Idle, secondDownload.State);
            Assert.Equal(2, secondDownload.DownloadedEvents);
            ClipboardHistoryItemSummary downloaded = Assert.Single((await secondContext.Store.SearchAsync(
                new ClipboardHistoryQuery { PageSize = 10 },
                CancellationToken.None)).Items);
            Assert.Equal(item.Id, downloaded.Id);
            ClipboardHistoryContent downloadedContent = Assert.IsType<ClipboardHistoryContent>(
                await secondContext.Store.GetContentAsync(item.Id, CancellationToken.None));
            Assert.Equal(html, downloadedContent.Html.ToArray());

            Assert.True(await firstContext.Store.SoftDeleteAsync(
                item.Id,
                CancellationToken.None));
            Assert.Equal(
                SyncServiceState.Idle,
                (await first.SynchronizeNowAsync(CancellationToken.None)).State);
            SyncStatusSnapshot tombstoneDownload = await second.SynchronizeNowAsync(
                CancellationToken.None);
            Assert.Equal(1, tombstoneDownload.DownloadedEvents);
            Assert.Empty((await secondContext.Store.SearchAsync(
                new ClipboardHistoryQuery { PageSize = 10 },
                CancellationToken.None)).Items);

            SyncConfigurationSnapshot secondConfiguration =
                Assert.IsType<SyncConfigurationSnapshot>(
                    await secondContext.Store.GetConfigurationAsync(CancellationToken.None));
            SyncConfigurationSnapshot firstConfiguration =
                Assert.IsType<SyncConfigurationSnapshot>(
                    await firstContext.Store.GetConfigurationAsync(CancellationToken.None));
            SyncCheckpointState checkpoint = await secondContext.Store.GetCheckpointAsync(
                created.SpaceId.Value,
                created.DeviceId!.Value,
                CancellationToken.None);
            Assert.Equal(3, checkpoint.AppliedSequence);
            Assert.Equal(4, firstConfiguration.NextSequence);
            Assert.Equal(1, secondConfiguration.NextSequence);
        }
        finally
        {
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }

            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(recoveryCode);
            CryptographicOperations.ZeroMemory(html);
            firstSecrets.Clear();
            secondSecrets.Clear();
            remote.Clear();
        }
    }

    [Fact]
    public async Task HistorySettingsConvergeAfterSecondDeviceJoinsSpace()
    {
        await using HistoryStoreTestContext firstContext =
            await HistoryStoreTestContext.CreateAsync();
        await using HistoryStoreTestContext secondContext =
            await HistoryStoreTestContext.CreateAsync();
        MemorySyncRemote remote = new();
        DictionarySecretStore firstSecrets = new();
        DictionarySecretStore secondSecrets = new();
        ClipboardHistoryChangeNotifier firstNotifier = new();
        ClipboardHistoryChangeNotifier secondNotifier = new();
        ClipboardHistoryService firstHistory = new(firstContext.Store, firstNotifier);
        ClipboardHistoryService secondHistory = new(secondContext.Store, secondNotifier);
        ClipboardCaptureOptions firstCaptureOptions = new();
        ClipboardCaptureOptions secondCaptureOptions = new();
        await using HistorySettingsService firstSettings = new(
            firstHistory,
            firstContext.Store,
            firstCaptureOptions,
            firstNotifier);
        await using HistorySettingsService secondSettings = new(
            secondHistory,
            secondContext.Store,
            secondCaptureOptions,
            secondNotifier);
        await using SyncService first = CreateService(
            firstContext,
            firstSecrets,
            remote,
            firstHistory,
            firstSettings);
        await using SyncService second = CreateService(
            secondContext,
            secondSecrets,
            remote,
            secondHistory,
            secondSettings);
        byte[] password = Encoding.UTF8.GetBytes("webdav-test-password");
        byte[] recoveryCode = Encoding.UTF8.GetBytes("correct horse battery staple");
        byte[]? recoveryEnvelope = null;
        try
        {
            HistoryCaptureSettings expectedCapture = new(
                Text: true,
                RichText: false,
                Images: true,
                Files: false);
            HistoryRetentionSettings expectedRetention = new(
                Enabled: true,
                RetentionDays: 90);
            SyncPollingSettings expectedPolling = new(15 * 60);
            await firstSettings.UpdateAsync(
                expectedCapture,
                expectedRetention,
                CancellationToken.None);
            await first.UpdatePollingSettingsAsync(expectedPolling, CancellationToken.None);

            SyncSetupRequest request = new(new SyncRemoteConfiguration(
                new Uri("https://dav.example.test/base/"),
                "SnapBoard/v1",
                "sync-user"));
            SyncSetupResult created = await first.CreateSpaceAsync(
                request,
                password,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath!);
            SyncSetupResult joined = await second.JoinSpaceAsync(
                created.SpaceId!.Value,
                keyVersion: 1,
                request,
                password,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, joined.Status);

            SyncStatusSnapshot uploaded = await first.SynchronizeNowAsync(CancellationToken.None);
            Assert.Equal(3, uploaded.UploadedEvents);
            SyncStatusSnapshot downloaded = await second.SynchronizeNowAsync(
                CancellationToken.None);
            Assert.True(
                downloaded.State == SyncServiceState.Idle,
                $"Second device sync failed: {downloaded.DiagnosticCode}");
            Assert.Equal(3, downloaded.DownloadedEvents);
            Assert.Equal(expectedCapture, secondSettings.Current.Capture);
            Assert.Equal(expectedRetention, secondSettings.Current.Retention);
            Assert.Equal(expectedPolling, second.PollingSettings);
            Assert.Equal(
                [ClipboardContentKind.Text, ClipboardContentKind.Image],
                secondCaptureOptions.EnabledContentKinds.Order());
        }
        finally
        {
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }

            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(recoveryCode);
            firstSecrets.Clear();
            secondSecrets.Clear();
            remote.Clear();
        }
    }

    private static SyncService CreateService(
        HistoryStoreTestContext context,
        IPlatformSecretStore secrets,
        ISyncRemoteSessionFactory remote,
        IClipboardHistoryService? historyService = null,
        IHistorySettingsService? historySettingsService = null) => new(
        context.Store,
        new PlatformSyncKeyService(
            secrets,
            new SyncRecoveryKdfParameters(
                MemoryKiB: 8 * 1024,
                Iterations: 2,
                Parallelism: 1)),
        new PlatformSyncCredentialService(secrets),
        new FileSyncRecoveryMaterialStore(context.Paths),
        new SyncObjectProtector(),
        remote,
        historyService ?? new ClipboardHistoryService(
            context.Store,
            new ClipboardHistoryChangeNotifier()),
        options: null,
        historySettingsService: historySettingsService);

    private static async Task AssertRemoteConfigurationIsNotPersistedAsync(
        HistoryStoreTestContext context)
    {
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM pragma_table_info('sync_spaces')
            WHERE name IN (
                'remote_endpoint', 'remote_root', 'remote_username',
                'certificate_sha256_pin', 'allow_insecure_loopback');
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private static ClipboardCapturedItem CreateHtmlItem(byte[] html)
    {
        ClipboardItemId id = ClipboardItemId.New();
        const string text = "shared html item";
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = DateTimeOffset.UtcNow,
            SourceProcessName = "source-device-only",
            SourceExecutablePath = @"C:\private\must-not-sync.exe",
            ContentHash = new ClipboardContentHash(Hash(textBytes)),
            PrimaryKind = ClipboardContentKind.Html,
            DisplayCategory = ClipboardHistoryDisplayCategory.Text,
            PreviewText = text,
            SearchableText = text,
            Representations =
            [
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Text,
                    "text/plain; charset=utf-8",
                    text,
                    default),
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Html,
                    "text/html",
                    null,
                    html),
            ],
            Formats = [new ClipboardCapturedFormat("html", "HTML", true)],
            TotalSizeBytes = textBytes.LongLength + html.LongLength,
        };
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private sealed class DictionarySecretStore : IPlatformSecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public ValueTask<PlatformSecretReadResult> ReadAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_secrets.TryGetValue(name, out byte[]? secret)
                ? new PlatformSecretReadResult(
                    PlatformSecretStoreStatus.Success,
                    secret.ToArray())
                : new PlatformSecretReadResult(PlatformSecretStoreStatus.NotFound));
        }

        public ValueTask<PlatformSecretWriteResult> WriteAsync(
            string name,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_secrets.Remove(name, out byte[]? previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            _secrets[name] = secret.ToArray();
            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.Success));
        }

        public ValueTask<PlatformSecretWriteResult> DeleteAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_secrets.Remove(name, out byte[]? secret))
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.Success));
        }

        public void Clear()
        {
            foreach (byte[] secret in _secrets.Values)
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            _secrets.Clear();
        }
    }

    private sealed class MemorySyncRemote : ISyncRemoteSessionFactory
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, byte[]> _metadata = [];
        private readonly HashSet<(Guid SpaceId, Guid DeviceId)> _devices = [];
        private readonly Dictionary<(Guid SpaceId, Guid DeviceId, long Sequence, Guid EventId), byte[]>
            _events = [];
        private readonly Dictionary<(Guid SpaceId, string BlobId), byte[]> _blobs = [];
        private EnsureBlock? _nextEnsureBlock;
        private int _activeEnsures;
        private int _maximumConcurrentEnsures;

        public IReadOnlyCollection<byte[]> Events => _events.Values;

        public IReadOnlyCollection<byte[]> Blobs => _blobs.Values;

        public int MaximumConcurrentEnsures => Volatile.Read(ref _maximumConcurrentEnsures);

        public ISyncRemoteSession Create(
            SyncRemoteConfiguration configuration,
            ReadOnlyMemory<byte> password) => new Session(this);

        public EnsureBlock BlockNextEnsure()
        {
            lock (_gate)
            {
                if (_nextEnsureBlock is not null)
                {
                    throw new InvalidOperationException("An ensure block is already pending.");
                }

                _nextEnsureBlock = new EnsureBlock();
                return _nextEnsureBlock;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                foreach (byte[] value in _metadata.Values.Concat(_events.Values).Concat(_blobs.Values))
                {
                    CryptographicOperations.ZeroMemory(value);
                }

                _metadata.Clear();
                _devices.Clear();
                _events.Clear();
                _blobs.Clear();
                _nextEnsureBlock?.Release();
                _nextEnsureBlock = null;
            }
        }

        public sealed class EnsureBlock
        {
            private readonly TaskCompletionSource _entered = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => _entered.Task;

            public void Release() => _release.TrySetResult();

            internal void MarkEntered() => _entered.TrySetResult();

            internal Task WaitAsync(CancellationToken cancellationToken) =>
                _release.Task.WaitAsync(cancellationToken);
        }

        private sealed class Session(MemorySyncRemote owner) : ISyncRemoteSession
        {
            public async ValueTask<SyncRemoteResult> EnsureHierarchyAsync(
                Guid spaceId,
                Guid localDeviceId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureBlock? block;
                lock (owner._gate)
                {
                    block = owner._nextEnsureBlock;
                    owner._nextEnsureBlock = null;
                    owner._devices.Add((spaceId, localDeviceId));
                }

                int active = Interlocked.Increment(ref owner._activeEnsures);
                UpdateMaximum(ref owner._maximumConcurrentEnsures, active);
                try
                {
                    if (block is not null)
                    {
                        block.MarkEntered();
                        await block.WaitAsync(cancellationToken);
                    }

                    return Success();
                }
                finally
                {
                    Interlocked.Decrement(ref owner._activeEnsures);
                }
            }

            public ValueTask<SyncRemoteContentResult> GetMetadataAsync(
                Guid spaceId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    return ValueTask.FromResult(owner._metadata.TryGetValue(spaceId, out byte[]? value)
                        ? Content(value)
                        : NotFound());
                }
            }

            public ValueTask<SyncRemoteResult> PutMetadataAsync(
                Guid spaceId,
                ReadOnlyMemory<byte> encryptedMetadata,
                CancellationToken cancellationToken) => Put(
                owner._metadata,
                spaceId,
                encryptedMetadata,
                cancellationToken);

            public ValueTask<SyncRemoteDeviceListResult> ListDevicesAsync(
                Guid spaceId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    Guid[] devices = owner._devices
                        .Where(value => value.SpaceId == spaceId)
                        .Select(value => value.DeviceId)
                        .OrderBy(value => value.ToString("N"), StringComparer.Ordinal)
                        .ToArray();
                    return ValueTask.FromResult(new SyncRemoteDeviceListResult(Success(), devices));
                }
            }

            public ValueTask<SyncRemoteEventListResult> ListEventsAsync(
                Guid spaceId,
                Guid deviceId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    SyncRemoteEventReference[] events = owner._events.Keys
                        .Where(value => value.SpaceId == spaceId && value.DeviceId == deviceId)
                        .OrderBy(value => value.Sequence)
                        .Select(value => new SyncRemoteEventReference(
                            value.DeviceId,
                            value.Sequence,
                            value.EventId,
                            GetEtag(value.EventId)))
                        .ToArray();
                    return ValueTask.FromResult(new SyncRemoteEventListResult(Success(), events));
                }
            }

            public ValueTask<SyncRemoteContentResult> GetEventAsync(
                Guid spaceId,
                SyncRemoteEventReference remoteEvent,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    return ValueTask.FromResult(owner._events.TryGetValue(
                        (spaceId, remoteEvent.DeviceId, remoteEvent.Sequence, remoteEvent.EventId),
                        out byte[]? value)
                            ? Content(value, GetEtag(remoteEvent.EventId))
                            : NotFound());
                }
            }

            public ValueTask<SyncRemoteResult> PutEventAsync(
                Guid spaceId,
                Guid deviceId,
                long sequence,
                Guid eventId,
                ReadOnlyMemory<byte> encryptedEvent,
                CancellationToken cancellationToken) => Put(
                owner._events,
                (spaceId, deviceId, sequence, eventId),
                encryptedEvent,
                cancellationToken,
                GetEtag(eventId));

            public ValueTask<SyncRemoteContentResult> GetBlobAsync(
                Guid spaceId,
                string keyedBlobId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    return ValueTask.FromResult(owner._blobs.TryGetValue(
                        (spaceId, keyedBlobId),
                        out byte[]? value)
                            ? Content(value)
                            : NotFound());
                }
            }

            public ValueTask<SyncRemoteResult> PutBlobAsync(
                Guid spaceId,
                string keyedBlobId,
                ReadOnlyMemory<byte> encryptedBlob,
                CancellationToken cancellationToken) => Put(
                owner._blobs,
                (spaceId, keyedBlobId),
                encryptedBlob,
                cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private ValueTask<SyncRemoteResult> Put<TKey>(
                Dictionary<TKey, byte[]> destination,
                TKey key,
                ReadOnlyMemory<byte> content,
                CancellationToken cancellationToken,
                string? etag = null)
                where TKey : notnull
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    if (destination.ContainsKey(key))
                    {
                        return ValueTask.FromResult(new SyncRemoteResult(
                            true,
                            SyncRemoteErrorCategory.None,
                            etag,
                            AlreadyExisted: true));
                    }

                    destination.Add(key, content.ToArray());
                    return ValueTask.FromResult(Success(etag));
                }
            }

            private static SyncRemoteContentResult Content(byte[] content, string? etag = null) =>
                new(
                    Success(etag),
                    new SyncRemoteContentLease(content.ToArray()));

            private static SyncRemoteContentResult NotFound() => new(
                new SyncRemoteResult(false, SyncRemoteErrorCategory.NotFound));

            private static SyncRemoteResult Success(string? etag = null) =>
                new(true, SyncRemoteErrorCategory.None, etag);

            private static string GetEtag(Guid eventId) => $"\"{eventId:N}\"";

            private static void UpdateMaximum(ref int maximum, int value)
            {
                int current = Volatile.Read(ref maximum);
                while (value > current)
                {
                    int observed = Interlocked.CompareExchange(ref maximum, value, current);
                    if (observed == current)
                    {
                        return;
                    }

                    current = observed;
                }
            }
        }
    }
}
