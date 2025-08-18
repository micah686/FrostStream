using FrostStream.CoreAPI.Services;
using TickerQ.Dashboard.DependencyInjection;
using TickerQ.DependencyInjection;

namespace FrostStream.CoreAPI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.AddTickerQ(options =>
            {
                options.SetMaxConcurrency(4); // Optional
                
                options.AddDashboard(basePath: "/tickerq-dashboard"); // Dashboard path
            });

            // Register JobQueueService as a singleton
            builder.Services.AddSingleton<JobQueueService>();

            // builder.Services.AddSingleton<JobServer>();
            // builder.Services.AddHostedService(provider => provider.GetRequiredService<JobServer>());

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.UseTickerQ();


            app.MapControllers();

            app.Run();
        }
    }
}
