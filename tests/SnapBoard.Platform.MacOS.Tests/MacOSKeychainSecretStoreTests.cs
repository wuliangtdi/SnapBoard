using System.Runtime.Versioning;
using System.Security.Cryptography;
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
        MacOSKeychainSecretStore store = new();

        try
        {
            PlatformSecretWriteResult write =
                await store.WriteAsync(name, expected, CancellationToken.None);
            PlatformSecretReadResult read =
                await store.ReadAsync(name, CancellationToken.None);
            PlatformSecretWriteResult delete =
                await store.DeleteAsync(name, CancellationToken.None);
            PlatformSecretReadResult missing =
                await store.ReadAsync(name, CancellationToken.None);

            Assert.Equal(PlatformSecretStoreStatus.Success, write.Status);
            Assert.Equal(PlatformSecretStoreStatus.Success, read.Status);
            Assert.Equal(expected, read.Secret.ToArray());
            Assert.Equal(PlatformSecretStoreStatus.Success, delete.Status);
            Assert.Equal(PlatformSecretStoreStatus.NotFound, missing.Status);
        }
        finally
        {
            await store.DeleteAsync(name, CancellationToken.None);
            CryptographicOperations.ZeroMemory(expected);
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
