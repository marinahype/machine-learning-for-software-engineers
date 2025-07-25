using FormCMS.Core.Assets;
using FormCMS.Core.Descriptors;
using FormCMS.Infrastructure.RelationDbDao;
using FormCMS.Utils.DataModels;
using FormCMS.Utils.EnumExt;
using FormCMS.Utils.RecordExt;
using Humanizer;
using Column = FormCMS.Utils.DataModels.Column;
using Query = SqlKata.Query;

namespace FormCMS.Activities.Models;

public record Bookmark(
    string EntityName,
    long RecordId,
    string UserId,
    long? FolderId = null,
    long Id = 0,
    string Title = "",
    string Url = "",
    string Image = "",
    string Subtitle = "",
    DateTime? PublishedAt = null,
    DateTime UpdatedAt = default
);

public static class Bookmarks
{
    internal const string TableName = "__bookmarks";
    internal const string ActivityType = "bookmark";
    const int DefaultPageSize = 8;

    public static readonly Column[] Columns =
    [
        ColumnHelper.CreateCamelColumn<Bookmark>(x => x.Id!, ColumnType.Id),
        ColumnHelper.CreateCamelColumn<Bookmark, string>(x => x.EntityName),
        ColumnHelper.CreateCamelColumn<Bookmark, long>(x => x.RecordId),
        ColumnHelper.CreateCamelColumn<Bookmark, string>(x => x.UserId),
        ColumnHelper.CreateCamelColumn<Bookmark, long?>(x => x.FolderId),

        ColumnHelper.CreateCamelColumn<Bookmark, string>(x => x.Title),
        ColumnHelper.CreateCamelColumn<Bookmark, string>(x => x.Url),
        ColumnHelper.CreateCamelColumn<Bookmark, string>(x => x.Subtitle),
        ColumnHelper.CreateCamelColumn<Bookmark, string>(x => x.Image),

        DefaultAttributeNames.PublishedAt.CreateCamelColumn(ColumnType.Datetime),
        DefaultColumnNames.Deleted.CreateCamelColumn(ColumnType.Boolean),
        DefaultColumnNames.CreatedAt.CreateCamelColumn(ColumnType.CreatedTime),
        DefaultColumnNames.UpdatedAt.CreateCamelColumn(ColumnType.UpdatedTime)
    ];

    public static Record ToInsertRecord(
        this Bookmark bookmark
    ) => RecordExtensions.FormObject(
        bookmark,
        whiteList:
        [
            nameof(Bookmark.EntityName),
            nameof(Bookmark.RecordId),
            nameof(Bookmark.UserId),
            nameof(Bookmark.FolderId),
            nameof(Bookmark.PublishedAt),
            nameof(Bookmark.Title),
            nameof(Bookmark.Url),
            nameof(Bookmark.Image),
            nameof(Bookmark.Subtitle),
        ]
    );

    public static Query Delete(string userId, long id)
        => new Query(TableName)
            .Where(nameof(Bookmark.UserId).Camelize(), userId)
            .Where(nameof(Bookmark.Id).Camelize(), id)
            .AsUpdate([DefaultColumnNames.Deleted.Camelize()], [true]);

    public static Query Delete(string userId, string entityName, long recordId, long? folderId)
        => new Query(TableName)
            .Where(nameof(Bookmark.UserId).Camelize(), userId)
            .Where(nameof(Bookmark.EntityName).Camelize(), entityName)
            .Where(nameof(Bookmark.RecordId).Camelize(), recordId)
            .Where(nameof(Bookmark.FolderId).Camelize(), folderId)
            .AsUpdate([DefaultColumnNames.Deleted.Camelize()], [true]);

    public static Query DeleteBookmarksByFolder(string userId, long folderId)
        => new Query(TableName)
            .Where(nameof(Bookmark.UserId).Camelize(), userId)
            .Where(nameof(Bookmark.FolderId).Camelize(), folderId)
            .AsUpdate([nameof(DefaultColumnNames.Deleted).Camelize()], [true]);

    public static Query FolderIdByUserIdRecordId(
        string userId, string entityName, long recordId
    ) => new Query(TableName)
        .Where(nameof(Bookmark.UserId).Camelize(), userId)
        .Where(nameof(Bookmark.EntityName).Camelize(), entityName)
        .Where(nameof(Bookmark.RecordId).Camelize(), recordId)
        .Where(nameof(DefaultColumnNames.Deleted).Camelize(), false)
        .Select(nameof(Bookmark.FolderId).Camelize());

    public static Query List(string userId, long folderId, int? offset, int? limit)
    {
        var query = new Query(TableName)
            .Select(
                nameof(Bookmark.Id).Camelize(),
                nameof(DefaultColumnNames.UpdatedAt).Camelize(),
                nameof(Bookmark.Image).Camelize(),
                nameof(Bookmark.Title).Camelize(),
                nameof(Bookmark.Subtitle).Camelize(),
                nameof(Bookmark.PublishedAt).Camelize(),
                nameof(Bookmark.Url).Camelize()
            )
            .Where(nameof(Bookmark.UserId).Camelize(), userId)
            .Where(nameof(Bookmark.FolderId).Camelize(), folderId > 0 ? folderId : null)
            .Where(nameof(DefaultColumnNames.Deleted).Camelize(), false);

        if (offset > 0) query.Offset(offset.Value);
        query.Limit(limit ?? DefaultPageSize);
        return query;
    }

    public static Query Count(string userId, long folderId)
        => new Query(TableName)
            .Where(nameof(Bookmark.UserId).Camelize(), userId)
            .Where(nameof(Bookmark.FolderId).Camelize(), folderId > 0 ? folderId : null)
            .Where(nameof(DefaultColumnNames.Deleted).Camelize(), false);
}