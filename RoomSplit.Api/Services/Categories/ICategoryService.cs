using RoomSplit.Api.Dtos;

namespace RoomSplit.Api.Services.Categories;

public interface ICategoryService
{
    Task<List<CategoryResponse>> GetAllAsync(CancellationToken ct);
}
