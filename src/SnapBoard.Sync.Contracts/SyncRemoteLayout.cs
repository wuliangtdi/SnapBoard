using System.Globalization;

namespace SnapBoard.Sync.Contracts;

public static class SyncRemoteLayout
{
    public static string GetSpaceRoot(Guid spaceId) =>
        $"{SyncProtocol.ProductDirectoryName}/{SyncProtocol.VersionDirectoryName}/spaces/{FormatId(spaceId)}";

    public static string GetMetadataPath(Guid spaceId) =>
        $"{GetSpaceRoot(spaceId)}/metadata.enc";

    public static string GetDeviceRoot(Guid spaceId, Guid deviceId) =>
        $"{GetSpaceRoot(spaceId)}/devices/{FormatId(deviceId)}";

    public static string GetEventsCollection(Guid spaceId, Guid deviceId) =>
        $"{GetDeviceRoot(spaceId, deviceId)}/events";

    public static string GetEventPath(
        Guid spaceId,
        Guid deviceId,
        long sequence,
        Guid eventId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        return $"{GetEventsCollection(spaceId, deviceId)}/{sequence.ToString("D20", CultureInfo.InvariantCulture)}-{FormatId(eventId)}.enc";
    }

    public static string GetCheckpointsCollection(Guid spaceId, Guid deviceId) =>
        $"{GetDeviceRoot(spaceId, deviceId)}/checkpoints";

    public static string GetProviderMigrationsCollection(Guid spaceId) =>
        $"{GetSpaceRoot(spaceId)}/migrations";

    public static string GetProviderMigrationRoot(Guid spaceId, Guid planId) =>
        $"{GetProviderMigrationsCollection(spaceId)}/{FormatId(planId)}";

    public static string GetProviderMigrationIntentPath(Guid spaceId, Guid planId) =>
        $"{GetProviderMigrationRoot(spaceId, planId)}/intent.enc";

    public static string GetProviderMigrationDeviceMarkerPath(
        Guid spaceId,
        Guid planId,
        SyncProviderMigrationMarkerKind kind,
        Guid deviceId)
    {
        string collection = kind switch
        {
            SyncProviderMigrationMarkerKind.Ready => "ready",
            SyncProviderMigrationMarkerKind.Committed => "committed",
            SyncProviderMigrationMarkerKind.RolledBack => "rolled-back",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return $"{GetProviderMigrationRoot(spaceId, planId)}/{collection}/{FormatId(deviceId)}.enc";
    }

    public static string GetProviderMigrationDecisionPath(
        Guid spaceId,
        Guid planId,
        SyncProviderMigrationMarkerKind kind)
    {
        string objectName = kind switch
        {
            SyncProviderMigrationMarkerKind.Freeze => "freeze.enc",
            SyncProviderMigrationMarkerKind.Commit => "commit.enc",
            SyncProviderMigrationMarkerKind.Rollback => "rollback.enc",
            SyncProviderMigrationMarkerKind.Completed => "completed.enc",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return $"{GetProviderMigrationRoot(spaceId, planId)}/{objectName}";
    }

    public static string GetBlobPath(Guid spaceId, string keyedBlobId)
    {
        if (!IsLowerHex(keyedBlobId, 64))
        {
            throw new ArgumentException("The keyed Blob identifier is invalid.", nameof(keyedBlobId));
        }

        return $"{GetSpaceRoot(spaceId)}/blobs/{keyedBlobId}.enc";
    }

    public static bool TryParseEventObjectName(
        string? objectName,
        out long sequence,
        out Guid eventId)
    {
        sequence = 0;
        eventId = Guid.Empty;
        if (objectName is null || objectName.Length != 57 || objectName[20] != '-' ||
            !objectName.EndsWith(".enc", StringComparison.Ordinal) ||
            !ContainsOnlyAsciiDigits(objectName.AsSpan(0, 20)) ||
            !long.TryParse(
                objectName.AsSpan(0, 20),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sequence) ||
            sequence <= 0 ||
            !Guid.TryParseExact(objectName.AsSpan(21, 32), "N", out eventId))
        {
            sequence = 0;
            eventId = Guid.Empty;
            return false;
        }

        return FormatId(eventId).AsSpan().Equals(
            objectName.AsSpan(21, 32),
            StringComparison.Ordinal);
    }

    public static bool TryParseBlobObjectName(
        string? objectName,
        out string keyedBlobId)
    {
        keyedBlobId = string.Empty;
        if (objectName is null || objectName.Length != 68 ||
            !objectName.EndsWith(".enc", StringComparison.Ordinal) ||
            !IsLowerHex(objectName[..64], 64))
        {
            return false;
        }

        keyedBlobId = objectName[..64];
        return true;
    }

    public static string FormatId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        return value.ToString("N");
    }

    public static bool IsLowerHex(string? value, int expectedLength)
    {
        if (value is null || value.Length != expectedLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsOnlyAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
