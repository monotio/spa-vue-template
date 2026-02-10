using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VueApp1.Server;

namespace VueApp1.Server.IntegrationTests.Infrastructure;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Empty);
    }
}
