using Microsoft.EntityFrameworkCore;
using YandexGamesAnalytics.Catalog;
using YandexGamesAnalytics.Components;
using YandexGamesAnalytics.Data;
using YandexGamesAnalytics.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    var path = Path.Combine(builder.Environment.ContentRootPath, "data");
    Directory.CreateDirectory(path);
    options.UseSqlite($"Data Source={Path.Combine(path, "catalog.db")}");
});

builder.Services.AddHttpClient("yandex-games", YandexCatalogClient.Configure);
builder.Services.AddSingleton<YandexCatalogClient>(sp =>
    new YandexCatalogClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("yandex-games"),
        sp.GetRequiredService<ILogger<YandexCatalogClient>>()));
builder.Services.AddSingleton<SyncCoordinator>();
builder.Services.AddSingleton<CatalogSyncService>();
builder.Services.AddScoped<AnalyticsEngine>();

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
