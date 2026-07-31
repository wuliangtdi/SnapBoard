namespace SnapBoard.Sync.Contracts;

public static class SyncProtocol
{
    public const int CurrentVersion = 1;
    public const int EncryptionEnvelopeVersion = 1;
    public const int MasterKeySize = 32;
    public const int NonceSize = 12;
    public const int AuthenticationTagSize = 16;
    public const int MaximumEventPlaintextBytes = 8 * 1024 * 1024;
    public const int MaximumBlobPlaintextBytes = 64 * 1024 * 1024;
    public const int MaximumEncryptedEnvelopeBytes = 90 * 1024 * 1024;
    public const int MaximumTagsPerItem = 64;
    public const int MaximumRepresentationsPerItem = 16;
    public const int SourceApplicationIconFormatVersion = 1;
    public const int SourceApplicationIconWidth = 32;
    public const int SourceApplicationIconHeight = 32;
    public const int SourceApplicationIconStride = SourceApplicationIconWidth * 4;
    public const int SourceApplicationIconSizeBytes =
        SourceApplicationIconStride * SourceApplicationIconHeight;
    public const string ProductDirectoryName = "SnapBoard";
    public const string VersionDirectoryName = "v1";
    public const string EncryptionAlgorithm = "A256GCM";
    public const string FileReferencePreview = "File reference (source device only)";
    public const string SourceApplicationIconMediaType =
        "application/vnd.snapboard.source-icon-bgra32";
}
