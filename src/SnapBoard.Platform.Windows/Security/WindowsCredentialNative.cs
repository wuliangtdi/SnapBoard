using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Security;

internal interface IWindowsCredentialNative
{
    int Read(string targetName, out byte[]? secret);

    int Write(string targetName, ReadOnlySpan<byte> secret);

    int Delete(string targetName);
}

internal sealed class WindowsCredentialNative : IWindowsCredentialNative
{
    private const string UserName = "SnapBoard";

    public unsafe int Read(string targetName, out byte[]? secret)
    {
        nint credentialPointer = 0;
        nint plaintextPointer = 0;
        int plaintextLength = 0;
        try
        {
            if (!WindowsNativeMethods.CredentialRead(
                    targetName,
                    WindowsNativeConstants.CredentialTypeGeneric,
                    0,
                    out credentialPointer))
            {
                secret = null;
                return Marshal.GetLastPInvokeError();
            }

            if (credentialPointer == 0)
            {
                secret = null;
                return 87;
            }

            NativeCredential credential = *(NativeCredential*)credentialPointer;
            if (credential.CredentialBlobSize > WindowsNativeConstants.MaximumCredentialBlobSize ||
                (credential.CredentialBlobSize != 0 && credential.CredentialBlob == 0))
            {
                secret = null;
                return 87;
            }

            plaintextPointer = credential.CredentialBlob;
            plaintextLength = checked((int)credential.CredentialBlobSize);
            secret = new byte[plaintextLength];
            if (secret.Length != 0)
            {
                Marshal.Copy(plaintextPointer, secret, 0, secret.Length);
            }

            return 0;
        }
        finally
        {
            if (plaintextPointer != 0 && plaintextLength > 0)
            {
                // CredRead 返回的结构包含临时明文；复制给调用方后先原位清零，再交还系统释放。
                CryptographicOperations.ZeroMemory(
                    new Span<byte>((void*)plaintextPointer, plaintextLength));
            }

            if (credentialPointer != 0)
            {
                // Credential Manager 拥有整块结构，只能用 CredFree 成对释放。
                WindowsNativeMethods.CredentialFree(credentialPointer);
            }
        }
    }

    public unsafe int Write(string targetName, ReadOnlySpan<byte> secret)
    {
        byte[] secretCopy = secret.ToArray();
        try
        {
            fixed (char* targetPointer = targetName)
            fixed (char* userPointer = UserName)
            fixed (byte* secretPointer = secretCopy)
            {
                NativeCredential credential = new()
                {
                    Type = WindowsNativeConstants.CredentialTypeGeneric,
                    TargetName = (nint)targetPointer,
                    CredentialBlobSize = checked((uint)secretCopy.Length),
                    CredentialBlob = secretCopy.Length == 0 ? 0 : (nint)secretPointer,
                    Persist = WindowsNativeConstants.CredentialPersistLocalMachine,
                    UserName = (nint)userPointer,
                };

                return WindowsNativeMethods.CredentialWrite(&credential, 0)
                    ? 0
                    : Marshal.GetLastPInvokeError();
            }
        }
        finally
        {
            // 调用方缓冲不归平台层所有；仅清零为 P/Invoke 创建的临时明文副本。
            CryptographicOperations.ZeroMemory(secretCopy);
        }
    }

    public int Delete(string targetName) => WindowsNativeMethods.CredentialDelete(
        targetName,
        WindowsNativeConstants.CredentialTypeGeneric,
        0)
        ? 0
        : Marshal.GetLastPInvokeError();
}
