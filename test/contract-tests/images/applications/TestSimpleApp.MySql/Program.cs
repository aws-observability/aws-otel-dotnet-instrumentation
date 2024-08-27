using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using TestSimpleApp.MySql.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<BloggingContext>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    using (var client = new BloggingContext())
    {
        client.Database.EnsureCreated();
    }
}

app.MapPost("/create_database", ([FromServices] BloggingContext db) => {
   db.Database.EnsureCreated();
})
.WithName("CreateDatabase")
.WithOpenApi();

app.MapGet("/select", ([FromServices] BloggingContext db) =>
{
    var blog = db.Blogs
        .OrderBy(b => b.BlogId)
        .ToList();

    return blog;
})
.WithName("Select")
.WithOpenApi();

app.MapPost("/create_item", ([FromServices] BloggingContext db) =>
{
    // Add a blog
    db.Add(new Blog
    {
        Url = "https://aws.amazon.com/blogs/opensource/aws-distro-for-opentelemetry-is-now-generally-available-for-metrics/test"
    });
    db.SaveChanges();
    return Results.Ok();
})
.WithName("CreateItem")
.WithOpenApi();

app.MapGet("/drop_table", ([FromServices] BloggingContext db) =>
{
    db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS Posts");
    return Results.Ok();
})
.WithName("DropTable")
.WithOpenApi();

app.MapGet("/fault", ([FromServices] BloggingContext db) =>
{
    var blog = db.Blogs
        .FromSql($"SELECT DISTINCT")
        .ToList();
    return Results.StatusCode(StatusCodes.Status500InternalServerError);
})
.WithName("Fault")
.WithOpenApi();

app.Run();
