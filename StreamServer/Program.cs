using StreamServer;
using StreamServer.Extensions;

var builder = WebApplication.CreateBuilder(args);
//builder.Host.ConfigureSerilog();
builder.UseStartup<Startup>();
