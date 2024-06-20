using Microsoft.AspNetCore.Mvc;
using TestSimpleApp.EfCore.Data;

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
        if (!client.Blogs.Any())
        {
            // Add a blog
            client.Add(new Blog
            {
                Url = "https://aws.amazon.com/blogs/opensource/aws-distro-for-opentelemetry-is-now-generally-available-for-metrics/"
            });
            client.SaveChanges();
        }

    }
}

app.MapPost("/blogs", ([FromBody] Blog blog, [FromServices] BloggingContext db) =>
{
    db.Add(blog);
    db.SaveChanges();
    return Results.Ok();
})
.WithName("CreateBlog")
.WithOpenApi();

app.MapGet("/blogs", ([FromServices] BloggingContext db) =>
{
    var blog = db.Blogs
        .OrderBy(b => b.BlogId)
        .ToList();

    return blog;
})
.WithName("GetBlogs")
.WithOpenApi();

app.MapGet("/blogs/{id}", ([FromRoute] int id, [FromServices] BloggingContext db) =>
{
    var blog = db.Blogs.Find(id);
    if (blog == null)
    {
        return Results.NotFound();
    }

    return Results.Ok(blog);
})
.WithName("GetBlogById")
.WithOpenApi();

app.MapDelete("/blogs/{id}", ([FromRoute] int id, [FromServices] BloggingContext db) =>
{
    var blog = db.Blogs.Find(id);
    if (blog == null)
    {
        return Results.NotFound();
    }

    db.Blogs.Remove(blog);
    db.SaveChanges();
    return Results.Ok();
})
.WithName("DeleteBlog")
.WithOpenApi();

app.Run();
