using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Security;

internal interface IMacOSKeychainNative
{
    int Find(string service, string account, out byte[]? secret, out nint item);

    int Add(string service, string account, ReadOnlySpan<byte> secret, out nint item);

    int Modify(nint item, ReadOnlySpan<byte> secret);

    int Delete(nint item);

    void ReleaseItem(nint item);
}

internal sealed class MacOSKeychainNative : IMacOSKeychainNative
{
    public unsafe int Find(string service, string account, out byte[]? secret, out nint item)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        nint passwordData = 0;
        try
        {
            fixed (byte* servicePointer = serviceBytes)
            fixed (byte* accountPointer = accountBytes)
            {
                int status = MacOSNativeMethods.SecKeychainFindGenericPassword(
                    0,
                    checked((uint)serviceBytes.Length),
                    servicePointer,
                    checked((uint)accountBytes.Length),
                    accountPointer,
                    out uint passwordLength,
                    out passwordData,
                    out item);
                if (status != 0)
                {
                    secret = null;
                    return status;
                }

                if (passwordLength > int.MaxValue || (passwordLength != 0 && passwordData == 0))
                {
                    secret = null;
                    return -50;
                }

                secret = new byte[(int)passwordLength];
                if (secret.Length != 0)
                {
                    Marshal.Copy(passwordData, secret, 0, secret.Length);
                }

                return 0;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serviceBytes);
            CryptographicOperations.ZeroMemory(accountBytes);
            if (passwordData != 0 &&
                MacOSNativeMethods.SecKeychainItemFreeContent(0, passwordData) != 0)
            {
                // Security.framework 拥有返回缓冲；释放失败时不能尝试由托管堆释放该指针。
            }
        }
    }

    public unsafe int Add(
        string service,
        string account,
        ReadOnlySpan<byte> secret,
        out nint item)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        byte[] secretBytes = secret.ToArray();
        try
        {
            fixed (byte* servicePointer = serviceBytes)
            fixed (byte* accountPointer = accountBytes)
            fixed (byte* secretPointer = secretBytes)
            {
                return MacOSNativeMethods.SecKeychainAddGenericPassword(
                    0,
                    checked((uint)serviceBytes.Length),
                    servicePointer,
                    checked((uint)accountBytes.Length),
                    accountPointer,
                    checked((uint)secretBytes.Length),
                    secretPointer,
                    out item);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serviceBytes);
            CryptographicOperations.ZeroMemory(accountBytes);
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public unsafe int Modify(nint item, ReadOnlySpan<byte> secret)
    {
        byte[] secretBytes = secret.ToArray();
        try
        {
            fixed (byte* secretPointer = secretBytes)
            {
                return MacOSNativeMethods.SecKeychainItemModifyAttributesAndData(
                    item,
                    0,
                    checked((uint)secretBytes.Length),
                    secretPointer);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public int Delete(nint item) => MacOSNativeMethods.SecKeychainItemDelete(item);

    public void ReleaseItem(nint item)
    {
        if (item != 0)
        {
            MacOSNativeMethods.CFRelease(item);
        }
    }
}
