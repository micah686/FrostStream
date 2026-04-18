using Scalar.AspNetCore;

namespace WebAPI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Add services to the container.
        builder.Services.AddAuthorization();

        
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        // if (app.Environment.IsDevelopment())
        // {
            app.MapOpenApi();
            app.MapScalarApiReference();
        //}

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapDefaultEndpoints();

        app.Run();
    }
}
