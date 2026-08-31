using Microsoft.Extensions.Hosting;

// Azure Functions .NET-isolated host. ASP.NET Core integration gives us HttpRequest/
// IActionResult in the function signatures.
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .Build();

host.Run();
