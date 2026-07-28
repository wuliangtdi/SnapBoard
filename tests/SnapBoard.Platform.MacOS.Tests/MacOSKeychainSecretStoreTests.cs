using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using SnapBoard.Application.Sync;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Platform.MacOS.Security;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSKeychainSecretStoreTests
{
    [MacOSFact]
    public async Task NativeKeychainRoundTripsAndDeletesTemporarySecret()
    {
        string name = $"integration-{Guid.NewGuid():N}";
        byte[] expected = RandomNumberGenerator.GetBytes(32);
        byte[] replacement = RandomNumberGenerator.GetBytes(32);
        MacOSKeychainSecretStore store = new();

        try
        {
            PlatformSecretWriteResult write =
                await store.WriteAsync(name, expected, CancellationToken.None);
            PlatformSecretReadResult read =
                await store.ReadAsync(name, CancellationToken.None);
            PlatformSecretWriteResult overwrite =
                await store.WriteAsync(name, replacement, CancellationToken.None);
            PlatformSecretReadResult overwritten =
                await store.ReadAsync(name, CancellationToken.None);
            PlatformSecretWriteResult delete =
                await store.DeleteAsync(name, CancellationToken.None);
            PlatformSecretReadResult missing =
                await store.ReadAsync(name, CancellationToken.None);

            Assert.Equal(PlatformSecretStoreStatus.Success, write.Status);
            Assert.Equal(PlatformSecretStoreStatus.Success, read.Status);
            Assert.Equal(expected, read.Secret.ToArray());
            Assert.Equal(PlatformSecretStoreStatus.Success, overwrite.Status);
            Assert.Equal(PlatformSecretStoreStatus.Success, overwritten.Status);
            Assert.Equal(replacement, overwritten.Secret.ToArray());
            Assert.Equal(PlatformSecretStoreStatus.Success, delete.Status);
            Assert.Equal(PlatformSecretStoreStatus.NotFound, missing.Status);
        }
        finally
        {
            await store.DeleteAsync(name, CancellationToken.None);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(replacement);
        }
    }

    [MacOSFact]
    public async Task NativeKeychainBacksMasterKeyAndCompleteCredentialBundle()
    {
        Guid spaceId = Guid.NewGuid();
        string keyName = $"sync/master/{spaceId:N}/v1";
        string credentialName = $"sync/webdav/{spaceId:N}";
        byte[] recoveryCode = "native-keychain-recovery-code"u8.ToArray();
        byte[] password = "native-keychain-app-password"u8.ToArray();
        byte[] replacementPassword = "replacement-app-password"u8.ToArray();
        MacOSKeychainSecretStore store = new();
        PlatformSyncKeyService keyService = new(store);
        PlatformSyncCredentialService credentialService = new(store);
        byte[]? recoveryEnvelope = null;
        try
        {
            SyncSpaceKeyCreationResult created = await keyService.CreateSpaceKeyAsync(
                spaceId,
                1,
                recoveryCode,
                CancellationToken.None);
            Assert.Equal(SyncKeyOperationStatus.Success, created.Status);
            recoveryEnvelope = Assert.IsType<byte[]>(created.RecoveryEnvelope);

            PlatformSecretReadResult rawKey = await store.ReadAsync(
                keyName,
                CancellationToken.None);
            Assert.Equal(PlatformSecretStoreStatus.Success, rawKey.Status);
            Assert.Equal(32, rawKey.Secret.Length);

            SyncRemoteConfiguration initial = new(
                new Uri("https://dav.example.test/user/"),
                "SnapBoard/v1",
                "mac-user",
                new string('a', 64));
            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await credentialService.StoreAsync(
                    spaceId,
                    initial,
                    password,
                    CancellationToken.None));

            SyncRemoteConfiguration replacement = new(
                new Uri("http://127.0.0.1:8080/"),
                "SnapBoard/dev",
                "local-user",
                certificateSha256Pin: null,
                allowInsecureLoopback: true);
            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await credentialService.StoreAsync(
                    spaceId,
                    replacement,
                    replacementPassword,
                    CancellationToken.None));

            SyncCredentialOpenResult opened = await credentialService.OpenAsync(
                spaceId,
                CancellationToken.None);
            Assert.Equal(SyncCredentialOperationStatus.Success, opened.Status);
            using SyncCredentialLease lease = Assert.IsType<SyncCredentialLease>(opened.Credential);
            Assert.Equal(replacement.Endpoint, lease.RemoteConfiguration.Endpoint);
            Assert.Equal(replacement.RemoteRoot, lease.RemoteConfiguration.RemoteRoot);
            Assert.Equal(replacement.Username, lease.RemoteConfiguration.Username);
            Assert.True(lease.RemoteConfiguration.AllowInsecureLoopback);
            Assert.Equal(replacementPassword, lease.Password.ToArray());

            Assert.Equal(
                SyncCredentialOperationStatus.Success,
                await credentialService.DeleteAsync(spaceId, CancellationToken.None));
            Assert.Equal(
                SyncKeyOperationStatus.Success,
                await keyService.DeleteSpaceKeyAsync(spaceId, 1, CancellationToken.None));
            Assert.Equal(
                PlatformSecretStoreStatus.NotFound,
                (await store.ReadAsync(keyName, CancellationToken.None)).Status);
            Assert.Equal(
                PlatformSecretStoreStatus.NotFound,
                (await store.ReadAsync(credentialName, CancellationToken.None)).Status);

            if (MemoryMarshal.TryGetArray(rawKey.Secret, out ArraySegment<byte> segment) &&
                segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(
                    segment.Array.AsSpan(segment.Offset, segment.Count));
            }
        }
        finally
        {
            await store.DeleteAsync(keyName, CancellationToken.None);
            await store.DeleteAsync(credentialName, CancellationToken.None);
            CryptographicOperations.ZeroMemory(recoveryCode);
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(replacementPassword);
            if (recoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
            }
        }
    }

    [Fact]
    public async Task ReadCopiesSecretAndReleasesItem()
    {
        FakeKeychainNative native = new()
        {
            FindStatus = 0,
            FindSecret = [1, 2, 3],
            FindItem = 11,
        };
        MacOSKeychainSecretStore store = new(native);

        PlatformSecretReadResult result =
            await store.ReadAsync("sync-key", CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.Success, result.Status);
        Assert.Equal([1, 2, 3], result.Secret.ToArray());
        Assert.Equal([11], native.ReleasedItems);
    }

    [Fact]
    public async Task ExistingSecretIsZeroedAfterModifyAndItemIsReleased()
    {
        byte[] existing = [9, 8, 7];
        FakeKeychainNative native = new()
        {
            FindStatus = 0,
            FindSecret = existing,
            FindItem = 12,
            ModifyStatus = 0,
        };
        MacOSKeychainSecretStore store = new(native);

        PlatformSecretWriteResult result =
            await store.WriteAsync("sync-key", new byte[] { 4, 5 }, CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.Success, result.Status);
        Assert.All(existing, value => Assert.Equal(0, value));
        Assert.Equal([12], native.ReleasedItems);
        Assert.Equal([4, 5], native.ModifiedSecret);
    }

    [Fact]
    public async Task MissingSecretIsAddedAndReturnedItemIsReleased()
    {
        FakeKeychainNative native = new()
        {
            FindStatus = -25300,
            AddStatus = 0,
            AddItem = 21,
        };
        MacOSKeychainSecretStore store = new(native);

        PlatformSecretWriteResult result =
            await store.WriteAsync("device-key", new byte[] { 6, 7 }, CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.Success, result.Status);
        Assert.Equal([21], native.ReleasedItems);
        Assert.Equal([6, 7], native.AddedSecret);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    public async Task InvalidNamesNeverReachKeychain(string name)
    {
        FakeKeychainNative native = new();
        MacOSKeychainSecretStore store = new(native);

        PlatformSecretReadResult result = await store.ReadAsync(name, CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.InvalidName, result.Status);
        Assert.Equal(0, native.FindCount);
    }

    [Theory]
    [InlineData(-25293)]
    [InlineData(-25308)]
    [InlineData(-128)]
    public async Task UserOrKeychainDenialReturnsStructuredAccessDenied(int nativeStatus)
    {
        FakeKeychainNative native = new() { FindStatus = nativeStatus };
        MacOSKeychainSecretStore store = new(native);

        PlatformSecretReadResult result = await store.ReadAsync(
            "sync-key",
            CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.AccessDenied, result.Status);
        Assert.Equal(nativeStatus, result.NativeErrorCode);
        Assert.True(result.Secret.IsEmpty);
    }

    private sealed class FakeKeychainNative : IMacOSKeychainNative
    {
        public int FindStatus { get; set; } = -25300;

        public byte[]? FindSecret { get; set; }

        public nint FindItem { get; set; }

        public int AddStatus { get; set; }

        public nint AddItem { get; set; }

        public int ModifyStatus { get; set; }

        public int DeleteStatus { get; set; }

        public int FindCount { get; private set; }

        public byte[]? AddedSecret { get; private set; }

        public byte[]? ModifiedSecret { get; private set; }

        public List<nint> ReleasedItems { get; } = [];

        public int Find(string service, string account, out byte[]? secret, out nint item)
        {
            FindCount++;
            secret = FindSecret;
            item = FindItem;
            return FindStatus;
        }

        public int Add(string service, string account, ReadOnlySpan<byte> secret, out nint item)
        {
            AddedSecret = secret.ToArray();
            item = AddItem;
            return AddStatus;
        }

        public int Modify(nint item, ReadOnlySpan<byte> secret)
        {
            ModifiedSecret = secret.ToArray();
            return ModifyStatus;
        }

        public int Delete(nint item) => DeleteStatus;

        public void ReleaseItem(nint item)
        {
            if (item != 0)
            {
                ReleasedItems.Add(item);
            }
        }
    }
}
