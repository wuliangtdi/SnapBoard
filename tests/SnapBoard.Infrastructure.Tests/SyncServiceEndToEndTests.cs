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
    public async Task CreatedSpaceKeepsKeysCredentialsAndRecoveryCodeOutOfOrdinaryFiles()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        MemorySyncRemote remote = new();
        DictionarySecretStore secrets = new();
        await using SyncService service = CreateService(context, secrets, remote);
        byte[] password = Encoding.UTF8.GetBytes("distinct-webdav-password-7f84d9");
        byte[] recoveryCode = Encoding.UTF8.GetBytes("distinct-recovery-code-118ca4");
        byte[]? masterKey = null;
        try
        {
            SyncRemoteConfiguration configuration = new(
                new Uri("https://private-dav.example.test/account-4b91/"),
                "PrivateRoot/62f0",
                "private-user-a731",
                new string('b', 64));
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(configuration),
                password,
                recoveryCode,
                CancellationToken.None);

            Assert.Equal(SyncSetupStatus.Success, created.Status);
            Assert.NotNull(created.SpaceId);
            Assert.True(File.Exists(created.RecoveryMaterialPath));
            Dictionary<string, byte[]> secretSnapshot = secrets.CopySecrets();
            KeyValuePair<string, byte[]> keySecret = Assert.Single(
                secretSnapshot,
                item => item.Key.StartsWith("sync/master/", StringComparison.Ordinal));
            Assert.Equal(32, keySecret.Value.Length);
            masterKey = keySecret.Value;
            Assert.Contains(
                secretSnapshot.Keys,
                name => name == $"sync/webdav/{created.SpaceId.Value:N}");

            await AssertRemoteConfigurationIsNotPersistedAsync(context);
            List<byte[]> forbidden =
            [
                Encoding.UTF8.GetBytes(configuration.Endpoint.AbsoluteUri),
                Encoding.UTF8.GetBytes(configuration.RemoteRoot),
                Encoding.UTF8.GetBytes(configuration.Username),
                Encoding.UTF8.GetBytes(configuration.CertificateSha256Pin!),
                password.ToArray(),
                recoveryCode.ToArray(),
                masterKey.ToArray(),
            ];
            try
            {
                foreach (string file in Directory.EnumerateFiles(
                    context.RootDirectory,
                    "*",
                    SearchOption.AllDirectories))
                {
                    await AssertFileDoesNotContainAnyAsync(file, forbidden);
                }
            }
            finally
            {
                foreach (byte[] value in forbidden)
                {
                    CryptographicOperations.ZeroMemory(value);
                }

                foreach (byte[] value in secretSnapshot.Values)
                {
                    if (!ReferenceEquals(value, masterKey))
                    {
                        CryptographicOperations.ZeroMemory(value);
                    }
                }
            }
        }
        finally
        {
            if (masterKey is not null)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }

            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(recoveryCode);
            secrets.Clear();
            remote.Clear();
        }
    }

    [Fact]
    public async Task FailedExistingSpaceReconfigurationPreservesWorkingSecretsAndConfiguration()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        MemorySyncRemote remote = new();
        DictionarySecretStore secrets = new();
        DictionarySecretStore alternateSecrets = new();
        await using SyncService service = CreateService(context, secrets, remote);
        byte[] originalPassword = Encoding.UTF8.GetBytes("original-webdav-password");
        byte[] proposedPassword = Encoding.UTF8.GetBytes("proposed-webdav-password");
        byte[] recoveryCode = Encoding.UTF8.GetBytes("existing-space-recovery-code");
        byte[] wrongRecoveryCode = Encoding.UTF8.GetBytes("wrong-existing-recovery-code");
        byte[]? recoveryEnvelope = null;
        byte[]? mismatchedRecoveryEnvelope = null;
        Dictionary<string, byte[]>? originalSecrets = null;
        try
        {
            SyncSetupRequest originalRequest = new(new SyncRemoteConfiguration(
                new Uri("https://dav.example.test/original/"),
                "SnapBoard/v1",
                "original-user",
                new string('a', 64)));
            SyncSetupResult created = await service.CreateSpaceAsync(
                originalRequest,
                originalPassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath!);
            SyncConfigurationSnapshot originalConfiguration =
                Assert.IsType<SyncConfigurationSnapshot>(
                    await context.Store.GetConfigurationAsync(CancellationToken.None));
            originalSecrets = secrets.CopySecrets();

            SyncSetupRequest proposedRequest = new(new SyncRemoteConfiguration(
                new Uri("https://dav.example.test/proposed/"),
                "ProposedRoot/v2",
                "proposed-user",
                new string('b', 64)));
            SyncSetupResult wrongCode = await service.JoinSpaceAsync(
                created.SpaceId!.Value,
                keyVersion: 1,
                proposedRequest,
                proposedPassword,
                recoveryEnvelope,
                wrongRecoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.CryptographicFailure, wrongCode.Status);
            Assert.Equal(originalConfiguration, await context.Store.GetConfigurationAsync(
                CancellationToken.None));
            AssertSecretSnapshotEquals(originalSecrets, secrets);

            remote.RejectNextEnsure(SyncRemoteErrorCategory.Certificate);
            SyncSetupResult rejectedCertificate = await service.JoinSpaceAsync(
                created.SpaceId.Value,
                keyVersion: 1,
                proposedRequest,
                proposedPassword,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.RemoteProtocolError, rejectedCertificate.Status);
            Assert.Equal("remote-hierarchy-failed", rejectedCertificate.DiagnosticCode);
            Assert.Equal(originalConfiguration, await context.Store.GetConfigurationAsync(
                CancellationToken.None));
            AssertSecretSnapshotEquals(originalSecrets, secrets);

            PlatformSyncKeyService alternateKeyService = new(
                alternateSecrets,
                new SyncRecoveryKdfParameters(
                    MemoryKiB: 8 * 1024,
                    Iterations: 2,
                    Parallelism: 1));
            SyncSpaceKeyCreationResult alternateKey =
                await alternateKeyService.CreateSpaceKeyAsync(
                    Guid.NewGuid(),
                    keyVersion: 1,
                    recoveryCode,
                    CancellationToken.None);
            Assert.Equal(SyncKeyOperationStatus.Success, alternateKey.Status);
            mismatchedRecoveryEnvelope = Assert.IsType<byte[]>(alternateKey.RecoveryEnvelope);

            SyncSetupResult mismatchedKey = await service.JoinSpaceAsync(
                created.SpaceId.Value,
                keyVersion: 1,
                proposedRequest,
                proposedPassword,
                mismatchedRecoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.CryptographicFailure, mismatchedKey.Status);
            Assert.Equal("recovery-key-mismatch", mismatchedKey.DiagnosticCode);
            Assert.Equal(originalConfiguration, await context.Store.GetConfigurationAsync(
                CancellationToken.None));
            AssertSecretSnapshotEquals(originalSecrets, secrets);

            SyncStatusSnapshot synchronized = await service.SynchronizeNowAsync(
                CancellationToken.None);
            Assert.Equal(SyncServiceState.Idle, synchronized.State);
        }
        finally
        {
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }

            if (mismatchedRecoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(mismatchedRecoveryEnvelope);
            }

            if (originalSecrets is not null)
            {
                foreach (byte[] secret in originalSecrets.Values)
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }

            CryptographicOperations.ZeroMemory(originalPassword);
            CryptographicOperations.ZeroMemory(proposedPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
            CryptographicOperations.ZeroMemory(wrongRecoveryCode);
            secrets.Clear();
            alternateSecrets.Clear();
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
    public async Task OfflineDevicesConvergeBidirectionallyAcrossConflictsDeletionAndRetention()
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
        byte[] firstPassword = Encoding.UTF8.GetBytes("first-device-app-password");
        byte[] secondPassword = Encoding.UTF8.GetBytes("second-device-app-password");
        byte[] recoveryCode = Encoding.UTF8.GetBytes("cross-device-recovery-code");
        byte[]? recoveryEnvelope = null;
        try
        {
            SyncSetupRequest firstRequest = new(new SyncRemoteConfiguration(
                new Uri("https://dav.example.test/base/"),
                "SnapBoard/v1",
                "windows-device-user"));
            SyncSetupRequest secondRequest = new(new SyncRemoteConfiguration(
                new Uri("https://dav.example.test/base/"),
                "SnapBoard/v1",
                "macos-device-user"));
            SyncSetupResult created = await first.CreateSpaceAsync(
                firstRequest,
                firstPassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath!);
            SyncSetupResult joined = await second.JoinSpaceAsync(
                created.SpaceId!.Value,
                keyVersion: 1,
                secondRequest,
                secondPassword,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, joined.Status);

            string credentialName = $"sync/webdav/{created.SpaceId.Value:N}";
            byte[] firstCredential = firstSecrets.CopySecret(credentialName);
            byte[] secondCredential = secondSecrets.CopySecret(credentialName);
            try
            {
                Assert.NotEqual(firstCredential, secondCredential);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(firstCredential);
                CryptographicOperations.ZeroMemory(secondCredential);
            }

            ClipboardCapturedItem shared = CreateTextItem("shared-conflict-item");
            await firstContext.Store.SaveAsync(shared, CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);

            Assert.True(await firstContext.Store.SetPinnedAsync(
                shared.Id,
                true,
                CancellationToken.None));
            Assert.True(await firstContext.Store.SetTagsAsync(
                shared.Id,
                ["alpha"],
                CancellationToken.None));
            Assert.True(await secondContext.Store.SetPinnedAsync(
                shared.Id,
                true,
                CancellationToken.None));
            Assert.True(await secondContext.Store.SetPinnedAsync(
                shared.Id,
                false,
                CancellationToken.None));
            Assert.True(await secondContext.Store.SetTagsAsync(
                shared.Id,
                ["beta"],
                CancellationToken.None));

            ClipboardCapturedItem firstOnly = CreateTextItem("first-offline-addition");
            ClipboardCapturedItem secondOnly = CreateTextItem("second-offline-addition");
            await firstContext.Store.SaveAsync(firstOnly, CancellationToken.None);
            await secondContext.Store.SaveAsync(secondOnly, CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);

            ClipboardHistoryItemSummary firstShared = await GetItemAsync(
                firstContext,
                shared.Id);
            ClipboardHistoryItemSummary secondShared = await GetItemAsync(
                secondContext,
                shared.Id);
            Assert.Equal(firstShared.IsPinned, secondShared.IsPinned);
            Assert.Equal(firstShared.Tags, secondShared.Tags);
            Assert.Equal(
                await GetVisibleItemIdsAsync(firstContext),
                await GetVisibleItemIdsAsync(secondContext));

            Assert.True(await firstContext.Store.SoftDeleteAsync(
                shared.Id,
                CancellationToken.None));
            Assert.True(await secondContext.Store.SetTagsAsync(
                shared.Id,
                ["offline-stale-update"],
                CancellationToken.None));
            await SynchronizeUntilConvergedAsync(first, second);
            Assert.DoesNotContain(shared.Id, await GetVisibleItemsAsync(firstContext));
            Assert.DoesNotContain(shared.Id, await GetVisibleItemsAsync(secondContext));

            DateTimeOffset oldCaptureTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(60);
            ClipboardCapturedItem oldPinned = CreateTextItem("old-pinned", oldCaptureTime);
            ClipboardCapturedItem oldExpired = CreateTextItem("old-expired", oldCaptureTime);
            await firstContext.Store.SaveAsync(oldPinned, CancellationToken.None);
            Assert.True(await firstContext.Store.SetPinnedAsync(
                oldPinned.Id,
                true,
                CancellationToken.None));
            await firstContext.Store.SaveAsync(oldExpired, CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);

            Assert.Equal(1, await firstContext.Store.ApplyRetentionAsync(
                ClipboardRetentionPolicy.Default,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
            await SynchronizeUntilConvergedAsync(first, second);
            IReadOnlyList<ClipboardItemId> firstVisible = await GetVisibleItemsAsync(firstContext);
            IReadOnlyList<ClipboardItemId> secondVisible = await GetVisibleItemsAsync(secondContext);
            Assert.Contains(oldPinned.Id, firstVisible);
            Assert.Contains(oldPinned.Id, secondVisible);
            Assert.DoesNotContain(oldExpired.Id, firstVisible);
            Assert.DoesNotContain(oldExpired.Id, secondVisible);
        }
        finally
        {
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }

            CryptographicOperations.ZeroMemory(firstPassword);
            CryptographicOperations.ZeroMemory(secondPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
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

    private static async Task AssertFileDoesNotContainAnyAsync(
        string path,
        IReadOnlyList<byte[]> forbidden)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        if (stream.Length > int.MaxValue)
        {
            throw new InvalidOperationException("Test file is unexpectedly large.");
        }

        byte[] content = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        try
        {
            await stream.ReadExactlyAsync(content);
            foreach (byte[] value in forbidden)
            {
                Assert.True(
                    content.AsSpan().IndexOf(value) < 0,
                    $"Sensitive material was persisted in {Path.GetFileName(path)}.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static void AssertSecretSnapshotEquals(
        IReadOnlyDictionary<string, byte[]> expected,
        DictionarySecretStore actualStore)
    {
        Dictionary<string, byte[]> actual = actualStore.CopySecrets();
        try
        {
            Assert.Equal(
                expected.Keys.Order(StringComparer.Ordinal),
                actual.Keys.Order(StringComparer.Ordinal));
            foreach ((string name, byte[] expectedSecret) in expected)
            {
                Assert.Equal(expectedSecret, actual[name]);
            }
        }
        finally
        {
            foreach (byte[] secret in actual.Values)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
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

    private static ClipboardCapturedItem CreateTextItem(
        string text,
        DateTimeOffset? capturedAt = null)
    {
        ClipboardItemId id = ClipboardItemId.New();
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
            SourceProcessName = "local-device-only",
            SourceExecutablePath = "/private/local-device-only",
            ContentHash = new ClipboardContentHash(Hash(textBytes)),
            PrimaryKind = ClipboardContentKind.Text,
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
            ],
            Formats = [new ClipboardCapturedFormat("text", "Text", true)],
            TotalSizeBytes = textBytes.LongLength,
        };
    }

    private static async Task SynchronizeUntilConvergedAsync(
        SyncService first,
        SyncService second)
    {
        for (int round = 0; round < 2; round++)
        {
            Assert.Equal(
                SyncServiceState.Idle,
                (await first.SynchronizeNowAsync(CancellationToken.None)).State);
            Assert.Equal(
                SyncServiceState.Idle,
                (await second.SynchronizeNowAsync(CancellationToken.None)).State);
        }
    }

    private static async Task<ClipboardHistoryItemSummary> GetItemAsync(
        HistoryStoreTestContext context,
        ClipboardItemId itemId) => Assert.Single(
            (await context.Store.SearchAsync(
                new ClipboardHistoryQuery { PageSize = 100 },
                CancellationToken.None)).Items,
            item => item.Id == itemId);

    private static async Task<IReadOnlyList<ClipboardItemId>> GetVisibleItemsAsync(
        HistoryStoreTestContext context) => (await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 100 },
            CancellationToken.None)).Items.Select(item => item.Id).ToArray();

    private static async Task<string[]> GetVisibleItemIdsAsync(
        HistoryStoreTestContext context) => (await GetVisibleItemsAsync(context))
            .Select(itemId => itemId.Value.ToString("N"))
            .Order(StringComparer.Ordinal)
            .ToArray();

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

        public Dictionary<string, byte[]> CopySecrets() => _secrets.ToDictionary(
            item => item.Key,
            item => item.Value.ToArray(),
            StringComparer.Ordinal);

        public byte[] CopySecret(string name) => _secrets[name].ToArray();
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
        private SyncRemoteErrorCategory _nextEnsureFailure;
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

        public void RejectNextEnsure(SyncRemoteErrorCategory category)
        {
            if (category == SyncRemoteErrorCategory.None)
            {
                throw new ArgumentOutOfRangeException(nameof(category));
            }

            lock (_gate)
            {
                if (_nextEnsureFailure != SyncRemoteErrorCategory.None)
                {
                    throw new InvalidOperationException("An ensure failure is already pending.");
                }

                _nextEnsureFailure = category;
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
                _nextEnsureFailure = SyncRemoteErrorCategory.None;
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
                SyncRemoteErrorCategory failure;
                lock (owner._gate)
                {
                    block = owner._nextEnsureBlock;
                    owner._nextEnsureBlock = null;
                    failure = owner._nextEnsureFailure;
                    owner._nextEnsureFailure = SyncRemoteErrorCategory.None;
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

                    return failure == SyncRemoteErrorCategory.None
                        ? Success()
                        : new SyncRemoteResult(false, failure);
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
