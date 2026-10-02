using DataBridge;
using Lite;
using WebAPI;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddLiteModules();
var app = builder.Build();
app.InitializeDataBridge();
app.MapWebAPIModule();
await app.RunAsync();
