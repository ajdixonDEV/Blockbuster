using Blockbuster.Components;
using Blockbuster.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(builder.Configuration["Storage:DataRoot"]))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Storage:DataRoot"] = Path.Combine(builder.Environment.ContentRootPath, ".data")
    });
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddDebug();
}

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddBlockbusterConfiguration(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
