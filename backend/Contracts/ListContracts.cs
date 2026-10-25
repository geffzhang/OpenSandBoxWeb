namespace OpenSandbox.Web.Contracts;

public sealed record PaginationInfoDto(int Page, int PageSize, int TotalItems, int TotalPages, bool HasNextPage);

public sealed record ListSandboxesResponse(IReadOnlyList<SandboxDto> Items, PaginationInfoDto Pagination);
