using Microsoft.AspNetCore.Mvc;
using RoomSplit.Api.Common;
using RoomSplit.Api.Dtos;
using RoomSplit.Api.Services.Categories;

namespace RoomSplit.Api.Controllers;

[ApiController]
[Route($"{ApiRoutes.Base}/categories")]
public class CategoriesController(ICategoryService categoryService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CategoryResponse>>> GetAll() =>
        await categoryService.GetAllAsync();
}
