using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Infrastructure.Persistence;

public sealed partial class SqliteClipboardHistoryStore
{
    private async ValueTask<ClipboardHistorySaveResult> SaveCoreAsync(
        SqliteConnection connection,
        ClipboardCapturedItem item,
        CancellationToken cancellationToken)
    {
        AdjacentItem? adjacent = await FindAdjacentItemAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (adjacent is { IsPinned: false } &&
            string.Equals(adjacent.ContentHash, item.ContentHash.Value, StringComparison.Ordinal))
        {
            PreparedSourceApplicationIcon? sourceIcon = adjacent.SourceApplicationIconBlobHash is null
                ? await PrepareSourceApplicationIconAsync(item, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            try
            {
                await MergeAdjacentItemAsync(
                        connection,
                        adjacent.Id,
                        item,
                        sourceIcon,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ClipboardHistorySaveResult(ParseItemId(adjacent.Id), true);
            }
            catch
            {
                if (sourceIcon is not null)
                {
                    await CleanupFailedStagingAsync(connection, [sourceIcon.Blob])
                        .ConfigureAwait(false);
                }

                throw;
            }
        }

        PreparedContent prepared = await PrepareContentAsync(
                connection,
                item,
                cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            foreach (StagedBlob blob in prepared.BlobReferences)
            {
                await AddBlobReferenceAsync(
                        connection,
                        transaction,
                        blob,
                        item.CapturedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await InsertItemAsync(
                    connection,
                    transaction,
                    item,
                    prepared.Thumbnail?.Hash,
                    prepared.SourceApplicationIcon,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertRepresentationsAsync(
                    connection,
                    transaction,
                    item.Id,
                    prepared.Representations,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertFilesAsync(
                    connection,
                    transaction,
                    item.Id,
                    item.FilePaths,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertFormatsAsync(
                    connection,
                    transaction,
                    item.Id,
                    item.Formats,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertFtsAsync(
                    connection,
                    transaction,
                    item.Id,
                    item.SearchableText,
                    cancellationToken)
                .ConfigureAwait(false);
            await AppendLocalSyncEventAsync(
                    connection,
                    transaction,
                    SyncChangeKind.Upsert,
                    item.Id,
                    tags: null,
                    isPinned: null,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClipboardHistorySaveResult(item.Id, false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await CleanupFailedStagingAsync(connection, prepared.BlobReferences)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            prepared.ZeroInlineBinaryCopies();
        }
    }

    private static async ValueTask<AdjacentItem?> FindAdjacentItemAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, is_pinned, content_hash, source_application_icon_blob_hash
            FROM clipboard_items
            WHERE is_deleted = 0
            ORDER BY captured_at_utc DESC, id DESC
            LIMIT 1;
            """;
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AdjacentItem(
                reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static async ValueTask MergeAdjacentItemAsync(
        SqliteConnection connection,
        string identifier,
        ClipboardCapturedItem item,
        PreparedSourceApplicationIcon? sourceIcon,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            if (sourceIcon is not null)
            {
                await AddBlobReferenceAsync(
                        connection,
                        transaction,
                        sourceIcon.Blob,
                        item.CapturedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await using (SqliteCommand update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE clipboard_items
                    SET sequence_number = @sequence,
                        captured_at_utc = @capturedAt,
                        updated_at_utc = @updatedAt,
                        source_process_id = @sourceProcessId,
                        source_process_name = @sourceProcessName,
                        source_executable_path = @sourceExecutablePath,
                        source_application_user_model_id = @sourceApplicationUserModelId,
                        source_package_family_name = @sourcePackageFamilyName,
                        source_access_status = @sourceAccessStatus,
                        source_attribution_kind = @sourceAttributionKind,
                        source_application_icon_blob_hash = COALESCE(
                            source_application_icon_blob_hash,
                            @sourceApplicationIconBlobHash),
                        source_application_icon_format_version = CASE
                            WHEN source_application_icon_blob_hash IS NULL
                                THEN @sourceApplicationIconFormatVersion
                            ELSE source_application_icon_format_version
                        END,
                        source_application_icon_width = CASE
                            WHEN source_application_icon_blob_hash IS NULL
                                THEN @sourceApplicationIconWidth
                            ELSE source_application_icon_width
                        END,
                        source_application_icon_height = CASE
                            WHEN source_application_icon_blob_hash IS NULL
                                THEN @sourceApplicationIconHeight
                            ELSE source_application_icon_height
                        END,
                        source_application_icon_stride = CASE
                            WHEN source_application_icon_blob_hash IS NULL
                                THEN @sourceApplicationIconStride
                            ELSE source_application_icon_stride
                        END,
                        preview_text = @previewText,
                        searchable_text = @searchableText,
                        display_category = @displayCategory,
                        search_order_key = @searchOrderBase + COALESCE((
                            SELECT MAX(other.search_order_key - @searchOrderBase) + 1
                            FROM clipboard_items other
                            WHERE other.captured_at_utc = @capturedAt
                              AND other.id <> @id
                        ), 0),
                        capture_count = capture_count + 1
                    WHERE id = @id AND is_deleted = 0 AND is_pinned = 0;
                    """;
                AddItemMetadataParameters(update, item);
                AddSourceApplicationIconParameters(update, sourceIcon);
                update.Parameters.AddWithValue("@id", identifier);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException("Adjacent clipboard item changed concurrently.");
                }
            }

            await using (SqliteCommand deleteFts = connection.CreateCommand())
            {
                deleteFts.Transaction = transaction;
                deleteFts.CommandText = "DELETE FROM clipboard_items_fts WHERE item_id = @id;";
                deleteFts.Parameters.AddWithValue("@id", identifier);
                await deleteFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertFtsAsync(
                    connection,
                    transaction,
                    ParseItemId(identifier),
                    item.SearchableText,
                    cancellationToken)
                .ConfigureAwait(false);
            await AppendLocalSyncEventAsync(
                    connection,
                    transaction,
                    SyncChangeKind.Upsert,
                    ParseItemId(identifier),
                    tags: null,
                    isPinned: null,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PreparedContent> PrepareContentAsync(
        SqliteConnection connection,
        ClipboardCapturedItem item,
        CancellationToken cancellationToken)
    {
        List<PreparedRepresentation> representations = [];
        List<StagedBlob> blobReferences = [];
        try
        {
            foreach (ClipboardCapturedRepresentation representation in item.Representations)
            {
                if (representation.Text is not null)
                {
                    int byteCount = Encoding.UTF8.GetByteCount(representation.Text);
                    if (byteCount <= InlinePayloadThresholdBytes)
                    {
                        representations.Add(new PreparedRepresentation(
                            representation,
                            representation.Text,
                            null,
                            null));
                    }
                    else
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(representation.Text);
                        try
                        {
                            StagedBlob blob = await _blobStore
                                .StageAsync(bytes, representation.MediaType, cancellationToken)
                                .ConfigureAwait(false);
                            blobReferences.Add(blob);
                            representations.Add(new PreparedRepresentation(
                                representation,
                                null,
                                null,
                                blob));
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(bytes);
                        }
                    }

                    continue;
                }

                if (representation.Kind != ClipboardContentKind.Image &&
                    representation.Data.Length <= InlinePayloadThresholdBytes)
                {
                    representations.Add(new PreparedRepresentation(
                        representation,
                        null,
                        representation.Data.ToArray(),
                        null));
                }
                else
                {
                    StagedBlob blob = await _blobStore
                        .StageAsync(representation.Data, representation.MediaType, cancellationToken)
                        .ConfigureAwait(false);
                    blobReferences.Add(blob);
                    representations.Add(new PreparedRepresentation(
                        representation,
                        null,
                        null,
                        blob));
                }
            }

            StagedBlob? thumbnail = null;
            ClipboardCapturedRepresentation? image = item.Representations.FirstOrDefault(
                representation => representation.Kind == ClipboardContentKind.Image);
            if (image is not null)
            {
                ReadOnlyMemory<byte> thumbnailBytes = await SkiaThumbnailGenerator
                    .GenerateAsync(image, cancellationToken)
                    .ConfigureAwait(false);
                if (!thumbnailBytes.IsEmpty)
                {
                    try
                    {
                        thumbnail = await _blobStore
                            .StageAsync(thumbnailBytes, "image/png", cancellationToken)
                            .ConfigureAwait(false);
                        blobReferences.Add(thumbnail);
                    }
                    finally
                    {
                        ZeroMemory(thumbnailBytes);
                    }
                }
            }

            PreparedSourceApplicationIcon? sourceIcon = await PrepareSourceApplicationIconAsync(
                    item,
                    cancellationToken)
                .ConfigureAwait(false);
            if (sourceIcon is not null)
            {
                blobReferences.Add(sourceIcon.Blob);
            }

            return new PreparedContent(
                representations,
                thumbnail,
                sourceIcon,
                blobReferences);
        }
        catch
        {
            ZeroInlineBinaryCopies(representations);
            await CleanupFailedStagingAsync(connection, blobReferences).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<PreparedSourceApplicationIcon?>
        PrepareSourceApplicationIconAsync(
            ClipboardCapturedItem item,
            CancellationToken cancellationToken)
    {
        ClipboardSourceApplicationIcon? icon = item.SourceApplicationIcon;
        if (icon is null || !ClipboardSourceApplicationIconRules.IsCanonical(icon))
        {
            return null;
        }

        StagedBlob blob = await _blobStore
            .StageAsync(
                icon.BgraPixels,
                SyncProtocol.SourceApplicationIconMediaType,
                cancellationToken)
            .ConfigureAwait(false);
        return new PreparedSourceApplicationIcon(
            blob,
            SyncProtocol.SourceApplicationIconFormatVersion,
            icon.Width,
            icon.Height,
            icon.Stride);
    }

    private static async ValueTask AddBlobReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StagedBlob blob,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_blobs(
                hash, relative_path, media_type, size_bytes, ref_count, created_at_utc)
            VALUES (@hash, @relativePath, @mediaType, @sizeBytes, 1, @createdAt)
            ON CONFLICT(hash) DO UPDATE SET
                ref_count = content_blobs.ref_count + 1
            WHERE content_blobs.relative_path = excluded.relative_path
              AND content_blobs.size_bytes = excluded.size_bytes;
            """;
        command.Parameters.AddWithValue("@hash", blob.Hash);
        command.Parameters.AddWithValue("@relativePath", blob.RelativePath);
        command.Parameters.AddWithValue("@mediaType", blob.MediaType);
        command.Parameters.AddWithValue("@sizeBytes", blob.SizeBytes);
        command.Parameters.AddWithValue("@createdAt", createdAt.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Clipboard blob metadata was inconsistent.");
        }
    }

    private static async ValueTask InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardCapturedItem item,
        string? thumbnailHash,
        PreparedSourceApplicationIcon? sourceIcon,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clipboard_items(
                id,
                sequence_number,
                primary_kind,
                display_category,
                captured_at_utc,
                updated_at_utc,
                source_process_id,
                source_process_name,
                source_executable_path,
                source_application_user_model_id,
                source_package_family_name,
                source_access_status,
                source_attribution_kind,
                content_hash,
                preview_text,
                searchable_text,
                is_pinned,
                use_count,
                last_used_at_utc,
                is_deleted,
                deleted_at_utc,
                total_size_bytes,
                thumbnail_blob_hash,
                source_application_icon_blob_hash,
                source_application_icon_format_version,
                source_application_icon_width,
                source_application_icon_height,
                source_application_icon_stride,
                capture_count,
                search_order_key)
            VALUES (
                @id,
                @sequence,
                @primaryKind,
                @displayCategory,
                @capturedAt,
                @updatedAt,
                @sourceProcessId,
                @sourceProcessName,
                @sourceExecutablePath,
                @sourceApplicationUserModelId,
                @sourcePackageFamilyName,
                @sourceAccessStatus,
                @sourceAttributionKind,
                @contentHash,
                @previewText,
                @searchableText,
                0,
                0,
                NULL,
                0,
                NULL,
                @totalSizeBytes,
                @thumbnailHash,
                @sourceApplicationIconBlobHash,
                @sourceApplicationIconFormatVersion,
                @sourceApplicationIconWidth,
                @sourceApplicationIconHeight,
                @sourceApplicationIconStride,
                1,
                @searchOrderBase + COALESCE((
                    SELECT MAX(search_order_key - @searchOrderBase) + 1
                    FROM clipboard_items
                    WHERE captured_at_utc = @capturedAt
                ), 0));
            """;
        command.Parameters.AddWithValue("@id", item.Id.ToString());
        command.Parameters.AddWithValue("@primaryKind", (int)item.PrimaryKind);
        command.Parameters.AddWithValue("@contentHash", item.ContentHash.Value);
        command.Parameters.AddWithValue("@totalSizeBytes", item.TotalSizeBytes);
        command.Parameters.AddWithValue("@thumbnailHash", (object?)thumbnailHash ?? DBNull.Value);
        AddSourceApplicationIconParameters(command, sourceIcon);
        AddItemMetadataParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddSourceApplicationIconParameters(
        SqliteCommand command,
        PreparedSourceApplicationIcon? sourceIcon)
    {
        command.Parameters.AddWithValue(
            "@sourceApplicationIconBlobHash",
            (object?)sourceIcon?.Blob.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@sourceApplicationIconFormatVersion",
            sourceIcon?.FormatVersion ?? 0);
        command.Parameters.AddWithValue(
            "@sourceApplicationIconWidth",
            sourceIcon?.Width ?? 0);
        command.Parameters.AddWithValue(
            "@sourceApplicationIconHeight",
            sourceIcon?.Height ?? 0);
        command.Parameters.AddWithValue(
            "@sourceApplicationIconStride",
            sourceIcon?.Stride ?? 0);
    }

    private static void AddItemMetadataParameters(
        SqliteCommand command,
        ClipboardCapturedItem item)
    {
        command.Parameters.AddWithValue(
            "@sequence",
            item.SequenceNumber.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@displayCategory", (int)item.DisplayCategory);
        command.Parameters.AddWithValue("@capturedAt", item.CapturedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "@searchOrderBase",
            GetSearchOrderBase(item.CapturedAt));
        command.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "@sourceProcessId",
            (object?)item.SourceProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@sourceProcessName",
            (object?)item.SourceProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@sourceExecutablePath",
            (object?)item.SourceExecutablePath ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@sourceApplicationUserModelId",
            (object?)item.SourceApplicationUserModelId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@sourcePackageFamilyName",
            (object?)item.SourcePackageFamilyName ?? DBNull.Value);
        command.Parameters.AddWithValue("@sourceAccessStatus", item.SourceAccessStatus);
        command.Parameters.AddWithValue("@sourceAttributionKind", item.SourceAttributionKind);
        command.Parameters.AddWithValue("@previewText", item.PreviewText);
        command.Parameters.AddWithValue("@searchableText", item.SearchableText);
    }

    private static long GetSearchOrderBase(DateTimeOffset capturedAt) => checked(
        capturedAt.ToUnixTimeMilliseconds() * 1_000_000L);

    private static async ValueTask InsertRepresentationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        IReadOnlyList<PreparedRepresentation> representations,
        CancellationToken cancellationToken)
    {
        foreach (PreparedRepresentation representation in representations)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO clipboard_representations(
                    item_id,
                    kind,
                    media_type,
                    inline_text,
                    inline_data,
                    blob_hash,
                    size_bytes,
                    bitmap_encoding,
                    width,
                    height,
                    bits_per_pixel)
                VALUES (
                    @itemId,
                    @kind,
                    @mediaType,
                    @inlineText,
                    @inlineData,
                    @blobHash,
                    @sizeBytes,
                    @bitmapEncoding,
                    @width,
                    @height,
                    @bitsPerPixel);
                """;
            command.Parameters.AddWithValue("@itemId", itemId.ToString());
            command.Parameters.AddWithValue("@kind", (int)representation.Source.Kind);
            command.Parameters.AddWithValue("@mediaType", representation.Source.MediaType);
            command.Parameters.AddWithValue(
                "@inlineText",
                (object?)representation.InlineText ?? DBNull.Value);
            command.Parameters.Add("@inlineData", SqliteType.Blob).Value =
                (object?)representation.InlineData ?? DBNull.Value;
            command.Parameters.AddWithValue(
                "@blobHash",
                (object?)representation.Blob?.Hash ?? DBNull.Value);
            command.Parameters.AddWithValue("@sizeBytes", representation.SizeBytes);
            command.Parameters.AddWithValue(
                "@bitmapEncoding",
                representation.Source.BitmapEncoding is { } bitmapEncoding
                    ? (int)bitmapEncoding
                    : DBNull.Value);
            command.Parameters.AddWithValue("@width", representation.Source.Width);
            command.Parameters.AddWithValue("@height", representation.Source.Height);
            command.Parameters.AddWithValue("@bitsPerPixel", representation.Source.BitsPerPixel);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < filePaths.Count; index++)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO clipboard_files(item_id, ordinal, path)
                VALUES (@itemId, @ordinal, @path);
                """;
            command.Parameters.AddWithValue("@itemId", itemId.ToString());
            command.Parameters.AddWithValue("@ordinal", index);
            command.Parameters.AddWithValue("@path", filePaths[index]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertFormatsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        IReadOnlyList<ClipboardCapturedFormat> formats,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < formats.Count; index++)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO clipboard_formats(
                    item_id, ordinal, identifier, name, is_available)
                VALUES (@itemId, @ordinal, @identifier, @name, @isAvailable);
                """;
            command.Parameters.AddWithValue("@itemId", itemId.ToString());
            command.Parameters.AddWithValue("@ordinal", index);
            command.Parameters.AddWithValue("@identifier", formats[index].Identifier);
            command.Parameters.AddWithValue("@name", formats[index].Name);
            command.Parameters.AddWithValue("@isAvailable", formats[index].IsAvailable ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        string searchableText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clipboard_items_fts(rowid, item_id, searchable_text)
            SELECT search_order_key, id, @searchableText
            FROM clipboard_items
            WHERE id = @itemId;
            """;
        command.Parameters.AddWithValue("@itemId", itemId.ToString());
        command.Parameters.AddWithValue("@searchableText", searchableText);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Clipboard FTS row could not be aligned.");
        }
    }

    private async ValueTask CleanupFailedStagingAsync(
        SqliteConnection connection,
        IReadOnlyList<StagedBlob> stagedBlobs)
    {
        foreach (StagedBlob blob in stagedBlobs
            .Where(blob => blob.CreatedNew)
            .DistinctBy(blob => blob.Hash, StringComparer.Ordinal))
        {
            try
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM content_blobs WHERE hash = @hash);";
                command.Parameters.AddWithValue("@hash", blob.Hash);
                int exists = Convert.ToInt32(
                    await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (exists == 0)
                {
                    await _blobStore.DeleteAsync(blob.RelativePath).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // 启动孤儿清理会再次处理；不能因清理失败覆盖原始事务异常。
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (SqliteException)
            {
                // 无法证明文件未被引用时必须保留，不能让回滚清理覆盖原始异常。
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    private static void ZeroInlineBinaryCopies(
        IEnumerable<PreparedRepresentation> representations)
    {
        foreach (PreparedRepresentation representation in representations)
        {
            if (representation.InlineData is not null)
            {
                CryptographicOperations.ZeroMemory(representation.InlineData);
            }
        }
    }

    private static async ValueTask<bool> SetPinnedCoreAsync(
        SqliteConnection connection,
        ClipboardItemId itemId,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE clipboard_items
                SET is_pinned = @isPinned,
                    updated_at_utc = @updatedAt
                WHERE id = @id AND is_deleted = 0;
                """;
            command.Parameters.AddWithValue("@isPinned", isPinned ? 1 : 0);
            command.Parameters.AddWithValue(
                "@updatedAt",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@id", itemId.ToString());
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await AppendLocalSyncEventAsync(
                    connection,
                    transaction,
                    SyncChangeKind.SetPinned,
                    itemId,
                    tags: null,
                    isPinned,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<bool> SetTagsCoreAsync(
        SqliteConnection connection,
        ClipboardItemId itemId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        string identifier = itemId.ToString();
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await using SqliteCommand exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM clipboard_items WHERE id = @id AND is_deleted = 0);";
            exists.Parameters.AddWithValue("@id", identifier);
            if (Convert.ToInt32(
                    await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using (SqliteCommand clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM clipboard_item_tags WHERE item_id = @id;";
                clear.Parameters.AddWithValue("@id", identifier);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (string tag in tags)
            {
                long tagId;
                await using (SqliteCommand upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText = """
                        INSERT INTO clipboard_tags(name, normalized_name, created_at_utc)
                        VALUES (@name, @normalizedName, @createdAt)
                        ON CONFLICT(normalized_name) DO UPDATE SET name = excluded.name
                        RETURNING id;
                        """;
                    upsert.Parameters.AddWithValue("@name", tag);
                    upsert.Parameters.AddWithValue("@normalizedName", tag.ToUpperInvariant());
                    upsert.Parameters.AddWithValue(
                        "@createdAt",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    tagId = Convert.ToInt64(
                        await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture);
                }

                await using SqliteCommand assign = connection.CreateCommand();
                assign.Transaction = transaction;
                assign.CommandText = """
                    INSERT INTO clipboard_item_tags(item_id, tag_id)
                    VALUES (@itemId, @tagId);
                    """;
                assign.Parameters.AddWithValue("@itemId", identifier);
                assign.Parameters.AddWithValue("@tagId", tagId);
                await assign.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = transaction;
                cleanup.CommandText = """
                    DELETE FROM clipboard_tags
                    WHERE NOT EXISTS(
                        SELECT 1 FROM clipboard_item_tags it WHERE it.tag_id = clipboard_tags.id
                    );
                    """;
                await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendLocalSyncEventAsync(
                    connection,
                    transaction,
                    SyncChangeKind.SetTags,
                    itemId,
                    tags.ToArray(),
                    isPinned: null,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<bool> RecordUseCoreAsync(
        SqliteConnection connection,
        ClipboardItemId itemId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE clipboard_items
            SET use_count = use_count + 1,
                last_used_at_utc = @usedAt,
                updated_at_utc = @usedAt
            WHERE id = @id AND is_deleted = 0;
            """;
        command.Parameters.AddWithValue("@usedAt", usedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@id", itemId.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static async ValueTask SetSettingCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings(key, value, version, updated_at_utc)
            VALUES (@key, @value, 1, @updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                version = settings.version + 1,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask BulkImportCoreAsync(
        SqliteConnection connection,
        IReadOnlyList<ClipboardCapturedItem> items,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            foreach (ClipboardCapturedItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Representations.Any(representation =>
                    representation.Kind == ClipboardContentKind.Image ||
                    representation.SizeBytes > InlinePayloadThresholdBytes))
                {
                    throw new InvalidOperationException(
                        "Bulk import accepts inline non-image representations only.");
                }

                PreparedRepresentation[] representations = item.Representations
                    .Select(representation => new PreparedRepresentation(
                        representation,
                        representation.Text,
                        representation.Text is null ? representation.Data.ToArray() : null,
                        null))
                    .ToArray();
                try
                {
                    await InsertItemAsync(
                            connection,
                            transaction,
                            item,
                            thumbnailHash: null,
                            sourceIcon: null,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await InsertRepresentationsAsync(
                            connection,
                            transaction,
                            item.Id,
                            representations,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await InsertFtsAsync(
                            connection,
                            transaction,
                            item.Id,
                            item.SearchableText,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    foreach (PreparedRepresentation representation in representations)
                    {
                        if (representation.InlineData is not null)
                        {
                            CryptographicOperations.ZeroMemory(representation.InlineData);
                        }
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void ZeroMemory(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) &&
            segment.Array is not null)
        {
            CryptographicOperations.ZeroMemory(
                segment.Array.AsSpan(segment.Offset, segment.Count));
        }
    }

    private sealed record AdjacentItem(
        string Id,
        bool IsPinned,
        string ContentHash,
        string? SourceApplicationIconBlobHash);

    private sealed record PreparedSourceApplicationIcon(
        StagedBlob Blob,
        int FormatVersion,
        int Width,
        int Height,
        int Stride);

    private sealed record PreparedRepresentation(
        ClipboardCapturedRepresentation Source,
        string? InlineText,
        byte[]? InlineData,
        StagedBlob? Blob,
        long? SizeBytesOverride = null)
    {
        public long SizeBytes => SizeBytesOverride ?? Source.SizeBytes;
    }

    private sealed class PreparedContent(
        IReadOnlyList<PreparedRepresentation> representations,
        StagedBlob? thumbnail,
        PreparedSourceApplicationIcon? sourceApplicationIcon,
        IReadOnlyList<StagedBlob> blobReferences)
    {
        public IReadOnlyList<PreparedRepresentation> Representations { get; } = representations;

        public StagedBlob? Thumbnail { get; } = thumbnail;

        public PreparedSourceApplicationIcon? SourceApplicationIcon { get; } =
            sourceApplicationIcon;

        public IReadOnlyList<StagedBlob> BlobReferences { get; } = blobReferences;

        public void ZeroInlineBinaryCopies()
            => SqliteClipboardHistoryStore.ZeroInlineBinaryCopies(Representations);
    }
}
