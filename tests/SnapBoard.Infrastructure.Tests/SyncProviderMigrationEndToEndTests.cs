using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Sync;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.WebDav;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SyncProviderMigrationEndToEndTests
{
    [Fact]
    public async Task TwoDevicesMigrateCiphertextWithDistinctCredentialsAndResumeAfterFailure()
    {
        await using HistoryStoreTestContext firstContext =
            await HistoryStoreTestContext.CreateAsync();
        await using HistoryStoreTestContext secondContext =
            await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore firstSecrets = new();
        using NamedSecretStore secondSecrets = new();
        using PlatformSyncCredentialService firstCredentials = new(firstSecrets);
        using PlatformSyncCredentialService secondCredentials = new(secondSecrets);
        await using SyncService first = CreateService(
            firstContext,
            firstSecrets,
            firstCredentials,
            remote);
        await using SyncService second = CreateService(
            secondContext,
            secondSecrets,
            secondCredentials,
            remote);
        byte[] firstSourcePassword = "source-password-first"u8.ToArray();
        byte[] secondSourcePassword = "source-password-second"u8.ToArray();
        byte[] firstTargetPassword = "target-password-first"u8.ToArray();
        byte[] secondTargetPassword = "target-password-second"u8.ToArray();
        byte[] recoveryCode = "provider-migration-recovery-code"u8.ToArray();
        byte[] html = Enumerable.Range(0, 70 * 1024)
            .Select(static index => (byte)('a' + index % 23))
            .ToArray();
        byte[]? recoveryEnvelope = null;
        try
        {
            SyncRemoteConfiguration firstSource = SourceConfiguration("windows-source-user");
            SyncRemoteConfiguration secondSource = SourceConfiguration("macos-source-user");
            SyncSetupResult created = await first.CreateSpaceAsync(
                new SyncSetupRequest(firstSource),
                firstSourcePassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath!);
            SyncSetupResult joined = await second.JoinSpaceAsync(
                created.SpaceId!.Value,
                keyVersion: 1,
                new SyncSetupRequest(secondSource),
                secondSourcePassword,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, joined.Status);
            await SynchronizeUntilConvergedAsync(first, second);

            ClipboardCapturedItem blobItem = CreateHtmlItem(html);
            await firstContext.Store.SaveAsync(blobItem, CancellationToken.None);
            await first.UpdatePollingSettingsAsync(
                new SyncPollingSettings(15 * 60),
                CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);
            Assert.True(await firstContext.Store.SoftDeleteAsync(
                blobItem.Id,
                CancellationToken.None));
            await SynchronizeUntilConvergedAsync(first, second);
            RemoteSnapshot sourceBefore = remote.Snapshot(firstSource, created.SpaceId.Value);
            Assert.True(sourceBefore.EventCount >= 3);
            Assert.Equal(1, sourceBefore.BlobCount);

            SyncProviderMigrationWatermark firstBefore = await firstContext.Store
                .CaptureProviderMigrationWatermarkAsync(
                    created.SpaceId.Value,
                    created.DeviceId!.Value,
                    CancellationToken.None);
            SyncProviderMigrationWatermark secondBefore = await secondContext.Store
                .CaptureProviderMigrationWatermarkAsync(
                    created.SpaceId.Value,
                    joined.DeviceId!.Value,
                    CancellationToken.None);
            SyncRemoteConfiguration firstTarget = TargetConfiguration("windows-target-user");
            SyncRemoteConfiguration secondTarget = TargetConfiguration("macos-target-user");
            SyncProviderMigrationResult started = await first.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(firstTarget),
                firstTargetPassword,
                CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.WaitingForDevices, started.Status);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            Assert.Equal(
                SyncProviderMigrationState.WaitingForDeviceAcks,
                started.Snapshot.State);

            SyncProviderMigrationResult offline = await first.ContinueProviderMigrationAsync(
                planId,
                CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.WaitingForDevices, offline.Status);
            Assert.Equal(
                SyncProviderMigrationState.WaitingForDeviceAcks,
                offline.Snapshot.State);
            SyncStatusSnapshot blocked = await second.SynchronizeNowAsync(CancellationToken.None);
            Assert.Equal(SyncServiceState.Error, blocked.State);
            Assert.Equal(
                "provider-migration-target-credentials-required",
                blocked.DiagnosticCode);
            Assert.Equal(
                SyncProviderMigrationState.TargetCredentialsRequired,
                second.ProviderMigration.State);

            SyncProviderMigrationResult prepared =
                await second.ProvideProviderMigrationCredentialsAsync(
                    planId,
                    new SyncProviderMigrationRequest(secondTarget),
                    secondTargetPassword,
                    CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.WaitingForDevices, prepared.Status);
            remote.FailCiphertextWriteAfter(firstTarget, successfulWrites: 1);
            SyncProviderMigrationResult interrupted = await first.ContinueProviderMigrationAsync(
                planId,
                CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.RemoteUnavailable, interrupted.Status);
            Assert.Equal(
                "provider-migration-target-object-write-failed",
                interrupted.DiagnosticCode);
            Assert.True(remote.Snapshot(firstTarget, created.SpaceId.Value).ObjectCount >= 1);

            SyncProviderMigrationResult firstCommitted =
                await first.ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.True(
                firstCommitted.Status == SyncProviderMigrationStatus.WaitingForDevices,
                $"Unexpected retry result: {firstCommitted.Status}/{firstCommitted.DiagnosticCode}");
            Assert.Equal(
                SyncProviderMigrationState.WaitingForDeviceCommits,
                firstCommitted.Snapshot.State);
            SyncProviderMigrationResult secondCommitted =
                await second.ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.WaitingForDevices, secondCommitted.Status);
            SyncProviderMigrationResult firstCompleted =
                await first.ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, firstCompleted.Status);
            Assert.Equal(SyncProviderMigrationState.Completed, firstCompleted.Snapshot.State);
            Assert.True(firstCompleted.Snapshot.OldRemoteRetained);
            SyncProviderMigrationResult secondCompleted =
                await second.ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, secondCompleted.Status);
            Assert.Equal(SyncProviderMigrationState.Completed, secondCompleted.Snapshot.State);

            RemoteSnapshot sourceAfter = remote.Snapshot(firstSource, created.SpaceId.Value);
            RemoteSnapshot targetAfter = remote.Snapshot(firstTarget, created.SpaceId.Value);
            Assert.Equal(sourceBefore.MainCiphertextSha256, sourceAfter.MainCiphertextSha256);
            Assert.Equal(sourceAfter.MainCiphertextSha256, targetAfter.MainCiphertextSha256);
            Assert.Equal(sourceAfter.ObjectCount, targetAfter.ObjectCount);
            Assert.True(remote.HasMarker(
                firstSource,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Completed));
            Assert.True(remote.HasMarker(
                firstTarget,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Completed));

            using (SyncCredentialLease firstActive = Assert.IsType<SyncCredentialLease>(
                (await firstCredentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential))
            using (SyncCredentialLease secondActive = Assert.IsType<SyncCredentialLease>(
                (await secondCredentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential))
            {
                Assert.Equal(firstTarget.Username, firstActive.RemoteConfiguration.Username);
                Assert.Equal(secondTarget.Username, secondActive.RemoteConfiguration.Username);
                Assert.Equal(firstTargetPassword, firstActive.Password.ToArray());
                Assert.Equal(secondTargetPassword, secondActive.Password.ToArray());
            }

            Assert.True(firstSecrets.Contains(
                $"sync/webdav/{created.SpaceId.Value:N}/migration/{planId:N}/source"));
            Assert.False(firstSecrets.Contains(
                $"sync/webdav/{created.SpaceId.Value:N}/migration/{planId:N}/target"));
            Assert.Equal(
                firstBefore.Checkpoints.Select(static value =>
                    (value.DeviceId, value.AppliedSequence, value.AppliedEventId)),
                (await firstContext.Store.CaptureProviderMigrationWatermarkAsync(
                    created.SpaceId.Value,
                    created.DeviceId.Value,
                    CancellationToken.None)).Checkpoints.Select(static value =>
                    (value.DeviceId, value.AppliedSequence, value.AppliedEventId)));
            Assert.Equal(
                secondBefore.Checkpoints.Select(static value =>
                    (value.DeviceId, value.AppliedSequence, value.AppliedEventId)),
                (await secondContext.Store.CaptureProviderMigrationWatermarkAsync(
                    created.SpaceId.Value,
                    joined.DeviceId.Value,
                    CancellationToken.None)).Checkpoints.Select(static value =>
                    (value.DeviceId, value.AppliedSequence, value.AppliedEventId)));
            remote.AssertDoesNotContain(
                firstTarget,
                firstTarget.Username,
                secondTarget.Username,
                Encoding.UTF8.GetString(firstTargetPassword),
                Encoding.UTF8.GetString(secondTargetPassword),
                Encoding.UTF8.GetString(recoveryCode));
        }
        finally
        {
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }

            CryptographicOperations.ZeroMemory(firstSourcePassword);
            CryptographicOperations.ZeroMemory(secondSourcePassword);
            CryptographicOperations.ZeroMemory(firstTargetPassword);
            CryptographicOperations.ZeroMemory(secondTargetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
            CryptographicOperations.ZeroMemory(html);
        }
    }

    [LiveWebDavFact]
    public async Task TwoDevicesMigrateBetweenRealWebDavEndpoints()
    {
        await using HistoryStoreTestContext firstContext =
            await HistoryStoreTestContext.CreateAsync();
        await using HistoryStoreTestContext secondContext =
            await HistoryStoreTestContext.CreateAsync();
        using NamedSecretStore firstSecrets = new();
        using NamedSecretStore secondSecrets = new();
        using PlatformSyncCredentialService firstCredentials = new(firstSecrets);
        using PlatformSyncCredentialService secondCredentials = new(secondSecrets);
        WebDavSyncRemoteSessionFactory remote = new();
        await using SyncService first = CreateService(
            firstContext,
            firstSecrets,
            firstCredentials,
            remote,
            remote);
        await using SyncService second = CreateService(
            secondContext,
            secondSecrets,
            secondCredentials,
            remote,
            remote);
        string runId = Guid.NewGuid().ToString("N");
        string remoteRoot = $"SnapBoardLive/{runId}";
        Uri sourceEndpoint = new(GetRequiredLiveValue(
            LiveWebDavFactAttribute.SourceEndpointVariable));
        Uri targetEndpoint = new(GetRequiredLiveValue(
            LiveWebDavFactAttribute.TargetEndpointVariable));
        string firstUsername = GetRequiredLiveValue(
            LiveWebDavFactAttribute.FirstUsernameVariable);
        string secondUsername = GetRequiredLiveValue(
            LiveWebDavFactAttribute.SecondUsernameVariable);
        byte[] firstPassword = Encoding.UTF8.GetBytes(GetRequiredLiveValue(
            LiveWebDavFactAttribute.FirstPasswordVariable));
        byte[] secondPassword = Encoding.UTF8.GetBytes(GetRequiredLiveValue(
            LiveWebDavFactAttribute.SecondPasswordVariable));
        byte[] recoveryCode = "live-webdav-provider-migration-recovery"u8.ToArray();
        byte[]? recoveryEnvelope = null;
        try
        {
            Assert.NotEqual(firstUsername, secondUsername);
            SyncRemoteConfiguration firstSource = LiveConfiguration(
                sourceEndpoint,
                remoteRoot,
                firstUsername);
            SyncRemoteConfiguration secondSource = LiveConfiguration(
                sourceEndpoint,
                remoteRoot,
                secondUsername);
            SyncRemoteConfiguration firstTarget = LiveConfiguration(
                targetEndpoint,
                remoteRoot,
                firstUsername);
            SyncRemoteConfiguration secondTarget = LiveConfiguration(
                targetEndpoint,
                remoteRoot,
                secondUsername);
            SyncSetupResult created = await first.CreateSpaceAsync(
                new SyncSetupRequest(firstSource),
                firstPassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath!);
            SyncSetupResult joined = await second.JoinSpaceAsync(
                created.SpaceId!.Value,
                keyVersion: 1,
                new SyncSetupRequest(secondSource),
                secondPassword,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, joined.Status);
            await SynchronizeUntilConvergedAsync(first, second);

            ClipboardCapturedItem tombstone = CreateTextItem("live-webdav-tombstone");
            await firstContext.Store.SaveAsync(tombstone, CancellationToken.None);
            await secondContext.Store.SaveAsync(
                CreateTextItem("live-webdav-second-device"),
                CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);
            Assert.True(await firstContext.Store.SoftDeleteAsync(
                tombstone.Id,
                CancellationToken.None));
            await SynchronizeUntilConvergedAsync(first, second);
            string sourceBefore = await ComputeRemoteCiphertextHashAsync(
                remote,
                firstSource,
                firstPassword,
                created.SpaceId.Value);

            SyncProviderMigrationResult started = await first.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(firstTarget),
                firstPassword,
                CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.WaitingForDevices, started.Status);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            SyncStatusSnapshot detected = await second.SynchronizeNowAsync(
                CancellationToken.None);
            Assert.Equal("provider-migration-target-credentials-required", detected.DiagnosticCode);
            SyncProviderMigrationResult ready = await second
                .ProvideProviderMigrationCredentialsAsync(
                    planId,
                    new SyncProviderMigrationRequest(secondTarget),
                    secondPassword,
                    CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.WaitingForDevices, ready.Status);
            Assert.Equal(
                SyncProviderMigrationStatus.WaitingForDevices,
                (await first.ContinueProviderMigrationAsync(
                    planId,
                    CancellationToken.None)).Status);
            Assert.Equal(
                SyncProviderMigrationStatus.WaitingForDevices,
                (await second.ContinueProviderMigrationAsync(
                    planId,
                    CancellationToken.None)).Status);
            Assert.Equal(
                SyncProviderMigrationStatus.Success,
                (await first.ContinueProviderMigrationAsync(
                    planId,
                    CancellationToken.None)).Status);
            Assert.Equal(
                SyncProviderMigrationStatus.Success,
                (await second.ContinueProviderMigrationAsync(
                    planId,
                    CancellationToken.None)).Status);

            string targetAfter = await ComputeRemoteCiphertextHashAsync(
                remote,
                firstTarget,
                firstPassword,
                created.SpaceId.Value);
            Assert.Equal(sourceBefore, targetAfter);
            await secondContext.Store.SaveAsync(
                CreateTextItem("live-webdav-after-migration"),
                CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);
            Assert.Equal(
                sourceBefore,
                await ComputeRemoteCiphertextHashAsync(
                    remote,
                    firstSource,
                    firstPassword,
                    created.SpaceId.Value));
            Assert.NotEqual(
                sourceBefore,
                await ComputeRemoteCiphertextHashAsync(
                    remote,
                    firstTarget,
                    firstPassword,
                    created.SpaceId.Value));

            using SyncCredentialLease firstActive = Assert.IsType<SyncCredentialLease>(
                (await firstCredentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential);
            using SyncCredentialLease secondActive = Assert.IsType<SyncCredentialLease>(
                (await secondCredentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential);
            Assert.Equal(targetEndpoint, firstActive.RemoteConfiguration.Endpoint);
            Assert.Equal(targetEndpoint, secondActive.RemoteConfiguration.Endpoint);
            Assert.Equal(firstUsername, firstActive.RemoteConfiguration.Username);
            Assert.Equal(secondUsername, secondActive.RemoteConfiguration.Username);
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
        }
    }

    [Fact]
    public async Task ConflictingTargetCiphertextBlocksCommitAndRollbackKeepsSourceAuthoritative()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore secrets = new();
        using PlatformSyncCredentialService credentials = new(secrets);
        await using SyncService service = CreateService(context, secrets, credentials, remote);
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] targetPassword = "target-password"u8.ToArray();
        byte[] recoveryCode = "recovery-code-for-conflict"u8.ToArray();
        try
        {
            SyncRemoteConfiguration source = SourceConfiguration("source-user");
            SyncRemoteConfiguration target = TargetConfiguration("target-user");
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(source),
                sourcePassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            await context.Store.SaveAsync(CreateTextItem("conflicting-object"), CancellationToken.None);
            Assert.Equal(
                SyncServiceState.Idle,
                (await service.SynchronizeNowAsync(CancellationToken.None)).State);
            SyncRemoteCiphertextObjectReference eventReference = remote
                .ListReferences(source, created.SpaceId!.Value)
                .First(static reference => reference.ObjectType == SyncObjectType.Event);
            remote.SeedCiphertext(target, created.SpaceId.Value, eventReference, [1, 2, 3, 4]);

            SyncProviderMigrationResult started = await service.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(target),
                targetPassword,
                CancellationToken.None);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            SyncProviderMigrationResult conflict = await service.ContinueProviderMigrationAsync(
                planId,
                CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.RemoteProtocolError, conflict.Status);
            Assert.Equal("provider-migration-target-object-conflict", conflict.DiagnosticCode);
            using (SyncCredentialLease stillSource = Assert.IsType<SyncCredentialLease>(
                (await credentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential))
            {
                Assert.Equal(source.Endpoint, stillSource.RemoteConfiguration.Endpoint);
            }

            SyncProviderMigrationResult rolledBack =
                await service.CancelOrRollbackProviderMigrationAsync(
                    planId,
                    CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, rolledBack.Status);
            Assert.Equal(SyncProviderMigrationState.RolledBack, rolledBack.Snapshot.State);
            Assert.True(remote.Snapshot(source, created.SpaceId.Value).EventCount >= 1);
            Assert.True(remote.HasMarker(
                source,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Rollback));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(targetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Fact]
    public async Task EmptySpaceRepairsPartialIntentAndCompletionWrites()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore secrets = new();
        using PlatformSyncCredentialService credentials = new(secrets);
        await using SyncService service = CreateService(context, secrets, credentials, remote);
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] targetPassword = "target-password"u8.ToArray();
        byte[] recoveryCode = "empty-space-recovery-code"u8.ToArray();
        try
        {
            SyncRemoteConfiguration source = SourceConfiguration("source-user");
            SyncRemoteConfiguration target = TargetConfiguration("target-user");
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(source),
                sourcePassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            remote.FailNextMarkerWrite(target, SyncProviderMigrationMarkerKind.Intent);

            SyncProviderMigrationResult interruptedStart = await service
                .StartProviderMigrationAsync(
                    new SyncProviderMigrationRequest(target),
                    targetPassword,
                    CancellationToken.None);

            Assert.Equal(SyncProviderMigrationStatus.RemoteUnavailable, interruptedStart.Status);
            Assert.Equal(SyncProviderMigrationState.PreflightTarget, interruptedStart.Snapshot.State);
            Guid planId = Assert.IsType<Guid>(interruptedStart.Snapshot.PlanId);
            Assert.True(remote.HasMarker(
                source,
                created.SpaceId!.Value,
                planId,
                SyncProviderMigrationMarkerKind.Intent));
            Assert.False(remote.HasMarker(
                target,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Intent));
            SyncProviderMigrationResult duplicate = await service.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(target),
                targetPassword,
                CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.InvalidState, duplicate.Status);

            remote.FailNextMarkerWrite(target, SyncProviderMigrationMarkerKind.Completed);
            SyncProviderMigrationResult interruptedCompletion = await service
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(
                SyncProviderMigrationStatus.RemoteUnavailable,
                interruptedCompletion.Status);
            Assert.True(remote.HasMarker(
                source,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Completed));
            Assert.False(remote.HasMarker(
                target,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Completed));

            SyncProviderMigrationResult completed = await service
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, completed.Status);
            Assert.Equal(SyncProviderMigrationState.Completed, completed.Snapshot.State);
            RemoteSnapshot sourceSnapshot = remote.Snapshot(source, created.SpaceId.Value);
            Assert.Equal(0, sourceSnapshot.BlobCount);
            Assert.Equal(sourceSnapshot.ObjectCount, completed.Snapshot.TotalObjects);
            Assert.True(remote.HasMarker(
                target,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Intent));
            Assert.True(remote.HasMarker(
                target,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Completed));
            Assert.Equal(
                sourceSnapshot.MainCiphertextSha256,
                remote.Snapshot(target, created.SpaceId.Value).MainCiphertextSha256);

            SyncProviderMigrationResult repeated = await service
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, repeated.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(targetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Fact]
    public async Task VerifiedMirrorWithInterruptedCommitKeepsSourceUntilRetry()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore secrets = new();
        using PlatformSyncCredentialService credentials = new(secrets);
        await using SyncService service = CreateService(context, secrets, credentials, remote);
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] targetPassword = "target-password"u8.ToArray();
        byte[] recoveryCode = "interrupted-commit-recovery-code"u8.ToArray();
        try
        {
            SyncRemoteConfiguration source = SourceConfiguration("source-user");
            SyncRemoteConfiguration target = TargetConfiguration("target-user");
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(source),
                sourcePassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            await context.Store.SaveAsync(
                CreateTextItem("verified-before-commit"),
                CancellationToken.None);
            Assert.Equal(
                SyncServiceState.Idle,
                (await service.SynchronizeNowAsync(CancellationToken.None)).State);
            SyncProviderMigrationResult started = await service.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(target),
                targetPassword,
                CancellationToken.None);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            remote.FailNextMarkerWrite(target, SyncProviderMigrationMarkerKind.Commit);

            SyncProviderMigrationResult interrupted = await service
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);

            Assert.Equal(SyncProviderMigrationStatus.RemoteUnavailable, interrupted.Status);
            Assert.True(remote.HasMarker(
                source,
                created.SpaceId!.Value,
                planId,
                SyncProviderMigrationMarkerKind.Commit));
            Assert.False(remote.HasMarker(
                target,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Commit));
            using (SyncCredentialLease activeSource = Assert.IsType<SyncCredentialLease>(
                (await credentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential))
            {
                Assert.Equal(source.Endpoint, activeSource.RemoteConfiguration.Endpoint);
            }

            SyncProviderMigrationResult completed = await service
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, completed.Status);
            Assert.Equal(SyncProviderMigrationState.Completed, completed.Snapshot.State);
            Assert.Equal(
                remote.Snapshot(source, created.SpaceId.Value).MainCiphertextSha256,
                remote.Snapshot(target, created.SpaceId.Value).MainCiphertextSha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(targetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Theory]
    [InlineData(
        SyncRemoteErrorCategory.Authentication,
        SyncProviderMigrationStatus.CredentialStoreFailed)]
    [InlineData(
        SyncRemoteErrorCategory.Permission,
        SyncProviderMigrationStatus.PermissionDenied)]
    [InlineData(
        SyncRemoteErrorCategory.RateLimited,
        SyncProviderMigrationStatus.RemoteUnavailable)]
    [InlineData(
        SyncRemoteErrorCategory.Transient,
        SyncProviderMigrationStatus.RemoteUnavailable)]
    [InlineData(
        SyncRemoteErrorCategory.Protocol,
        SyncProviderMigrationStatus.RemoteProtocolError)]
    [InlineData(
        SyncRemoteErrorCategory.Certificate,
        SyncProviderMigrationStatus.RemoteProtocolError)]
    [InlineData(
        SyncRemoteErrorCategory.ResponseTooLarge,
        SyncProviderMigrationStatus.RemoteProtocolError)]
    public async Task TargetFailureNeverSwitchesCredentialsAndRetryCompletes(
        SyncRemoteErrorCategory remoteError,
        SyncProviderMigrationStatus expectedStatus)
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore secrets = new();
        using PlatformSyncCredentialService credentials = new(secrets);
        await using SyncService service = CreateService(context, secrets, credentials, remote);
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] targetPassword = "target-password"u8.ToArray();
        byte[] recoveryCode = "failure-matrix-recovery-code"u8.ToArray();
        try
        {
            SyncRemoteConfiguration source = SourceConfiguration("source-user");
            SyncRemoteConfiguration target = TargetConfiguration("target-user");
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(source),
                sourcePassword,
                recoveryCode,
                CancellationToken.None);
            SyncProviderMigrationResult started = await service.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(target),
                targetPassword,
                CancellationToken.None);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            remote.FailCiphertextWriteAfter(target, successfulWrites: 0, remoteError);

            SyncProviderMigrationResult failed = await service.ContinueProviderMigrationAsync(
                planId,
                CancellationToken.None);

            Assert.Equal(expectedStatus, failed.Status);
            using (SyncCredentialLease active = Assert.IsType<SyncCredentialLease>(
                (await credentials.OpenAsync(
                    created.SpaceId!.Value,
                    CancellationToken.None)).Credential))
            {
                Assert.Equal(source.Endpoint, active.RemoteConfiguration.Endpoint);
            }

            SyncProviderMigrationResult recovered = await service.ContinueProviderMigrationAsync(
                planId,
                CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, recovered.Status);
            Assert.Equal(SyncProviderMigrationState.Completed, recovered.Snapshot.State);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(targetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Fact]
    public async Task SourceEventSequenceGapBlocksCommit()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore secrets = new();
        using PlatformSyncCredentialService credentials = new(secrets);
        await using SyncService service = CreateService(context, secrets, credentials, remote);
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] targetPassword = "target-password"u8.ToArray();
        byte[] recoveryCode = "sequence-gap-recovery-code"u8.ToArray();
        try
        {
            SyncRemoteConfiguration source = SourceConfiguration("source-user");
            SyncRemoteConfiguration target = TargetConfiguration("target-user");
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(source),
                sourcePassword,
                recoveryCode,
                CancellationToken.None);
            await context.Store.SaveAsync(CreateTextItem("event-one"), CancellationToken.None);
            await context.Store.SaveAsync(CreateTextItem("event-two"), CancellationToken.None);
            Assert.Equal(
                SyncServiceState.Idle,
                (await service.SynchronizeNowAsync(CancellationToken.None)).State);
            remote.RemoveLowestEvent(source, created.SpaceId!.Value);
            SyncProviderMigrationResult started = await service.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(target),
                targetPassword,
                CancellationToken.None);

            SyncProviderMigrationResult blocked = await service.ContinueProviderMigrationAsync(
                Assert.IsType<Guid>(started.Snapshot.PlanId),
                CancellationToken.None);

            Assert.Equal(SyncProviderMigrationStatus.RemoteProtocolError, blocked.Status);
            Assert.Equal("provider-migration-event-sequence-gap", blocked.DiagnosticCode);
            using SyncCredentialLease active = Assert.IsType<SyncCredentialLease>(
                (await credentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential);
            Assert.Equal(source.Endpoint, active.RemoteConfiguration.Endpoint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(targetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Fact]
    public async Task OrphanBlobWithMismatchedContentAddressBlocksCommit()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore secrets = new();
        using PlatformSyncCredentialService credentials = new(secrets);
        await using SyncService service = CreateService(context, secrets, credentials, remote);
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] targetPassword = "target-password"u8.ToArray();
        byte[] recoveryCode = "orphan-blob-recovery-code"u8.ToArray();
        byte[] plaintext = "orphan-blob-content"u8.ToArray();
        byte[]? encrypted = null;
        try
        {
            SyncRemoteConfiguration source = SourceConfiguration("source-user");
            SyncRemoteConfiguration target = TargetConfiguration("target-user");
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(source),
                sourcePassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            Guid spaceId = created.SpaceId!.Value;
            const string wrongKeyedBlobId =
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
            PlatformSyncKeyService keyService = new(
                secrets,
                new SyncRecoveryKdfParameters(
                    MemoryKiB: 8 * 1024,
                    Iterations: 2,
                    Parallelism: 1));
            SyncMasterKeyOpenResult key = await keyService.OpenMasterKeyAsync(
                spaceId,
                keyVersion: 1,
                CancellationToken.None);
            using (key.Key)
            {
                encrypted = new SyncObjectProtector().Encrypt(
                    plaintext,
                    new SyncObjectDescriptor(
                        SyncProtocol.CurrentVersion,
                        spaceId,
                        spaceId,
                        SyncObjectType.Blob,
                        Sequence: 0,
                        wrongKeyedBlobId,
                        KeyVersion: 1),
                    key.Key!.Key.Span);
            }

            remote.SeedCiphertext(
                source,
                spaceId,
                new SyncRemoteCiphertextObjectReference(
                    SyncObjectType.Blob,
                    DeviceId: null,
                    Sequence: 0,
                    EventId: null,
                    wrongKeyedBlobId,
                    ETag: null,
                    encrypted.Length),
                encrypted);

            SyncProviderMigrationResult started = await service.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(target),
                targetPassword,
                CancellationToken.None);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            SyncProviderMigrationResult blocked = await service.ContinueProviderMigrationAsync(
                planId,
                CancellationToken.None);

            Assert.Equal(SyncProviderMigrationStatus.RemoteProtocolError, blocked.Status);
            Assert.Equal("provider-migration-blob-identity-invalid", blocked.DiagnosticCode);
            using SyncCredentialLease active = Assert.IsType<SyncCredentialLease>(
                (await credentials.OpenAsync(spaceId, CancellationToken.None)).Credential);
            Assert.Equal(source.Endpoint, active.RemoteConfiguration.Endpoint);
        }
        finally
        {
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(targetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Fact]
    public async Task FailedMigrationBlocksRegularUploadsUntilAuthorityIsResolved()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore secrets = new();
        using PlatformSyncCredentialService credentials = new(secrets);
        await using SyncService service = CreateService(context, secrets, credentials, remote);
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] recoveryCode = "failed-migration-recovery-code"u8.ToArray();
        try
        {
            SyncRemoteConfiguration source = SourceConfiguration("source-user");
            SyncSetupResult created = await service.CreateSpaceAsync(
                new SyncSetupRequest(source),
                sourcePassword,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncSetupStatus.Success, created.Status);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await context.Store.CreateProviderMigrationAsync(
                new SyncProviderMigrationRecord(
                    Guid.NewGuid(),
                    created.SpaceId!.Value,
                    Epoch: 1,
                    created.DeviceId!.Value,
                    new string('a', 64),
                    new string('b', 64),
                    SyncProviderMigrationState.Failed,
                    TotalObjects: 0,
                    TotalBytes: 0,
                    CompletedObjects: 0,
                    CompletedBytes: 0,
                    InventorySha256: null,
                    DiagnosticCode: "provider-migration-authority-unresolved",
                    CreatedAtUnixMilliseconds: now,
                    UpdatedAtUnixMilliseconds: now),
                [created.DeviceId.Value],
                CancellationToken.None);
            await context.Store.SaveAsync(
                CreateTextItem("must-not-upload-while-authority-is-unresolved"),
                CancellationToken.None);

            SyncStatusSnapshot blocked = await service.SynchronizeNowAsync(CancellationToken.None);

            Assert.Equal(SyncServiceState.Error, blocked.State);
            Assert.Equal("provider-migration-authority-unresolved", blocked.DiagnosticCode);
            Assert.Equal(SyncProviderMigrationState.Failed, service.ProviderMigration.State);
            Assert.Equal(0, remote.Snapshot(source, created.SpaceId.Value).EventCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Fact]
    public async Task GlobalRollbackWaitsForEveryDeviceAndClearsTargetSlots()
    {
        await using HistoryStoreTestContext firstContext =
            await HistoryStoreTestContext.CreateAsync();
        await using HistoryStoreTestContext secondContext =
            await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore firstSecrets = new();
        using NamedSecretStore secondSecrets = new();
        using PlatformSyncCredentialService firstCredentials = new(firstSecrets);
        using PlatformSyncCredentialService secondCredentials = new(secondSecrets);
        await using SyncService first = CreateService(
            firstContext,
            firstSecrets,
            firstCredentials,
            remote);
        await using SyncService second = CreateService(
            secondContext,
            secondSecrets,
            secondCredentials,
            remote);
        byte[] firstSourcePassword = "first-source-password"u8.ToArray();
        byte[] secondSourcePassword = "second-source-password"u8.ToArray();
        byte[] firstTargetPassword = "first-target-password"u8.ToArray();
        byte[] secondTargetPassword = "second-target-password"u8.ToArray();
        byte[] recoveryCode = "global-rollback-recovery-code"u8.ToArray();
        byte[]? recoveryEnvelope = null;
        try
        {
            SyncRemoteConfiguration firstSource = SourceConfiguration("first-source-user");
            SyncRemoteConfiguration secondSource = SourceConfiguration("second-source-user");
            SyncRemoteConfiguration firstTarget = TargetConfiguration("first-target-user");
            SyncRemoteConfiguration secondTarget = TargetConfiguration("second-target-user");
            SyncSetupResult created = await first.CreateSpaceAsync(
                new SyncSetupRequest(firstSource),
                firstSourcePassword,
                recoveryCode,
                CancellationToken.None);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath!);
            SyncSetupResult joined = await second.JoinSpaceAsync(
                created.SpaceId!.Value,
                keyVersion: 1,
                new SyncSetupRequest(secondSource),
                secondSourcePassword,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);
            SyncProviderMigrationResult started = await first.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(firstTarget),
                firstTargetPassword,
                CancellationToken.None);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            Assert.Equal(
                SyncServiceState.Error,
                (await second.SynchronizeNowAsync(CancellationToken.None)).State);
            await second.ProvideProviderMigrationCredentialsAsync(
                planId,
                new SyncProviderMigrationRequest(secondTarget),
                secondTargetPassword,
                CancellationToken.None);

            SyncProviderMigrationResult coordinatorWaiting = await first
                .CancelOrRollbackProviderMigrationAsync(planId, CancellationToken.None);

            Assert.Equal(
                SyncProviderMigrationStatus.WaitingForDevices,
                coordinatorWaiting.Status);
            Assert.Equal(
                SyncProviderMigrationState.RollingBack,
                coordinatorWaiting.Snapshot.State);
            SyncProviderMigrationResult participantRolledBack = await second
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, participantRolledBack.Status);
            Assert.Equal(
                SyncProviderMigrationState.RolledBack,
                participantRolledBack.Snapshot.State);
            SyncProviderMigrationResult completedRollback = await first
                .CancelOrRollbackProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.Success, completedRollback.Status);
            Assert.Equal(
                SyncProviderMigrationState.RolledBack,
                completedRollback.Snapshot.State);

            Assert.False(firstSecrets.Contains(
                $"sync/webdav/{created.SpaceId.Value:N}/migration/{planId:N}/target"));
            Assert.False(secondSecrets.Contains(
                $"sync/webdav/{created.SpaceId.Value:N}/migration/{planId:N}/target"));
            Assert.True(firstSecrets.Contains(
                $"sync/webdav/{created.SpaceId.Value:N}/migration/{planId:N}/source"));
            Assert.True(secondSecrets.Contains(
                $"sync/webdav/{created.SpaceId.Value:N}/migration/{planId:N}/source"));
            using (SyncCredentialLease firstActive = Assert.IsType<SyncCredentialLease>(
                (await firstCredentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential))
            using (SyncCredentialLease secondActive = Assert.IsType<SyncCredentialLease>(
                (await secondCredentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential))
            {
                Assert.Equal(firstSource.Endpoint, firstActive.RemoteConfiguration.Endpoint);
                Assert.Equal(secondSource.Endpoint, secondActive.RemoteConfiguration.Endpoint);
            }

            await SynchronizeUntilConvergedAsync(first, second);
            Assert.True(remote.HasMarker(
                firstSource,
                created.SpaceId.Value,
                planId,
                SyncProviderMigrationMarkerKind.Rollback));
        }
        finally
        {
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }

            CryptographicOperations.ZeroMemory(firstSourcePassword);
            CryptographicOperations.ZeroMemory(secondSourcePassword);
            CryptographicOperations.ZeroMemory(firstTargetPassword);
            CryptographicOperations.ZeroMemory(secondTargetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    [Fact]
    public async Task CommittedDeviceFailureRemainsBlockedAndResumesWithoutSplitWrites()
    {
        await using HistoryStoreTestContext firstContext =
            await HistoryStoreTestContext.CreateAsync();
        await using HistoryStoreTestContext secondContext =
            await HistoryStoreTestContext.CreateAsync();
        using StatefulRemoteHub remote = new();
        using NamedSecretStore firstSecrets = new();
        using NamedSecretStore secondSecrets = new();
        using PlatformSyncCredentialService firstCredentials = new(firstSecrets);
        using PlatformSyncCredentialService secondCredentials = new(secondSecrets);
        await using SyncService first = CreateService(
            firstContext,
            firstSecrets,
            firstCredentials,
            remote);
        await using SyncService second = CreateService(
            secondContext,
            secondSecrets,
            secondCredentials,
            remote);
        byte[] firstSourcePassword = "first-source-password"u8.ToArray();
        byte[] secondSourcePassword = "second-source-password"u8.ToArray();
        byte[] firstTargetPassword = "first-target-password"u8.ToArray();
        byte[] secondTargetPassword = "second-target-password"u8.ToArray();
        byte[] recoveryCode = "partial-commit-recovery-code"u8.ToArray();
        byte[]? recoveryEnvelope = null;
        try
        {
            SyncRemoteConfiguration firstSource = SourceConfiguration("first-source-user");
            SyncRemoteConfiguration secondSource = SourceConfiguration("second-source-user");
            SyncRemoteConfiguration firstTarget = TargetConfiguration("first-target-user");
            SyncRemoteConfiguration secondTarget = TargetConfiguration("second-target-user");
            SyncSetupResult created = await first.CreateSpaceAsync(
                new SyncSetupRequest(firstSource),
                firstSourcePassword,
                recoveryCode,
                CancellationToken.None);
            recoveryEnvelope = await File.ReadAllBytesAsync(created.RecoveryMaterialPath!);
            SyncSetupResult joined = await second.JoinSpaceAsync(
                created.SpaceId!.Value,
                keyVersion: 1,
                new SyncSetupRequest(secondSource),
                secondSourcePassword,
                recoveryEnvelope,
                recoveryCode,
                CancellationToken.None);
            await SynchronizeUntilConvergedAsync(first, second);
            SyncProviderMigrationResult started = await first.StartProviderMigrationAsync(
                new SyncProviderMigrationRequest(firstTarget),
                firstTargetPassword,
                CancellationToken.None);
            Guid planId = Assert.IsType<Guid>(started.Snapshot.PlanId);
            _ = await second.SynchronizeNowAsync(CancellationToken.None);
            _ = await second.ProvideProviderMigrationCredentialsAsync(
                planId,
                new SyncProviderMigrationRequest(secondTarget),
                secondTargetPassword,
                CancellationToken.None);
            SyncProviderMigrationResult firstCommitted = await first
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(
                SyncProviderMigrationState.WaitingForDeviceCommits,
                firstCommitted.Snapshot.State);
            remote.FailNextMarkerWrite(
                firstSource,
                SyncProviderMigrationMarkerKind.Committed);

            SyncProviderMigrationResult interrupted = await second
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);

            Assert.Equal(SyncProviderMigrationStatus.RemoteUnavailable, interrupted.Status);
            using (SyncCredentialLease secondActive = Assert.IsType<SyncCredentialLease>(
                (await secondCredentials.OpenAsync(
                    created.SpaceId.Value,
                    CancellationToken.None)).Credential))
            {
                Assert.Equal(secondTarget.Endpoint, secondActive.RemoteConfiguration.Endpoint);
            }

            SyncStatusSnapshot blocked = await second.SynchronizeNowAsync(CancellationToken.None);
            Assert.Equal(SyncServiceState.Error, blocked.State);
            Assert.Equal("provider-migration-write-frozen", blocked.DiagnosticCode);
            SyncProviderMigrationResult secondCommitted = await second
                .ContinueProviderMigrationAsync(planId, CancellationToken.None);
            Assert.Equal(SyncProviderMigrationStatus.WaitingForDevices, secondCommitted.Status);
            Assert.Equal(
                SyncProviderMigrationStatus.Success,
                (await first.ContinueProviderMigrationAsync(
                    planId,
                    CancellationToken.None)).Status);
            Assert.Equal(
                SyncProviderMigrationStatus.Success,
                (await second.ContinueProviderMigrationAsync(
                    planId,
                    CancellationToken.None)).Status);
            Assert.Equal(
                remote.Snapshot(firstSource, created.SpaceId.Value).MainCiphertextSha256,
                remote.Snapshot(firstTarget, created.SpaceId.Value).MainCiphertextSha256);
        }
        finally
        {
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }

            CryptographicOperations.ZeroMemory(firstSourcePassword);
            CryptographicOperations.ZeroMemory(secondSourcePassword);
            CryptographicOperations.ZeroMemory(firstTargetPassword);
            CryptographicOperations.ZeroMemory(secondTargetPassword);
            CryptographicOperations.ZeroMemory(recoveryCode);
        }
    }

    private static SyncService CreateService(
        HistoryStoreTestContext context,
        IPlatformSecretStore secrets,
        PlatformSyncCredentialService credentials,
        StatefulRemoteHub remote) => CreateService(
        context,
        secrets,
        credentials,
        remote,
        remote);

    private static SyncService CreateService(
        HistoryStoreTestContext context,
        IPlatformSecretStore secrets,
        PlatformSyncCredentialService credentials,
        ISyncRemoteSessionFactory remote,
        ISyncRemoteProviderMigrationSessionFactory providerMigrationRemote)
    {
        ClipboardHistoryChangeNotifier notifier = new();
        ClipboardHistoryService history = new(context.Store, notifier);
        return new SyncService(
            context.Store,
            new PlatformSyncKeyService(
                secrets,
                new SyncRecoveryKdfParameters(
                    MemoryKiB: 8 * 1024,
                    Iterations: 2,
                    Parallelism: 1)),
            credentials,
            new FileSyncRecoveryMaterialStore(context.Paths),
            new SyncObjectProtector(),
            remote,
            history,
            options: null,
            historySettingsService: null,
            historyChangeNotifier: notifier,
            providerMigrationSessionFactory: providerMigrationRemote);
    }

    private static SyncRemoteConfiguration LiveConfiguration(
        Uri endpoint,
        string remoteRoot,
        string username) => new(
        endpoint,
        remoteRoot,
        username,
        certificateSha256Pin: null,
        allowInsecureLoopback: endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback);

    private static string GetRequiredLiveValue(string name) =>
        Environment.GetEnvironmentVariable(name) ??
        throw new InvalidOperationException($"The required live WebDAV setting {name} is missing.");

    private static async Task<string> ComputeRemoteCiphertextHashAsync(
        WebDavSyncRemoteSessionFactory factory,
        SyncRemoteConfiguration configuration,
        ReadOnlyMemory<byte> password,
        Guid spaceId)
    {
        await using ISyncRemoteProviderMigrationSession session =
            factory.CreateProviderMigrationSession(configuration, password);
        SyncRemoteCiphertextObjectListResult list = await session.ListCiphertextObjectsAsync(
            spaceId,
            CancellationToken.None);
        Assert.True(list.Result.IsSuccess);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (SyncRemoteCiphertextObjectReference reference in list.Objects)
        {
            string canonicalIdentity = string.Join(
                '|',
                ((int)reference.ObjectType).ToString(CultureInfo.InvariantCulture),
                reference.DeviceId?.ToString("D") ?? string.Empty,
                reference.Sequence.ToString(CultureInfo.InvariantCulture),
                reference.EventId?.ToString("D") ?? string.Empty,
                reference.KeyedBlobId ?? string.Empty);
            byte[] identity = Encoding.UTF8.GetBytes(canonicalIdentity);
            try
            {
                hash.AppendData(identity);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(identity);
            }

            SyncRemoteContentResult content = await session.GetCiphertextObjectAsync(
                spaceId,
                reference,
                CancellationToken.None);
            Assert.True(content.Result.IsSuccess);
            using SyncRemoteContentLease lease = Assert.IsType<SyncRemoteContentLease>(
                content.Content);
            hash.AppendData(lease.Content.Span);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task SynchronizeUntilConvergedAsync(
        SyncService first,
        SyncService second)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            SyncStatusSnapshot firstResult = await first.SynchronizeNowAsync(CancellationToken.None);
            SyncStatusSnapshot secondResult = await second.SynchronizeNowAsync(CancellationToken.None);
            Assert.Equal(SyncServiceState.Idle, firstResult.State);
            Assert.Equal(SyncServiceState.Idle, secondResult.State);
            if (firstResult.UploadedEvents == 0 && firstResult.DownloadedEvents == 0 &&
                secondResult.UploadedEvents == 0 && secondResult.DownloadedEvents == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException("The test devices did not converge.");
    }

    private static SyncRemoteConfiguration SourceConfiguration(string username) => new(
        new Uri("https://source.example.test/dav/"),
        "SnapBoardSource",
        username,
        new string('a', 64));

    private static SyncRemoteConfiguration TargetConfiguration(string username) => new(
        new Uri("https://target.example.test/dav/"),
        "SnapBoardTarget",
        username,
        new string('b', 64));

    private static ClipboardCapturedItem CreateHtmlItem(byte[] html)
    {
        ClipboardItemId id = ClipboardItemId.New();
        const string text = "provider migration blob";
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = DateTimeOffset.UtcNow,
            SourceProcessName = "migration-test",
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
            TotalSizeBytes = textBytes.LongLength + html.LongLength,
        };
    }

    private static ClipboardCapturedItem CreateTextItem(string text)
    {
        ClipboardItemId id = ClipboardItemId.New();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = DateTimeOffset.UtcNow,
            ContentHash = new ClipboardContentHash(Hash(bytes)),
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
            TotalSizeBytes = bytes.LongLength,
        };
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed class NamedSecretStore : IPlatformSecretStore, IDisposable
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public bool Contains(string name) => _secrets.ContainsKey(name);

        public ValueTask<PlatformSecretReadResult> ReadAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_secrets.TryGetValue(name, out byte[]? value)
                ? new PlatformSecretReadResult(
                    PlatformSecretStoreStatus.Success,
                    value.ToArray())
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
            if (_secrets.Remove(name, out byte[]? value))
            {
                CryptographicOperations.ZeroMemory(value);
            }

            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.Success));
        }

        public void Dispose()
        {
            foreach (byte[] value in _secrets.Values)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            _secrets.Clear();
        }
    }

    private sealed class StatefulRemoteHub :
        ISyncRemoteSessionFactory,
        ISyncRemoteProviderMigrationSessionFactory,
        IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<RemoteIdentity, RemoteState> _remotes = [];

        public ISyncRemoteSession Create(
            SyncRemoteConfiguration configuration,
            ReadOnlyMemory<byte> password) => new Session(
            this,
            GetIdentity(configuration));

        public ISyncRemoteProviderMigrationSession CreateProviderMigrationSession(
            SyncRemoteConfiguration configuration,
            ReadOnlyMemory<byte> password) => new Session(
            this,
            GetIdentity(configuration));

        public void FailCiphertextWriteAfter(
            SyncRemoteConfiguration configuration,
            int successfulWrites,
            SyncRemoteErrorCategory category = SyncRemoteErrorCategory.Network)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(successfulWrites);
            if (category == SyncRemoteErrorCategory.None)
            {
                throw new ArgumentOutOfRangeException(nameof(category));
            }

            lock (_gate)
            {
                RemoteState state = GetState(GetIdentity(configuration));
                state.FailCiphertextWriteAfter = successfulWrites;
                state.CiphertextWriteFailureCategory = category;
            }
        }

        public void FailNextMarkerWrite(
            SyncRemoteConfiguration configuration,
            SyncProviderMigrationMarkerKind kind,
            SyncRemoteErrorCategory category = SyncRemoteErrorCategory.Network)
        {
            if (category == SyncRemoteErrorCategory.None)
            {
                throw new ArgumentOutOfRangeException(nameof(category));
            }

            lock (_gate)
            {
                RemoteState state = GetState(GetIdentity(configuration));
                state.MarkerWriteFailureKind = kind;
                state.MarkerWriteFailureCategory = category;
            }
        }

        public void RemoveLowestEvent(
            SyncRemoteConfiguration configuration,
            Guid spaceId)
        {
            lock (_gate)
            {
                RemoteState state = GetState(GetIdentity(configuration));
                var key = state.Events.Keys
                    .Where(value => value.SpaceId == spaceId)
                    .OrderBy(value => value.Sequence)
                    .First();
                byte[] removed = state.Events[key];
                state.Events.Remove(key);
                CryptographicOperations.ZeroMemory(removed);
            }
        }

        public List<SyncRemoteCiphertextObjectReference> ListReferences(
            SyncRemoteConfiguration configuration,
            Guid spaceId)
        {
            lock (_gate)
            {
                return BuildReferences(GetState(GetIdentity(configuration)), spaceId);
            }
        }

        public void SeedCiphertext(
            SyncRemoteConfiguration configuration,
            Guid spaceId,
            SyncRemoteCiphertextObjectReference reference,
            ReadOnlySpan<byte> content)
        {
            lock (_gate)
            {
                RemoteState state = GetState(GetIdentity(configuration));
                StoreCiphertext(state, spaceId, reference, content.ToArray(), overwrite: true);
            }
        }

        public RemoteSnapshot Snapshot(
            SyncRemoteConfiguration configuration,
            Guid spaceId)
        {
            lock (_gate)
            {
                RemoteState state = GetState(GetIdentity(configuration));
                List<SyncRemoteCiphertextObjectReference> references = BuildReferences(state, spaceId);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (SyncRemoteCiphertextObjectReference reference in references)
                {
                    byte[] content = GetCiphertext(state, spaceId, reference)!;
                    hash.AppendData(Encoding.UTF8.GetBytes(Identity(reference)));
                    hash.AppendData(content);
                }

                return new RemoteSnapshot(
                    references.Count,
                    references.Count(static value => value.ObjectType == SyncObjectType.Event),
                    references.Count(static value => value.ObjectType == SyncObjectType.Blob),
                    Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
        }

        public bool HasMarker(
            SyncRemoteConfiguration configuration,
            Guid spaceId,
            Guid planId,
            SyncProviderMigrationMarkerKind kind)
        {
            lock (_gate)
            {
                return GetState(GetIdentity(configuration)).Markers.ContainsKey(
                    (spaceId, planId, kind, null));
            }
        }

        public void AssertDoesNotContain(
            SyncRemoteConfiguration configuration,
            params string[] forbidden)
        {
            lock (_gate)
            {
                RemoteState state = GetState(GetIdentity(configuration));
                foreach (byte[] value in state.AllContent())
                {
                    foreach (string text in forbidden)
                    {
                        Assert.DoesNotContain(Encoding.UTF8.GetBytes(text), value);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (RemoteState state in _remotes.Values)
                {
                    state.Clear();
                }

                _remotes.Clear();
            }
        }

        private RemoteState GetState(RemoteIdentity identity)
        {
            if (!_remotes.TryGetValue(identity, out RemoteState? state))
            {
                state = new RemoteState();
                _remotes.Add(identity, state);
            }

            return state;
        }

        private static RemoteIdentity GetIdentity(SyncRemoteConfiguration configuration) => new(
            configuration.Endpoint.AbsoluteUri,
            configuration.RemoteRoot);

        private sealed class Session(StatefulRemoteHub owner, RemoteIdentity identity) :
            ISyncRemoteSession,
            ISyncRemoteProviderMigrationSession
        {
            public ValueTask<SyncRemoteResult> EnsureHierarchyAsync(
                Guid spaceId,
                Guid localDeviceId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    owner.GetState(identity).Devices.Add((spaceId, localDeviceId));
                    return ValueTask.FromResult(Success());
                }
            }

            public ValueTask<SyncRemoteContentResult> GetMetadataAsync(
                Guid spaceId,
                CancellationToken cancellationToken) => Get(
                state => state.Metadata.TryGetValue(spaceId, out byte[]? value) ? value : null,
                cancellationToken);

            public ValueTask<SyncRemoteResult> PutMetadataAsync(
                Guid spaceId,
                ReadOnlyMemory<byte> encryptedMetadata,
                CancellationToken cancellationToken) => Put(
                state => state.Metadata,
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
                    Guid[] devices = owner.GetState(identity).Devices
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
                    SyncRemoteEventReference[] events = owner.GetState(identity).Events.Keys
                        .Where(value => value.SpaceId == spaceId && value.DeviceId == deviceId)
                        .OrderBy(value => value.Sequence)
                        .Select(value => new SyncRemoteEventReference(
                            value.DeviceId,
                            value.Sequence,
                            value.EventId,
                            null))
                        .ToArray();
                    return ValueTask.FromResult(new SyncRemoteEventListResult(Success(), events));
                }
            }

            public ValueTask<SyncRemoteContentResult> GetEventAsync(
                Guid spaceId,
                SyncRemoteEventReference remoteEvent,
                CancellationToken cancellationToken) => Get(
                state => state.Events.TryGetValue(
                    (spaceId, remoteEvent.DeviceId, remoteEvent.Sequence, remoteEvent.EventId),
                    out byte[]? value) ? value : null,
                cancellationToken);

            public ValueTask<SyncRemoteResult> PutEventAsync(
                Guid spaceId,
                Guid deviceId,
                long sequence,
                Guid eventId,
                ReadOnlyMemory<byte> encryptedEvent,
                CancellationToken cancellationToken) => Put(
                state => state.Events,
                (spaceId, deviceId, sequence, eventId),
                encryptedEvent,
                cancellationToken);

            public ValueTask<SyncRemoteContentResult> GetBlobAsync(
                Guid spaceId,
                string keyedBlobId,
                CancellationToken cancellationToken) => Get(
                state => state.Blobs.TryGetValue((spaceId, keyedBlobId), out byte[]? value)
                    ? value
                    : null,
                cancellationToken);

            public ValueTask<SyncRemoteResult> PutBlobAsync(
                Guid spaceId,
                string keyedBlobId,
                ReadOnlyMemory<byte> encryptedBlob,
                CancellationToken cancellationToken) => Put(
                state => state.Blobs,
                (spaceId, keyedBlobId),
                encryptedBlob,
                cancellationToken);

            public async ValueTask<SyncRemoteResult> EnsureMigrationHierarchyAsync(
                Guid spaceId,
                Guid planId,
                IReadOnlyList<Guid> requiredDeviceIds,
                CancellationToken cancellationToken)
            {
                foreach (Guid deviceId in requiredDeviceIds)
                {
                    _ = await EnsureHierarchyAsync(spaceId, deviceId, cancellationToken);
                }

                lock (owner._gate)
                {
                    owner.GetState(identity).Plans.Add((spaceId, planId));
                    return Success();
                }
            }

            public ValueTask<SyncRemoteCiphertextObjectListResult> ListCiphertextObjectsAsync(
                Guid spaceId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    return ValueTask.FromResult(new SyncRemoteCiphertextObjectListResult(
                        Success(),
                        BuildReferences(owner.GetState(identity), spaceId)));
                }
            }

            public ValueTask<SyncRemoteContentResult> GetCiphertextObjectAsync(
                Guid spaceId,
                SyncRemoteCiphertextObjectReference reference,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    byte[]? content = GetCiphertext(owner.GetState(identity), spaceId, reference);
                    return ValueTask.FromResult(content is null ? NotFound() : Content(content));
                }
            }

            public ValueTask<SyncRemoteResult> PutCiphertextObjectAsync(
                Guid spaceId,
                SyncRemoteCiphertextObjectReference reference,
                ReadOnlyMemory<byte> encryptedContent,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    RemoteState state = owner.GetState(identity);
                    if (GetCiphertext(state, spaceId, reference) is not null)
                    {
                        return ValueTask.FromResult(new SyncRemoteResult(
                            true,
                            SyncRemoteErrorCategory.None,
                            AlreadyExisted: true));
                    }

                    if (state.FailCiphertextWriteAfter == 0)
                    {
                        state.FailCiphertextWriteAfter = -1;
                        return ValueTask.FromResult(new SyncRemoteResult(
                            false,
                            state.CiphertextWriteFailureCategory));
                    }

                    if (state.FailCiphertextWriteAfter > 0)
                    {
                        state.FailCiphertextWriteAfter--;
                    }

                    StoreCiphertext(
                        state,
                        spaceId,
                        reference,
                        encryptedContent.ToArray(),
                        overwrite: false);
                    return ValueTask.FromResult(Success());
                }
            }

            public ValueTask<SyncRemoteProviderMigrationPlanListResult>
                ListProviderMigrationPlansAsync(
                    Guid spaceId,
                    CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    SyncRemoteProviderMigrationPlanReference[] plans = owner.GetState(identity).Plans
                        .Where(value => value.SpaceId == spaceId)
                        .Select(value => new SyncRemoteProviderMigrationPlanReference(
                            value.PlanId,
                            null))
                        .OrderBy(value => value.PlanId.ToString("N"), StringComparer.Ordinal)
                        .ToArray();
                    return ValueTask.FromResult(new SyncRemoteProviderMigrationPlanListResult(
                        Success(),
                        plans));
                }
            }

            public ValueTask<SyncRemoteContentResult> GetProviderMigrationMarkerAsync(
                Guid spaceId,
                SyncProviderMigrationMarkerAddress address,
                CancellationToken cancellationToken) => Get(
                state => state.Markers.TryGetValue(
                    (spaceId, address.PlanId, address.Kind, address.DeviceId),
                    out byte[]? value) ? value : null,
                cancellationToken);

            public ValueTask<SyncRemoteResult> PutProviderMigrationMarkerAsync(
                Guid spaceId,
                SyncProviderMigrationMarkerAddress address,
                ReadOnlyMemory<byte> encryptedMarker,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    RemoteState state = owner.GetState(identity);
                    if (state.MarkerWriteFailureKind == address.Kind)
                    {
                        SyncRemoteErrorCategory category = state.MarkerWriteFailureCategory;
                        state.MarkerWriteFailureKind = null;
                        state.MarkerWriteFailureCategory = SyncRemoteErrorCategory.None;
                        return ValueTask.FromResult(new SyncRemoteResult(false, category));
                    }
                }

                return Put(
                    state => state.Markers,
                    (spaceId, address.PlanId, address.Kind, address.DeviceId),
                    encryptedMarker,
                    cancellationToken,
                    onAdded: state => state.Plans.Add((spaceId, address.PlanId)));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private ValueTask<SyncRemoteContentResult> Get(
                Func<RemoteState, byte[]?> getter,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    byte[]? value = getter(owner.GetState(identity));
                    return ValueTask.FromResult(value is null ? NotFound() : Content(value));
                }
            }

            private ValueTask<SyncRemoteResult> Put<TKey>(
                Func<RemoteState, Dictionary<TKey, byte[]>> destination,
                TKey key,
                ReadOnlyMemory<byte> content,
                CancellationToken cancellationToken,
                Action<RemoteState>? onAdded = null)
                where TKey : notnull
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (owner._gate)
                {
                    RemoteState state = owner.GetState(identity);
                    Dictionary<TKey, byte[]> values = destination(state);
                    if (values.ContainsKey(key))
                    {
                        return ValueTask.FromResult(new SyncRemoteResult(
                            true,
                            SyncRemoteErrorCategory.None,
                            AlreadyExisted: true));
                    }

                    values.Add(key, content.ToArray());
                    onAdded?.Invoke(state);
                    return ValueTask.FromResult(Success());
                }
            }
        }

        private static List<SyncRemoteCiphertextObjectReference> BuildReferences(
            RemoteState state,
            Guid spaceId)
        {
            List<SyncRemoteCiphertextObjectReference> references = [];
            if (state.Metadata.TryGetValue(spaceId, out byte[]? metadata))
            {
                references.Add(new SyncRemoteCiphertextObjectReference(
                    SyncObjectType.Metadata,
                    null,
                    0,
                    null,
                    null,
                    null,
                    metadata.Length));
            }

            references.AddRange(state.Events
                .Where(value => value.Key.SpaceId == spaceId)
                .Select(value => new SyncRemoteCiphertextObjectReference(
                    SyncObjectType.Event,
                    value.Key.DeviceId,
                    value.Key.Sequence,
                    value.Key.EventId,
                    null,
                    null,
                    value.Value.Length)));
            references.AddRange(state.Blobs
                .Where(value => value.Key.SpaceId == spaceId)
                .Select(value => new SyncRemoteCiphertextObjectReference(
                    SyncObjectType.Blob,
                    null,
                    0,
                    null,
                    value.Key.BlobId,
                    null,
                    value.Value.Length)));
            references.Sort(static (left, right) => string.Compare(
                Identity(left),
                Identity(right),
                StringComparison.Ordinal));
            return references;
        }

        private static byte[]? GetCiphertext(
            RemoteState state,
            Guid spaceId,
            SyncRemoteCiphertextObjectReference reference) => reference.ObjectType switch
            {
                SyncObjectType.Metadata => state.Metadata.TryGetValue(spaceId, out byte[]? value)
                    ? value
                    : null,
                SyncObjectType.Event when reference.DeviceId is Guid deviceId &&
                    reference.EventId is Guid eventId => state.Events.TryGetValue(
                        (spaceId, deviceId, reference.Sequence, eventId),
                        out byte[]? value) ? value : null,
                SyncObjectType.Blob when reference.KeyedBlobId is string blobId =>
                    state.Blobs.TryGetValue((spaceId, blobId), out byte[]? value) ? value : null,
                _ => null,
            };

        private static void StoreCiphertext(
            RemoteState state,
            Guid spaceId,
            SyncRemoteCiphertextObjectReference reference,
            byte[] content,
            bool overwrite)
        {
            switch (reference.ObjectType)
            {
                case SyncObjectType.Metadata:
                    Store(state.Metadata, spaceId, content, overwrite);
                    break;
                case SyncObjectType.Event when reference.DeviceId is Guid deviceId &&
                    reference.EventId is Guid eventId:
                    Store(
                        state.Events,
                        (spaceId, deviceId, reference.Sequence, eventId),
                        content,
                        overwrite);
                    break;
                case SyncObjectType.Blob when reference.KeyedBlobId is string blobId:
                    Store(state.Blobs, (spaceId, blobId), content, overwrite);
                    break;
                default:
                    throw new ArgumentException("Invalid ciphertext reference.", nameof(reference));
            }
        }

        private static void Store<TKey>(
            Dictionary<TKey, byte[]> destination,
            TKey key,
            byte[] content,
            bool overwrite)
            where TKey : notnull
        {
            if (overwrite && destination.Remove(key, out byte[]? previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            destination.Add(key, content);
        }

        private static string Identity(SyncRemoteCiphertextObjectReference reference) =>
            reference.ObjectType switch
            {
                SyncObjectType.Metadata => "1/metadata",
                SyncObjectType.Event =>
                    $"2/{reference.DeviceId:N}/{reference.Sequence:D20}/{reference.EventId:N}",
                SyncObjectType.Blob => $"3/{reference.KeyedBlobId}",
                _ => throw new InvalidOperationException(),
            };

        private static SyncRemoteContentResult Content(byte[] content) => new(
            Success(),
            new SyncRemoteContentLease(content.ToArray()));

        private static SyncRemoteContentResult NotFound() => new(
            new SyncRemoteResult(false, SyncRemoteErrorCategory.NotFound));

        private static SyncRemoteResult Success() => new(true, SyncRemoteErrorCategory.None);

        private sealed class RemoteState
        {
            public Dictionary<Guid, byte[]> Metadata { get; } = [];

            public HashSet<(Guid SpaceId, Guid DeviceId)> Devices { get; } = [];

            public Dictionary<(Guid SpaceId, Guid DeviceId, long Sequence, Guid EventId), byte[]>
                Events
            { get; } = [];

            public Dictionary<(Guid SpaceId, string BlobId), byte[]> Blobs { get; } = [];

            public HashSet<(Guid SpaceId, Guid PlanId)> Plans { get; } = [];

            public Dictionary<(Guid SpaceId, Guid PlanId, SyncProviderMigrationMarkerKind Kind,
                Guid? DeviceId), byte[]> Markers
            { get; } = [];

            public int FailCiphertextWriteAfter { get; set; } = -1;

            public SyncRemoteErrorCategory CiphertextWriteFailureCategory { get; set; } =
                SyncRemoteErrorCategory.Network;

            public SyncProviderMigrationMarkerKind? MarkerWriteFailureKind { get; set; }

            public SyncRemoteErrorCategory MarkerWriteFailureCategory { get; set; }

            public IEnumerable<byte[]> AllContent() =>
                Metadata.Values.Concat(Events.Values).Concat(Blobs.Values).Concat(Markers.Values);

            public void Clear()
            {
                foreach (byte[] value in AllContent())
                {
                    CryptographicOperations.ZeroMemory(value);
                }

                Metadata.Clear();
                Devices.Clear();
                Events.Clear();
                Blobs.Clear();
                Plans.Clear();
                Markers.Clear();
            }
        }

        private sealed record RemoteIdentity(string Endpoint, string RemoteRoot);
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveWebDavFactAttribute : FactAttribute
{
    public const string SourceEndpointVariable =
        "SNAPBOARD_LIVE_WEBDAV_SOURCE_ENDPOINT";
    public const string TargetEndpointVariable =
        "SNAPBOARD_LIVE_WEBDAV_TARGET_ENDPOINT";
    public const string FirstUsernameVariable =
        "SNAPBOARD_LIVE_WEBDAV_USERNAME_1";
    public const string FirstPasswordVariable =
        "SNAPBOARD_LIVE_WEBDAV_PASSWORD_1";
    public const string SecondUsernameVariable =
        "SNAPBOARD_LIVE_WEBDAV_USERNAME_2";
    public const string SecondPasswordVariable =
        "SNAPBOARD_LIVE_WEBDAV_PASSWORD_2";

    private static readonly string[] RequiredVariables =
    [
        SourceEndpointVariable,
        TargetEndpointVariable,
        FirstUsernameVariable,
        FirstPasswordVariable,
        SecondUsernameVariable,
        SecondPasswordVariable,
    ];

    public LiveWebDavFactAttribute()
    {
        if (RequiredVariables.Any(static name =>
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip = "Set the SNAPBOARD_LIVE_WEBDAV_* variables to run the live migration test.";
        }
    }
}

public sealed record RemoteSnapshot(
    int ObjectCount,
    int EventCount,
    int BlobCount,
    string MainCiphertextSha256);
