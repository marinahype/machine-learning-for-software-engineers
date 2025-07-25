using System.Collections.Immutable;
using FormCMS.Cms.Services;
using FormCMS.Core.Descriptors;

namespace FormCMS.Core.HookFactory;

public record QueryPreListArgs(
    LoadedQuery Query,
    ImmutableArray<ValidFilter> Filters,
    ImmutableArray<ValidSort> Sorts,
    ValidSpan Span,
    ValidPagination Pagination,
    Record[]? OutRecords = null
) : BaseArgs(Query.Name);

public record QueryPostListArgs(
    LoadedQuery Query,
    ValidSpan Span,
    ValidPagination Pagination,
    Record[] RefRecords 
) : BaseArgs(Query.Name);


public record QueryPreSingleArgs(
    LoadedQuery Query,
    Record? OutRecord = null
) : BaseArgs(Query.Name);

public record QueryPostSingleArgs(
    LoadedQuery Query,
    Record RefRecord,
    StrArgs StrArgs
) : BaseArgs(Query.Name);

public record QueryPartialArgs(
    LoadedEntity ParentEntity,
    GraphNode Node,
    ValidSpan Span,
    ValidPagination Pagination,
    long SourceId,
    Record[]? OutRecords  = null 
):BaseArgs(Node.Field);

public record QueryPostPartialArgs(
    GraphNode Node,
    Record[] RefRecords  
):BaseArgs(Node.Field);

public record ListPlugInQueryArgs(
    string Name,
    Span Span,
    Pagination Pagination,
    StrArgs Args,
    Record[]? OutRecords = null
) : BaseArgs(Name);

public record SinglePlugInQueryArgs(
    string Name,
    StrArgs Args,
    Record? OutRecord = null
) : BaseArgs(Name);