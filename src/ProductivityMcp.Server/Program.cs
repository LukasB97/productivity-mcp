using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;
using ProductivityMcp.Server;

var options = GoogleOptions.FromEnvironment();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(console =>
{
    console.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<MultiGoogleProvider>();
builder.Services.AddSingleton<ICalendarProvider>(services => services.GetRequiredService<MultiGoogleProvider>());
builder.Services.AddSingleton<ITasksProvider>(services => services.GetRequiredService<MultiGoogleProvider>());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CalendarTools>()
    .WithTools<TasksTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, cancellationToken) =>
    {
        try
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            return McpToolResults.Error(McpToolResults.InvalidJson(exception));
        }
    }));

await builder.Build().RunAsync();
