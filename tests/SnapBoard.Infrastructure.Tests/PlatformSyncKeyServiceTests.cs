using System.Security.Cryptography;
using System.Text;
using SnapBoard.Application.Sync;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Platform.Abstractions.Security;

namespace SnapBoard.Infrastructure.Tests;

public sealed class PlatformSyncKeyServiceTests
{
    private static readonly Guid SpaceId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task CreatesImportsAndClearsLeasedMasterKey()
    {
        MemorySecretStore sourceStore = new();
        PlatformSyncKeyService source = CreateService(sourceStore);
        byte[] recoveryCode = Encoding.UTF8.GetBytes("correct horse battery staple");
        SyncSpaceKeyCreationResult created = await source.CreateSpaceKeyAsync(
            SpaceId,
            1,
            recoveryCode,
            CancellationToken.None);

        Assert.Equal(SyncKeyOperationStatus.Success, created.Status);
        Assert.NotNull(created.RecoveryEnvelope);
        Assert.Equal("sync/master/11111111111111111111111111111111/v1", sourceStore.LastName);

        MemorySecretStore destinationStore = new();
        PlatformSyncKeyService destination = CreateService(destinationStore);
        SyncKeyOperationStatus imported = await destination.ImportSpaceKeyAsync(
            SpaceId,
            1,
            created.RecoveryEnvelope,
            recoveryCode,
            CancellationToken.None);
        SyncMasterKeyOpenResult opened = await destination.OpenMasterKeyAsync(
            SpaceId,
            1,
            CancellationToken.None);

        Assert.Equal(SyncKeyOperationStatus.Success, imported);
        Assert.Equal(SyncKeyOperationStatus.Success, opened.Status);
        Assert.NotNull(opened.Key);
        ReadOnlyMemory<byte> leasedMemory = opened.Key.Key;
        Assert.Contains(leasedMemory.Span.ToArray(), value => value != 0);
        opened.Key.Dispose();
        Assert.All(leasedMemory.Span.ToArray(), value => Assert.Equal(0, value));

        CryptographicOperations.ZeroMemory(recoveryCode);
        CryptographicOperations.ZeroMemory(created.RecoveryEnvelope);
    }

    [Fact]
    public async Task MissingPlatformSecretIsReportedWithoutKeyMaterial()
    {
        PlatformSyncKeyService service = CreateService(new MemorySecretStore());

        SyncMasterKeyOpenResult result = await service.OpenMasterKeyAsync(
            SpaceId,
            1,
            CancellationToken.None);

        Assert.Equal(SyncKeyOperationStatus.NotFound, result.Status);
        Assert.Null(result.Key);
    }

    private static PlatformSyncKeyService CreateService(IPlatformSecretStore store) => new(
        store,
        new SyncRecoveryKdfParameters(
            MemoryKiB: 8 * 1024,
            Iterations: 2,
            Parallelism: 1));

    private sealed class MemorySecretStore : IPlatformSecretStore
    {
        private byte[]? _secret;

        public string? LastName { get; private set; }

        public ValueTask<PlatformSecretReadResult> ReadAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastName = name;
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
            LastName = name;
            _secret = secret.ToArray();
            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.Success));
        }

        public ValueTask<PlatformSecretWriteResult> DeleteAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastName = name;
            if (_secret is not null)
            {
                CryptographicOperations.ZeroMemory(_secret);
                _secret = null;
            }

            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.Success));
        }
    }
}
