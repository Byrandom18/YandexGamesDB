using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YandexGamesAnalytics.Catalog;

public sealed class CatalogGameDto
{
    public int AppId { get; set; }
    public string Title { get; set; } = "";
    public string? AppSlug { get; set; }
    public string? PlayUrl { get; set; }
    public int? DeveloperId { get; set; }
    public string? DeveloperName { get; set; }
    public double? Rating { get; set; }
    public int RatingCount { get; set; }
    public int? GqRating { get; set; }
    public DateTime? FirstPublished { get; set; }
    public double? MinLoadTime { get; set; }
    public string? AgeRating { get; set; }
    public string? Description { get; set; }
    public string? Instruction { get; set; }
    public bool HasPurchases { get; set; }
    public bool HasProducts { get; set; }
    public bool HasLeaderboards { get; set; }
    public bool InAppGame { get; set; }
    public bool CloudSave { get; set; }
    public bool HasVideo { get; set; }
    public int ScreenshotCount { get; set; }
    public bool SupportsDesktop { get; set; }
    public bool SupportsMobile { get; set; }
    public bool SupportsTv { get; set; }
    public string? Orientation { get; set; }
    public string? Languages { get; set; }
    public int LanguageCount { get; set; }
    public int? Score1 { get; set; }
    public int? Score2 { get; set; }
    public int? Score3 { get; set; }
    public int? Score4 { get; set; }
    public int? Score5 { get; set; }
    public string? IconUrl { get; set; }
    public string? CoverUrl { get; set; }
    public string? CoverColor { get; set; }
    public List<(int Id, string? Slug)> Categories { get; set; } = [];
    public List<int> TagIds { get; set; } = [];
    public bool Enriched { get; set; }
}

public sealed class CatalogTagDto
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public int GamesCount { get; set; }
}

public sealed class AllGamesPage
{
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public List<CatalogGameDto> Games { get; set; } = [];
}

public class YandexCatalogClient(HttpClient http, ILogger<YandexCatalogClient> logger)
{
    public async Task<AllGamesPage> GetAllGamesPageAsync(int page, CancellationToken ct)
    {
        var path = page <= 1 ? "/games/all-games" : $"/games/all-games?page={page}";
        var state = await GetAppStateAsync(path, ct);
        var all = state["allGames"] ?? throw new InvalidOperationException("allGames missing");
        var totalPages = all["totalPages"]?.GetValue<int>() ?? 1;
        var games = new List<CatalogGameDto>();
        if (all["items"] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is JsonObject obj)
                    games.Add(ParseGame(obj, enriched: false));
            }
        }

        return new AllGamesPage { Page = page, TotalPages = Math.Max(1, totalPages), Games = games };
    }

    public async Task<List<CatalogTagDto>> GetTagsAsync(CancellationToken ct)
    {
        var state = await GetAppStateAsync("/games/tags", ct);
        var result = new List<CatalogTagDto>();
        if (state["tags"] is not JsonArray tags)
            return result;

        foreach (var tag in tags)
        {
            if (tag is not JsonObject obj)
                continue;

            result.Add(new CatalogTagDto
            {
                Id = obj["id"]?.GetValue<int>() ?? 0,
                Slug = obj["slug"]?.GetValue<string>() ?? "",
                Title = obj["title"]?.GetValue<string>() ?? "",
                GamesCount = obj["info"]?["games_count"]?.GetValue<int>() ?? 0
            });
        }

        return result.Where(t => t.Id > 0).ToList();
    }

    public async Task<List<int>> GetCategoryRankingAsync(string slug, int page, CancellationToken ct)
    {
        var path = page <= 1 ? $"/games/category/{slug}" : $"/games/category/{slug}?page={page}";
        var state = await GetAppStateAsync(path, ct);
        var ids = new List<int>();
        var seen = new HashSet<int>();

        if (state["feedsData"] is not JsonObject feeds)
            return ids;

        foreach (var feed in feeds)
        {
            if (feed.Value is not JsonArray blocks)
                continue;

            foreach (var block in blocks)
            {
                if (block is not JsonObject blockObj)
                    continue;
                if (blockObj["widgets"] is not JsonArray widgets)
                    continue;

                foreach (var widget in widgets)
                {
                    var data = widget?["data"] as JsonObject ?? widget as JsonObject;
                    var appId = data?["appID"]?.GetValue<int>() ?? 0;
                    if (appId > 0 && seen.Add(appId))
                        ids.Add(appId);
                }
            }
        }

        return ids;
    }

    public async Task<List<int>> GetFeaturedIdsAsync(CancellationToken ct)
    {
        var state = await GetAppStateAsync("/games/", ct);
        var ids = new List<int>();
        var seen = new HashSet<int>();
        if (state["feedsData"] is not JsonObject feeds)
            return ids;

        foreach (var feed in feeds)
        {
            if (feed.Value is not JsonArray blocks)
                continue;
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() != "grid_layout")
                    continue;
                if (block["widgets"] is not JsonArray widgets)
                    continue;
                foreach (var widget in widgets)
                {
                    var appId = widget?["data"]?["appID"]?.GetValue<int>() ?? 0;
                    if (appId > 0 && seen.Add(appId))
                        ids.Add(appId);
                }
            }
        }

        return ids;
    }

    public async Task<List<CatalogGameDto>> GetGamesLongAsync(IReadOnlyList<int> appIds, CancellationToken ct)
    {
        if (appIds.Count == 0)
            return [];

        var payload = JsonSerializer.Serialize(new { appIDs = appIds, format = "long" });
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/games/api/catalogue/v2/get_games?lang=ru&draft=false")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            return request;
        }, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct);
        var games = new List<CatalogGameDto>();
        if (node?["games"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject obj)
                    games.Add(ParseGame(obj, enriched: true));
            }
        }

        return games;
    }

    private async Task<JsonObject> GetAppStateAsync(string path, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var json = ExtractAppState(html)
            ?? throw new InvalidOperationException($"Не найден __appState__ на {path}");
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"Некорректный JSON на {path}");
        return node;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var request = factory();
                var response = await http.SendAsync(request, ct);
                if ((int)response.StatusCode is 429 or >= 500)
                {
                    logger.LogWarning("Yandex {Status} on {Uri}, attempt {Attempt}", response.StatusCode, request.RequestUri, attempt);
                    response.Dispose();
                    await Task.Delay(400 * attempt, ct);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                last = ex;
                logger.LogWarning(ex, "Request failed, attempt {Attempt}", attempt);
                await Task.Delay(400 * attempt, ct);
            }
        }

        throw last ?? new HttpRequestException("Не удалось обратиться к каталогу Яндекс Игр");
    }

    private static string? ExtractAppState(string html)
    {
        const string marker = "id=\"__appState__\"";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var start = html.IndexOf('>', idx);
        if (start < 0)
            return null;

        start++;
        var end = html.IndexOf("</script>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return null;

        return html[start..end].Trim();
    }

    private static CatalogGameDto ParseGame(JsonObject obj, bool enriched)
    {
        var dto = new CatalogGameDto
        {
            AppId = obj["appID"]?.GetValue<int>() ?? 0,
            Title = obj["title"]?.GetValue<string>() ?? "",
            AppSlug = obj["appSlug"]?.GetValue<string>(),
            PlayUrl = obj["url"]?.GetValue<string>(),
            Rating = GetDouble(obj["rating"]),
            RatingCount = obj["ratingCount"]?.GetValue<int>() ?? 0,
            GqRating = obj["gqRating"]?.GetValue<int>(),
            Description = obj["description"]?.GetValue<string>(),
            Instruction = obj["instruction"]?.GetValue<string>(),
            Enriched = enriched
        };

        if (obj["developer"] is JsonObject dev)
        {
            dto.DeveloperId = dev["id"]?.GetValue<int>();
            dto.DeveloperName = dev["name"]?.GetValue<string>();
        }

        if (obj["firstPublished"] is JsonValue published && published.TryGetValue<long>(out var unix) && unix > 0)
            dto.FirstPublished = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        dto.MinLoadTime = GetDouble(obj["minLoadTime"]);

        if (obj["extraFeatures"] is JsonObject extra)
        {
            dto.HasPurchases = extra["purchases"]?.GetValue<bool>() ?? false;
            dto.HasProducts = extra["hasProducts"]?.GetValue<bool>() ?? false;
            dto.HasLeaderboards = extra["leaderboards"]?.GetValue<bool>() ?? false;
            dto.InAppGame = extra["inAppGame"]?.GetValue<bool>() ?? false;
        }

        if (obj["features"] is JsonObject features)
        {
            dto.AgeRating = features["age_rating"]?.GetValue<string>();
            dto.CloudSave = features["cloud_save"]?.GetValue<bool>() ?? false;
            dto.Orientation = features["orientation"]?.GetValue<string>();

            if (features["languages"] is JsonArray langs)
            {
                var list = langs.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
                dto.Languages = string.Join(",", list);
                dto.LanguageCount = list.Count;
            }
            else if (features["game_languages"] is JsonObject gameLangs)
            {
                var list = gameLangs.Where(p => p.Value?.GetValue<bool>() == true).Select(p => p.Key).ToList();
                dto.Languages = string.Join(",", list);
                dto.LanguageCount = list.Count;
            }

            if (features["platforms"] is JsonObject platforms)
            {
                dto.SupportsDesktop = IsPlatformEnabled(platforms["desktop"]);
                dto.SupportsMobile = IsPlatformEnabled(platforms["android"]) || IsPlatformEnabled(platforms["ios"]);
                dto.SupportsTv = IsPlatformEnabled(platforms["tv"]);
            }
        }

        if (obj["score"] is JsonObject score)
        {
            dto.Score1 = score["1"]?.GetValue<int>();
            dto.Score2 = score["2"]?.GetValue<int>();
            dto.Score3 = score["3"]?.GetValue<int>();
            dto.Score4 = score["4"]?.GetValue<int>();
            dto.Score5 = score["5"]?.GetValue<int>();
        }

        if (obj["media"] is JsonObject media)
        {
            dto.IconUrl = media["icon"]?["prefix-url"]?.GetValue<string>() ?? media["icon"]?["prefixURL"]?.GetValue<string>();
            dto.CoverUrl = media["cover"]?["prefix-url"]?.GetValue<string>() ?? media["cover"]?["prefixURL"]?.GetValue<string>();
            dto.CoverColor = media["cover"]?["mainColor"]?.GetValue<string>();
            dto.HasVideo = media["videos"] is JsonArray videos && videos.Count > 0;

            var shots = 0;
            if (media["screenshots"] is JsonObject shotsObj)
            {
                if (shotsObj["desktop"] is JsonArray d)
                    shots += d.Count;
                if (shotsObj["mobile"] is JsonArray m)
                    shots += m.Count;
            }

            dto.ScreenshotCount = shots;
        }

        var ids = ReadIntArray(obj["categoryIDs"]);
        var names = ReadStringArray(obj["categoriesNames"]);
        var paired = Math.Min(ids.Count, names.Count);
        for (var i = 0; i < paired; i++)
            dto.Categories.Add((ids[i], names[i]));

        dto.TagIds = ReadIntArray(obj["tagIDs"]);
        return dto;
    }

    private static bool IsPlatformEnabled(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out var flag))
            return flag;
        if (node is not JsonObject obj)
            return false;
        return obj.Any(p => p.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b);
    }

    private static List<int> ReadIntArray(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [];
        return array.Select(x => x?.GetValue<int>() ?? 0).Where(x => x > 0).ToList();
    }

    private static List<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
            return [];
        return array.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
    }

    private static double? GetDouble(JsonNode? node)
    {
        if (node is null)
            return null;
        if (node is JsonValue v && v.TryGetValue<double>(out var d))
            return d;
        if (node is JsonValue v2 && v2.TryGetValue<int>(out var i))
            return i;
        return null;
    }

    public static void Configure(HttpClient client)
    {
        client.BaseAddress = new Uri(CatalogConstants.CatalogHost);
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Referrer = new Uri("https://yandex.ru/games/");
    }
}
