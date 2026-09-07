using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client;
using SmartX.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The gateway endpoint is configuration, not a literal. wwwroot/appsettings.json is
// fetched at startup, so the same published client can be pointed at a different gateway
// without a rebuild. Blank means "same origin as this page", which is how the hosted
// deployment runs - the API serves this client, so there is nothing to configure locally.
var configured = builder.Configuration["Gateway:BaseUrl"];
var baseAddress = string.IsNullOrWhiteSpace(configured) ? builder.HostEnvironment.BaseAddress : configured;

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(baseAddress),
    Timeout = TimeSpan.FromMinutes(5)   // uploads are streamed and can outlive the default
});

builder.Services.AddScoped<GatewayClient>();

await builder.Build().RunAsync();
