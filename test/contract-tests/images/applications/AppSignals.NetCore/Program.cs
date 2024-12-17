using System;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/success", async () =>
    {
        return Results.Ok();
    })
    .WithName("Success")
    .WithOpenApi();

app.MapGet("/error", async () =>
    {
        return Results.StatusCode(400);
    })
    .WithName("Error")
    .WithOpenApi();

app.MapGet("/fault", async () =>
    {
        return Results.StatusCode(500);
    })
    .WithName("Fault")
    .WithOpenApi();

app.MapGet("/gc", async () =>
    {
        GC.Collect();
        return Results.Ok();
    })
    .WithName("GCSuccess")
    .WithOpenApi();

app.MapPost("/success/postmethod", async () =>
    {
        return Results.Ok();
    })
    .WithName("SuccessPost")
    .WithOpenApi();

app.MapPost("/error/postmethod", async () =>
    {
        return Results.StatusCode(400);
    })
    .WithName("ErrorPost")
    .WithOpenApi();

app.MapPost("/fault/postmethod", async () =>
    {
        return Results.StatusCode(500);
    })
    .WithName("FaultPost")
    .WithOpenApi();

app.Run();
