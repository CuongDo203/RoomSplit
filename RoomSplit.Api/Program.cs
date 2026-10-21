using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using RoomSplit.Api.Common;
using RoomSplit.Api.Data;
using RoomSplit.Api.Exceptions;
using RoomSplit.Api.Services.Categories;
using RoomSplit.Api.Services.Members;
using RoomSplit.Api.Services.Rooms;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)))
           .UseSnakeCaseNamingConvention());

// Fails fast at startup if the configured time zone id is unknown on this OS.
var appTimeZone = TimeZoneInfo.FindSystemTimeZoneById(builder.Configuration["App:TimeZone"] ?? "Asia/Ho_Chi_Minh");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAppClock>(sp => new AppClock(sp.GetRequiredService<TimeProvider>(), appTimeZone));

builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IMemberService, MemberService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

// Swagger is on in Development unless configured; other environments opt in via Swagger:Enabled.
if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "RoomSplit API v1"));
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

// live: process is up (no dependencies). ready: database reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();
