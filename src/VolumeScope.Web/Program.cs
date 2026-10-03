using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using VolumeScope.Web;
using VolumeScope.Web.State;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddSingleton<Studio>();
builder.Services.AddSingleton<BrowserIo>();
await builder.Build().RunAsync();
