using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Data;
using RoomSplit.Api.Dtos;

namespace RoomSplit.Api.Services.Categories;

public class CategoryService(AppDbContext db) : ICategoryService
{
    public Task<List<CategoryResponse>> GetAllAsync(CancellationToken ct) =>
        db.Categories
            .OrderBy(c => c.Id)
            .Select(c => new CategoryResponse(c.Id, c.Name))
            .ToListAsync(ct);
}
