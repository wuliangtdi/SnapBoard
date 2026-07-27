using System.Runtime.Versioning;
using System.Security.Cryptography;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Platform.Windows.Interop;
using SnapBoard.Platform.Windows.Security;

namespace SnapBoard.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSecretStoreTests
{
    [WindowsFact]
    public async Task NativeCredentialManagerRoundTripsOverwritesAndDeletesTemporarySecret()
    {
        string name = $"integration-{Guid.NewGuid():N}";
        byte[] first = RandomNumberGenerator.GetBytes(32);
        byte[] second = RandomNumberGenerator.GetBytes(48);
        WindowsCredentialSecretStore store = new();

        try
        {
            PlatformSecretWriteResult create =
                await store.WriteAsync(name, first, CancellationToken.None);
            PlatformSecretReadResult initial =
                await store.ReadAsync(name, CancellationToken.None);
            PlatformSecretWriteResult overwrite =
                await store.WriteAsync(name, second, CancellationToken.None);
            PlatformSecretReadResult updated =
                await store.ReadAsync(name, CancellationToken.None);
            PlatformSecretWriteResult delete =
                await store.DeleteAsync(name, CancellationToken.None);
            PlatformSecretReadResult missing =
                await store.ReadAsync(name, CancellationToken.None);

            Assert.Equal(PlatformSecretStoreStatus.Success, create.Status);
            Assert.Equal(first, initial.Secret.ToArray());
            Assert.Equal(PlatformSecretStoreStatus.Success, overwrite.Status);
            Assert.Equal(second, updated.Secret.ToArray());
            Assert.Equal(PlatformSecretStoreStatus.Success, delete.Status);
            Assert.Equal(PlatformSecretStoreStatus.NotFound, missing.Status);
        }
        finally
        {
            await store.DeleteAsync(name, CancellationToken.None);
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    [Fact]
    public async Task NewAndExistingSecretsUseTheSamePrefixedTarget()
    {
        FakeCredentialNative native = new();
        WindowsCredentialSecretStore store = new(native);

        await store.WriteAsync("master-key", new byte[] { 1, 2 }, CancellationToken.None);
        await store.WriteAsync("master-key", new byte[] { 3, 4 }, CancellationToken.None);
        PlatformSecretReadResult result =
            await store.ReadAsync("master-key", CancellationToken.None);

        Assert.Equal([3, 4], result.Secret.ToArray());
        Assert.All(
            native.Targets,
            target => Assert.Equal("com.wuliangtdi.snapboard/master-key", target));
    }

    [Fact]
    public async Task DeleteAndMissingStatesAreDistinct()
    {
        FakeCredentialNative native = new();
        WindowsCredentialSecretStore store = new(native);

        await store.WriteAsync("device-key", new byte[] { 5 }, CancellationToken.None);
        PlatformSecretWriteResult deleted =
            await store.DeleteAsync("device-key", CancellationToken.None);
        PlatformSecretWriteResult missing =
            await store.DeleteAsync("device-key", CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.Success, deleted.Status);
        Assert.Equal(PlatformSecretStoreStatus.NotFound, missing.Status);
    }

    [Fact]
    public async Task AccessDeniedIsReportedWithoutReturningSecret()
    {
        FakeCredentialNative native = new()
        {
            ForcedReadError = WindowsNativeConstants.ErrorAccessDenied,
            ForcedWriteError = WindowsNativeConstants.ErrorCancelled,
            ForcedDeleteError = WindowsNativeConstants.ErrorNoSuchLogonSession,
        };
        WindowsCredentialSecretStore store = new(native);

        PlatformSecretReadResult read =
            await store.ReadAsync("sync-key", CancellationToken.None);
        PlatformSecretWriteResult write =
            await store.WriteAsync("sync-key", new byte[] { 1 }, CancellationToken.None);
        PlatformSecretWriteResult delete =
            await store.DeleteAsync("sync-key", CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.AccessDenied, read.Status);
        Assert.True(read.Secret.IsEmpty);
        Assert.Equal(PlatformSecretStoreStatus.AccessDenied, write.Status);
        Assert.Equal(PlatformSecretStoreStatus.AccessDenied, delete.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    public async Task InvalidNamesNeverReachCredentialManager(string name)
    {
        FakeCredentialNative native = new();
        WindowsCredentialSecretStore store = new(native);

        PlatformSecretReadResult result =
            await store.ReadAsync(name, CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.InvalidName, result.Status);
        Assert.Empty(native.Targets);
    }

    [Fact]
    public async Task OversizedSecretNeverReachesCredentialManager()
    {
        FakeCredentialNative native = new();
        WindowsCredentialSecretStore store = new(native);
        byte[] secret = new byte[WindowsNativeConstants.MaximumCredentialBlobSize + 1];

        PlatformSecretWriteResult result =
            await store.WriteAsync("too-large", secret, CancellationToken.None);

        Assert.Equal(PlatformSecretStoreStatus.InvalidName, result.Status);
        Assert.Empty(native.Targets);
    }

    private sealed class FakeCredentialNative : IWindowsCredentialNative
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public int ForcedReadError { get; init; }

        public int ForcedWriteError { get; init; }

        public int ForcedDeleteError { get; init; }

        public List<string> Targets { get; } = [];

        public int Read(string targetName, out byte[]? secret)
        {
            Targets.Add(targetName);
            if (ForcedReadError != 0)
            {
                secret = null;
                return ForcedReadError;
            }

            if (!_secrets.TryGetValue(targetName, out byte[]? stored))
            {
                secret = null;
                return WindowsNativeConstants.ErrorNotFound;
            }

            secret = stored.ToArray();
            return 0;
        }

        public int Write(string targetName, ReadOnlySpan<byte> secret)
        {
            Targets.Add(targetName);
            if (ForcedWriteError != 0)
            {
                return ForcedWriteError;
            }

            _secrets[targetName] = secret.ToArray();
            return 0;
        }

        public int Delete(string targetName)
        {
            Targets.Add(targetName);
            if (ForcedDeleteError != 0)
            {
                return ForcedDeleteError;
            }

            return _secrets.Remove(targetName)
                ? 0
                : WindowsNativeConstants.ErrorNotFound;
        }
    }
}
