using System.Security.Cryptography;
using SnapBoard.Application.Sync;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Platform.Abstractions.Security;

namespace SnapBoard.Infrastructure.Tests;

public sealed class PlatformSyncCredentialServiceTests
{
    [Fact]
    public async Task ProtectedBundleRoundTripsRemoteConfigurationAndPassword()
    {
        using MemorySecretStore secrets = new();
        PlatformSyncCredentialService service = new(secrets);
        Guid spaceId = Guid.NewGuid();
        SyncRemoteConfiguration configuration = new(
            new Uri("https://dav.example.test/user/"),
            "SnapBoard/v1",
            "alice",
            new string('a', 64));
        byte[] password = "application-password"u8.ToArray();
        try
        {
            SyncCredentialOperationStatus stored = await service.StoreAsync(
                spaceId,
                configuration,
                password,
                CancellationToken.None);
            Assert.Equal(SyncCredentialOperationStatus.Success, stored);
            Assert.Equal(1, secrets.WriteCount);

            SyncCredentialOpenResult opened = await service.OpenAsync(
                spaceId,
                CancellationToken.None);
            Assert.Equal(SyncCredentialOperationStatus.Success, opened.Status);
            using SyncCredentialLease lease = Assert.IsType<SyncCredentialLease>(
                opened.Credential);
            Assert.Equal(configuration.Endpoint, lease.RemoteConfiguration.Endpoint);
            Assert.Equal(configuration.RemoteRoot, lease.RemoteConfiguration.RemoteRoot);
            Assert.Equal(configuration.Username, lease.RemoteConfiguration.Username);
            Assert.Equal(
                configuration.CertificateSha256Pin,
                lease.RemoteConfiguration.CertificateSha256Pin);
            Assert.Equal(password, lease.Password.ToArray());

            lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Password.Length);
            Assert.Throws<ObjectDisposedException>(() => lease.RemoteConfiguration);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    [Fact]
    public async Task CorruptedOrNonCanonicalBundleFailsClosed()
    {
        using MemorySecretStore secrets = new();
        secrets.Set([0x53, 0x42, 0x57, 0x43, 0x01, 0x00, 0x01]);
        PlatformSyncCredentialService service = new(secrets);

        SyncCredentialOpenResult opened = await service.OpenAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(SyncCredentialOperationStatus.Failed, opened.Status);
        Assert.Null(opened.Credential);
    }

    [Fact]
    public async Task OversizedCombinedBundleIsRejectedBeforePlatformWrite()
    {
        using MemorySecretStore secrets = new();
        PlatformSyncCredentialService service = new(secrets);
        string remoteRoot = string.Join(
            '/',
            Enumerable.Repeat(new string('r', 100), 8));
        SyncRemoteConfiguration configuration = new(
            new Uri($"https://dav.example.test/{new string('e', 800)}/"),
            remoteRoot,
            new string('u', 200));
        byte[] password = Enumerable.Repeat((byte)'p', 1000).ToArray();
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await service.StoreAsync(
                    Guid.NewGuid(),
                    configuration,
                    password,
                    CancellationToken.None));
            Assert.Equal(0, secrets.WriteCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    [Fact]
    public async Task MigrationSlotsCommitIdempotentlyAndRollbackToSource()
    {
        using NamedMemorySecretStore secrets = new();
        using PlatformSyncCredentialService service = new(secrets);
        Guid spaceId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        SyncRemoteConfiguration source = new(
            new Uri("https://source.example.test/dav/"),
            "SourceRoot",
            "source-user");
        SyncRemoteConfiguration target = new(
            new Uri("https://target.example.test/dav/"),
            "TargetRoot",
            "target-user",
            new string('c', 64));
        byte[] sourcePassword = "source-password"u8.ToArray();
        byte[] targetPassword = "target-password"u8.ToArray();
        try
        {
            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await service.StoreAsync(
                    spaceId,
                    source,
                    sourcePassword,
                    CancellationToken.None));
            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await service.StageCurrentForMigrationAsync(
                    spaceId,
                    planId,
                    CancellationToken.None));
            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await service.StageMigrationTargetAsync(
                    spaceId,
                    planId,
                    target,
                    targetPassword,
                    CancellationToken.None));
            Assert.Equal(
                SyncCredentialOperationStatus.Conflict,
                await service.StageMigrationTargetAsync(
                    spaceId,
                    planId,
                    target with { },
                    "different-password"u8.ToArray(),
                    CancellationToken.None));

            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await service.CommitMigrationTargetAsync(
                    spaceId,
                    planId,
                    CancellationToken.None));
            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await service.CommitMigrationTargetAsync(
                    spaceId,
                    planId,
                    CancellationToken.None));
            using (SyncCredentialLease activeTarget = Assert.IsType<SyncCredentialLease>(
                (await service.OpenAsync(spaceId, CancellationToken.None)).Credential))
            {
                Assert.Equal(target.Endpoint, activeTarget.RemoteConfiguration.Endpoint);
                Assert.Equal(targetPassword, activeTarget.Password.ToArray());
            }

            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await service.RollbackMigrationSourceAsync(
                    spaceId,
                    planId,
                    CancellationToken.None));
            using SyncCredentialLease activeSource = Assert.IsType<SyncCredentialLease>(
                (await service.OpenAsync(spaceId, CancellationToken.None)).Credential);
            Assert.Equal(source.Endpoint, activeSource.RemoteConfiguration.Endpoint);
            Assert.Equal(sourcePassword, activeSource.Password.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourcePassword);
            CryptographicOperations.ZeroMemory(targetPassword);
        }
    }

    private sealed class MemorySecretStore : IPlatformSecretStore, IDisposable
    {
        private byte[]? _secret;

        public int WriteCount { get; private set; }

        public ValueTask<PlatformSecretReadResult> ReadAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_secret is null
                ? new PlatformSecretReadResult(PlatformSecretStoreStatus.NotFound)
                : new PlatformSecretReadResult(
                    PlatformSecretStoreStatus.Success,
                    _secret.ToArray()));
        }

        public ValueTask<PlatformSecretWriteResult> WriteAsync(
            string name,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clear();
            _secret = secret.ToArray();
            WriteCount++;
            return ValueTask.FromResult(
                new PlatformSecretWriteResult(PlatformSecretStoreStatus.Success));
        }

        public ValueTask<PlatformSecretWriteResult> DeleteAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clear();
            return ValueTask.FromResult(
                new PlatformSecretWriteResult(PlatformSecretStoreStatus.Success));
        }

        public void Set(byte[] secret)
        {
            Clear();
            _secret = secret.ToArray();
        }

        public void Dispose()
        {
            Clear();
            GC.SuppressFinalize(this);
        }

        private void Clear()
        {
            if (_secret is not null)
            {
                CryptographicOperations.ZeroMemory(_secret);
                _secret = null;
            }
        }
    }

    private sealed class NamedMemorySecretStore : IPlatformSecretStore, IDisposable
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

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
}
