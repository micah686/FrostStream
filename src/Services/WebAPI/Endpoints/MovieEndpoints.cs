using FlySwattr.NATS.Abstractions;
using Shared;
using Shared.Messages;

namespace WebAPI;

public static class MovieEndpoints
{
    public static void MapMovieEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/movies - Query movies via DataBridge
        app.MapGet("/api/movies", async (
            string? title,
            int? year,
            bool? includeUnverified,
            int? page,
            int? pageSize,
            IMessageBus messageBus,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            logger.LogInformation("Querying movies: title={Title}, year={Year}", title, year);

            var request = new MovieQueryRequest
            {
                TitleSearch = title,
                ReleaseYear = year,
                IncludeUnverified = includeUnverified ?? false,
                Page = page ?? 1,
                PageSize = pageSize ?? 20
            };

            var response = await messageBus.RequestAsync<MovieQueryRequest, MovieQueryResponse>(
                Subjects.MovieQuery,
                request,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: ct);

            if (response is null)
                return Results.StatusCode(503);

            return Results.Ok(response);
        })
        .WithName("QueryMovies");

        // GET /api/movies/{id} - Get movie by ID via DataBridge
        app.MapGet("/api/movies/{id:guid}", async (
            Guid id,
            IMessageBus messageBus,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            logger.LogInformation("Getting movie {MovieId}", id);

            var response = await messageBus.RequestAsync<MovieGetRequest, MovieGetResponse>(
                Subjects.MovieGet,
                new MovieGetRequest { MovieId = id },
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: ct);

            if (response?.Movie is null)
                return Results.NotFound();

            return Results.Ok(response);
        })
        .WithName("GetMovie");
    }
}
