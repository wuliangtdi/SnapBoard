namespace SnapBoard.Sync.Contracts;

public sealed record SyncRecoveryEnvelope(
    int FormatVersion,
    string Kdf,
    int MemoryKiB,
    int Iterations,
    int Parallelism,
    byte[] Salt,
    string Algorithm,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] AuthenticationTag);
