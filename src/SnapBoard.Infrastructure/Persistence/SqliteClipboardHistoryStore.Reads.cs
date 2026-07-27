using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Infrastructure.Persistence;

public sealed partial class SqliteClipboardHistoryStore
{
    private const string SummaryProjection = """
        i.id,
        i.primary_kind,
        i.display_category,
        i.captured_at_utc,
        COALESCE(NULLIF(i.source_process_name, ''), '未知来源'),
        i.preview_text,
        i.is_pinned,
        i.use_count,
        i.last_used_at_utc,
        i.total_size_bytes,
        CASE WHEN i.thumbnail_blob_hash IS NULL THEN 0 ELSE 1 END,
        COALESCE((
            SELECT group_concat(tag_name, char(31))
            FROM (
                SELECT t.name AS tag_name
                FROM clipboard_item_tags it
                JOIN clipboard_tags t ON t.id = it.tag_id
                WHERE it.item_id = i.id
                ORDER BY t.normalized_name
            )
        ), ''),
        i.search_order_key
        """;

    private static async ValueTask<ClipboardHistoryPage> SearchCoreAsync(
        SqliteConnection connection,
        ClipboardHistoryQuery query,
        CancellationToken cancellationToken)
    {
        if (query.PageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        string search = NormalizeSearchText(query.SearchText);
        if (search.EnumerateRunes().Take(3).Count() >= 3)
        {
            return await SearchOrderedFtsAsync(
                    connection,
                    query,
                    search,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await SearchStandardAsync(connection, query, search, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<ClipboardHistoryPage> SearchStandardAsync(
        SqliteConnection connection,
        ClipboardHistoryQuery query,
        string normalizedSearch,
        CancellationToken cancellationToken)
    {
        long totalCount = -1;
        if (normalizedSearch.Length == 0 || query.IncludeSearchResultCount)
        {
            totalCount = await CountMatchesAsync(connection, query, cancellationToken)
                .ConfigureAwait(false);
        }

        QueryPlan pagePlan = BuildQueryPlan(query, includeCursor: true);
        string direction = query.NewestFirst ? "DESC" : "ASC";
        await using SqliteCommand pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT
                {SummaryProjection}
            FROM clipboard_items i
            {pagePlan.JoinClause}
            WHERE {pagePlan.WhereClause}
            ORDER BY i.is_pinned DESC, i.captured_at_utc {direction}, i.id {direction}
            LIMIT @pageLimit;
            """;
        AddParameters(pageCommand, pagePlan.Parameters);
        pageCommand.Parameters.AddWithValue("@pageLimit", query.PageSize + 1);

        List<SearchSummaryRow> rows = await ReadSummaryRowsAsync(
                pageCommand,
                cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = rows.Count > query.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return CreatePage(rows, hasMore, totalCount);
    }

    private static async ValueTask<ClipboardHistoryPage> SearchOrderedFtsAsync(
        SqliteConnection connection,
        ClipboardHistoryQuery query,
        string normalizedSearch,
        CancellationToken cancellationToken)
    {
        long totalCount = query.IncludeSearchResultCount
            ? await CountMatchesAsync(connection, query, cancellationToken).ConfigureAwait(false)
            : -1;
        List<SearchSummaryRow> rows = [];
        int maximumRows = query.PageSize + 1;
        bool searchPinned = query.IsPinned != false &&
            (query.Cursor is null || query.Cursor.IsPinned);
        if (searchPinned)
        {
            long? pinnedCursor = query.Cursor is { IsPinned: true }
                ? query.Cursor.SearchOrderKey
                : null;
            rows.AddRange(await ReadFtsPhaseAsync(
                    connection,
                    query,
                    normalizedSearch,
                    isPinned: true,
                    pinnedCursor,
                    maximumRows,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        bool searchUnpinned = query.IsPinned != true;
        if (searchUnpinned && rows.Count < maximumRows)
        {
            long? unpinnedCursor = query.Cursor is { IsPinned: false }
                ? query.Cursor.SearchOrderKey
                : null;
            rows.AddRange(await ReadFtsPhaseAsync(
                    connection,
                    query,
                    normalizedSearch,
                    isPinned: false,
                    unpinnedCursor,
                    maximumRows - rows.Count,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        bool hasMore = rows.Count > query.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return CreatePage(rows, hasMore, totalCount);
    }

    private static async ValueTask<IReadOnlyList<SearchSummaryRow>> ReadFtsPhaseAsync(
        SqliteConnection connection,
        ClipboardHistoryQuery query,
        string normalizedSearch,
        bool isPinned,
        long? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        QueryPlan plan = BuildFtsPhasePlan(
            query,
            normalizedSearch,
            isPinned,
            cursor);
        string direction = query.NewestFirst ? "DESC" : "ASC";
        string fromClause = isPinned
            ? """
                clipboard_items AS i INDEXED BY ix_clipboard_items_search_phase
                CROSS JOIN clipboard_items_fts
                """
            : """
                clipboard_items_fts
                CROSS JOIN clipboard_items AS i
                """;
        string orderColumn = isPinned ? "i.search_order_key" : "clipboard_items_fts.rowid";
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {SummaryProjection}
            FROM {fromClause}
            WHERE clipboard_items_fts.rowid = i.search_order_key
              AND {plan.WhereClause}
            ORDER BY {orderColumn} {direction}
            LIMIT @pageLimit;
            """;
        AddParameters(command, plan.Parameters);
        command.Parameters.AddWithValue("@pageLimit", limit);
        return await ReadSummaryRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> CountMatchesAsync(
        SqliteConnection connection,
        ClipboardHistoryQuery query,
        CancellationToken cancellationToken)
    {
        QueryPlan totalPlan = BuildQueryPlan(query, includeCursor: false);
        await using SqliteCommand countCommand = connection.CreateCommand();
        countCommand.CommandText = $"""
            SELECT COUNT(*)
            FROM clipboard_items i
            {totalPlan.JoinClause}
            WHERE {totalPlan.WhereClause};
            """;
        AddParameters(countCommand, totalPlan.Parameters);
        return Convert.ToInt64(
            await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static ClipboardHistoryPage CreatePage(
        IReadOnlyList<SearchSummaryRow> rows,
        bool hasMore,
        long totalCount)
    {
        ClipboardHistoryCursor? nextCursor = hasMore && rows.Count > 0
            ? new ClipboardHistoryCursor(
                rows[^1].Summary.IsPinned,
                rows[^1].Summary.CapturedAt.ToUnixTimeMilliseconds(),
                rows[^1].Summary.Id,
                rows[^1].SearchOrderKey)
            : null;
        return new ClipboardHistoryPage(
            rows.Select(row => row.Summary).ToArray(),
            nextCursor,
            totalCount);
    }

    private static async ValueTask<List<SearchSummaryRow>> ReadSummaryRowsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        List<SearchSummaryRow> rows = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SearchSummaryRow(
                new ClipboardHistoryItemSummary(
                    ParseItemId(reader.GetString(0)),
                    (ClipboardContentKind)reader.GetInt32(1),
                    (ClipboardHistoryDisplayCategory)reader.GetInt32(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6) != 0,
                    ParseTags(reader.GetString(11)),
                    reader.GetInt64(7),
                    reader.IsDBNull(8)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                    reader.GetInt64(9),
                    reader.GetInt64(10) != 0),
                reader.GetInt64(12)));
        }

        return rows;
    }

    private async ValueTask<ClipboardHistoryContent?> GetContentCoreAsync(
        SqliteConnection connection,
        ClipboardItemId itemId,
        CancellationToken cancellationToken)
    {
        string identifier = itemId.ToString();
        await using (SqliteCommand exists = connection.CreateCommand())
        {
            exists.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM clipboard_items
                    WHERE id = @id AND is_deleted = 0
                );
                """;
            exists.Parameters.AddWithValue("@id", identifier);
            if (Convert.ToInt32(
                    await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 0)
            {
                return null;
            }
        }

        List<RepresentationRow> representations = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    r.kind,
                    r.inline_text,
                    r.inline_data,
                    b.relative_path,
                    r.bitmap_encoding,
                    r.width,
                    r.height,
                    r.bits_per_pixel
                FROM clipboard_representations r
                LEFT JOIN content_blobs b ON b.hash = r.blob_hash
                WHERE r.item_id = @id
                ORDER BY r.kind;
                """;
            command.Parameters.AddWithValue("@id", identifier);
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                representations.Add(new RepresentationRow(
                    (ClipboardContentKind)reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4)
                        ? null
                        : (ClipboardStoredBitmapEncoding)reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    checked((ushort)reader.GetInt32(7))));
            }
        }

        string? text = null;
        ReadOnlyMemory<byte> html = default;
        ReadOnlyMemory<byte> richText = default;
        ClipboardHistoryBitmap? bitmap = null;
        foreach (RepresentationRow representation in representations)
        {
            ReadOnlyMemory<byte> data = representation.InlineData ??
                (representation.BlobRelativePath is null
                    ? ReadOnlyMemory<byte>.Empty
                    : await _blobStore.ReadAsync(
                            representation.BlobRelativePath,
                            cancellationToken)
                        .ConfigureAwait(false));
            switch (representation.Kind)
            {
                case ClipboardContentKind.Text:
                    text = representation.InlineText ??
                        (data.IsEmpty ? null : Encoding.UTF8.GetString(data.Span));
                    break;
                case ClipboardContentKind.Html:
                    html = data;
                    break;
                case ClipboardContentKind.RichText:
                    richText = data;
                    break;
                case ClipboardContentKind.Image when representation.BitmapEncoding is { } encoding:
                    bitmap = new ClipboardHistoryBitmap(
                        encoding,
                        data,
                        representation.Width,
                        representation.Height,
                        representation.BitsPerPixel);
                    break;
            }
        }

        List<string> filePaths = [];
        await using (SqliteCommand files = connection.CreateCommand())
        {
            files.CommandText = """
                SELECT path
                FROM clipboard_files
                WHERE item_id = @id
                ORDER BY ordinal;
                """;
            files.Parameters.AddWithValue("@id", identifier);
            await using SqliteDataReader reader = await files
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                filePaths.Add(reader.GetString(0));
            }
        }

        return new ClipboardHistoryContent(itemId, text, html, richText, bitmap, filePaths);
    }

    private async Task<ReadOnlyMemory<byte>> GetThumbnailCoreAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string? relativePath = await Task.Run(async () =>
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.relative_path
                FROM clipboard_items i
                JOIN content_blobs b ON b.hash = i.thumbnail_blob_hash
                WHERE i.id = @id AND i.is_deleted = 0;
                """;
            command.Parameters.AddWithValue("@id", itemId.ToString());
            return Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }, cancellationToken).ConfigureAwait(false);
        return relativePath is null
            ? ReadOnlyMemory<byte>.Empty
            : await _blobStore.ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string?> GetSettingCoreAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static QueryPlan BuildQueryPlan(
        ClipboardHistoryQuery query,
        bool includeCursor)
    {
        List<string> predicates = ["i.is_deleted = 0"];
        List<QueryParameter> parameters = [];
        string search = NormalizeSearchText(query.SearchText);
        string join = string.Empty;
        if (search.Length > 0)
        {
            if (search.EnumerateRunes().Take(3).Count() >= 3)
            {
                join = "JOIN clipboard_items_fts ON clipboard_items_fts.rowid = i.search_order_key";
                predicates.Add("clipboard_items_fts MATCH @ftsQuery");
                parameters.Add(new QueryParameter(
                    "@ftsQuery",
                    CreateFtsQuery(search)));
            }
            else
            {
                predicates.Add("i.searchable_text LIKE @likeQuery ESCAPE '\\'");
                parameters.Add(new QueryParameter("@likeQuery", $"%{EscapeLike(search)}%"));
            }
        }

        AddCommonFilterPredicates(query, predicates, parameters, includePinned: true);

        if (includeCursor && query.Cursor is { } cursor)
        {
            string capturedComparison = query.NewestFirst ? "<" : ">";
            string idComparison = query.NewestFirst ? "<" : ">";
            predicates.Add($"""
                (
                    i.is_pinned < @cursorPinned
                    OR (
                        i.is_pinned = @cursorPinned
                        AND i.captured_at_utc {capturedComparison} @cursorCapturedAt
                    )
                    OR (
                        i.is_pinned = @cursorPinned
                        AND i.captured_at_utc = @cursorCapturedAt
                        AND i.id {idComparison} @cursorId
                    )
                )
                """);
            parameters.Add(new QueryParameter("@cursorPinned", cursor.IsPinned ? 1 : 0));
            parameters.Add(new QueryParameter(
                "@cursorCapturedAt",
                cursor.CapturedAtUnixMilliseconds));
            parameters.Add(new QueryParameter("@cursorId", cursor.Id.ToString()));
        }

        return new QueryPlan(join, string.Join(" AND ", predicates), parameters);
    }

    private static QueryPlan BuildFtsPhasePlan(
        ClipboardHistoryQuery query,
        string normalizedSearch,
        bool isPinned,
        long? cursor)
    {
        List<string> predicates =
        [
            "i.is_deleted = 0",
            "i.is_pinned = @phasePinned",
            "clipboard_items_fts MATCH @ftsQuery",
        ];
        List<QueryParameter> parameters =
        [
            new QueryParameter("@phasePinned", isPinned ? 1 : 0),
            new QueryParameter("@ftsQuery", CreateFtsQuery(normalizedSearch)),
        ];
        AddCommonFilterPredicates(query, predicates, parameters, includePinned: false);
        if (cursor is { } orderKey)
        {
            string comparison = query.NewestFirst ? "<" : ">";
            predicates.Add($"i.search_order_key {comparison} @phaseCursor");
            parameters.Add(new QueryParameter("@phaseCursor", orderKey));
        }

        return new QueryPlan(string.Empty, string.Join(" AND ", predicates), parameters);
    }

    private static void AddCommonFilterPredicates(
        ClipboardHistoryQuery query,
        List<string> predicates,
        List<QueryParameter> parameters,
        bool includePinned)
    {
        if (query.DisplayCategory is { } displayCategory)
        {
            predicates.Add("i.display_category = @displayCategory");
            parameters.Add(new QueryParameter("@displayCategory", (int)displayCategory));
        }

        if (query.ContentKinds is { Count: > 0 } contentKinds)
        {
            List<string> names = [];
            int index = 0;
            foreach (ClipboardContentKind kind in contentKinds.OrderBy(kind => kind))
            {
                string name = $"@kind{index++}";
                names.Add(name);
                parameters.Add(new QueryParameter(name, (int)kind));
            }

            predicates.Add($"i.primary_kind IN ({string.Join(", ", names)})");
        }

        if (!string.IsNullOrWhiteSpace(query.SourceApplication))
        {
            predicates.Add("i.source_process_name = @sourceApplication COLLATE NOCASE");
            parameters.Add(new QueryParameter(
                "@sourceApplication",
                query.SourceApplication.Trim()));
        }

        if (query.CapturedAfter is { } after)
        {
            predicates.Add("i.captured_at_utc >= @capturedAfter");
            parameters.Add(new QueryParameter(
                "@capturedAfter",
                after.ToUnixTimeMilliseconds()));
        }

        if (query.CapturedBefore is { } before)
        {
            predicates.Add("i.captured_at_utc <= @capturedBefore");
            parameters.Add(new QueryParameter(
                "@capturedBefore",
                before.ToUnixTimeMilliseconds()));
        }

        if (includePinned && query.IsPinned is { } isPinned)
        {
            predicates.Add("i.is_pinned = @isPinned");
            parameters.Add(new QueryParameter("@isPinned", isPinned ? 1 : 0));
        }

        int tagIndex = 0;
        foreach (string tag in query.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string parameterName = $"@tag{tagIndex++}";
            predicates.Add($"""
                EXISTS(
                    SELECT 1
                    FROM clipboard_item_tags filter_it
                    JOIN clipboard_tags filter_t ON filter_t.id = filter_it.tag_id
                    WHERE filter_it.item_id = i.id
                      AND filter_t.normalized_name = {parameterName}
                )
                """);
            parameters.Add(new QueryParameter(parameterName, tag.Trim().ToUpperInvariant()));
        }
    }

    private static string CreateFtsQuery(string search) =>
        $"\"{search.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void AddParameters(
        SqliteCommand command,
        IReadOnlyList<QueryParameter> parameters)
    {
        foreach (QueryParameter parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static string NormalizeSearchText(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return string.Empty;
        }

        StringBuilder builder = new(Math.Min(searchText.Length, 1024));
        bool whitespace = false;
        foreach (char character in searchText)
        {
            if (builder.Length >= 1024)
            {
                break;
            }

            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                if (!whitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                    whitespace = true;
                }

                continue;
            }

            builder.Append(character);
            whitespace = false;
        }

        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        return builder.ToString().Trim();
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static ClipboardItemId ParseItemId(string value) => new(Guid.Parse(value));

    private static string[] ParseTags(string value) => value.Length == 0
        ? Array.Empty<string>()
        : value.Split((char)31, StringSplitOptions.RemoveEmptyEntries);

    private sealed record QueryPlan(
        string JoinClause,
        string WhereClause,
        IReadOnlyList<QueryParameter> Parameters);

    private sealed record QueryParameter(string Name, object Value);

    private sealed record SearchSummaryRow(
        ClipboardHistoryItemSummary Summary,
        long SearchOrderKey);

    private sealed record RepresentationRow(
        ClipboardContentKind Kind,
        string? InlineText,
        byte[]? InlineData,
        string? BlobRelativePath,
        ClipboardStoredBitmapEncoding? BitmapEncoding,
        int Width,
        int Height,
        ushort BitsPerPixel);
}
