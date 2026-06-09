using Microsoft.Extensions.DependencyInjection;

namespace DataBridge;

internal static class ScopedServiceFactoryExtensions
{
    public static async Task WithScopedAsync<TService>(
        this IServiceScopeFactory scopeFactory,
        Func<TService, Task> action)
        where TService : notnull
    {
        using var scope = scopeFactory.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public static async Task<TResult> WithScopedAsync<TService, TResult>(
        this IServiceScopeFactory scopeFactory,
        Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        using var scope = scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public static async Task WithScopedAsync<TService1, TService2>(
        this IServiceScopeFactory scopeFactory,
        Func<TService1, TService2, Task> action)
        where TService1 : notnull
        where TService2 : notnull
    {
        using var scope = scopeFactory.CreateScope();
        await action(
            scope.ServiceProvider.GetRequiredService<TService1>(),
            scope.ServiceProvider.GetRequiredService<TService2>());
    }

    public static async Task<TResult> WithScopedAsync<TService1, TService2, TResult>(
        this IServiceScopeFactory scopeFactory,
        Func<TService1, TService2, Task<TResult>> action)
        where TService1 : notnull
        where TService2 : notnull
    {
        using var scope = scopeFactory.CreateScope();
        return await action(
            scope.ServiceProvider.GetRequiredService<TService1>(),
            scope.ServiceProvider.GetRequiredService<TService2>());
    }
}
