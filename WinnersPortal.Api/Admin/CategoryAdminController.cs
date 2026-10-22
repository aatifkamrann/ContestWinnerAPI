using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Admin;

namespace WinnersPortal.Api.Admin;

/// <summary>The HTTP edge of <see cref="CategoryAdminService"/>: Admin → Categories reads the list and saves it whole.</summary>
[ApiController]
public sealed class CategoryAdminController(CategoryAdminService categories) : ControllerBase
{
    [HttpGet("api/admin/categories")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetCategories(CancellationToken ct) =>
        (await categories.ListAsync(ct)).ToResult();

    // The whole list in its new order, naming the version it was read at:
    // a key missing from it is a removal, refused while anything uses it.
    [HttpPut("api/admin/categories")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PutCategories(CategoriesSaveRequest request, CancellationToken ct) =>
        (await categories.SaveAsync(request, User, ct)).ToResult();
}
