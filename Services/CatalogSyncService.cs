using Microsoft.EntityFrameworkCore;
using YandexGamesAnalytics.Catalog;
using YandexGamesAnalytics.Data;

namespace YandexGamesAnalytics.Services;

public class CatalogSyncService(
    IDbContextFactory<AppDbContext> dbFactory,
    YandexCatalogClient catalog,
    SyncCoordinator coordinator,
    AnalyticsCache analytics,
    ILogger<CatalogSyncService> logger)
{
    private const int EnrichBatchSize = 80;

    public bool Start(SyncMode mode)
    {
        if (!coordinator.TryBegin(out var ct))
            return false;

        var task = Task.Run(() => RunAsync(mode, ct), CancellationToken.None);
        coordinator.Attach(task);
        return true;
    }

    public void Cancel() => coordinator.Cancel();

    private async Task RunAsync(SyncMode mode, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = new SyncRun
        {
            StartedAt = DateTime.UtcNow,
            Mode = mode.ToString(),
            Status = "running"
        };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var runId = run.Id;
        coordinator.Progress.LastMode = mode.ToString();

        try
        {
            SeedCategories(db);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            await SyncTagsAsync(db, ct);
            db.ChangeTracker.Clear();

            await SyncListingAsync(db, mode, ct);
            db.ChangeTracker.Clear();

            await SyncRanksAsync(db, mode, ct);
            db.ChangeTracker.Clear();

            var collected = await db.Games.CountAsync(ct);
            coordinator.Progress.GamesCollected = collected;

            await EnrichAsync(ct);

            await RecomputeDemandAsync(db, ct);

            var done = await db.SyncRuns.FirstAsync(x => x.Id == runId, CancellationToken.None);
            done.FinishedAt = DateTime.UtcNow;
            done.Status = "ok";
            done.GamesCollected = coordinator.Progress.GamesCollected;
            done.GamesEnriched = coordinator.Progress.GamesEnriched;
            await db.SaveChangesAsync(CancellationToken.None);
            analytics.Invalidate();
            coordinator.Complete();
        }
        catch (OperationCanceledException)
        {
            var cancelled = await db.SyncRuns.FirstAsync(x => x.Id == runId, CancellationToken.None);
            cancelled.FinishedAt = DateTime.UtcNow;
            cancelled.Status = "cancelled";
            cancelled.Error = "Остановлено пользователем";
            await db.SaveChangesAsync(CancellationToken.None);
            analytics.Invalidate();
            coordinator.Complete("Сбор остановлен.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalog sync failed");
            var failed = await db.SyncRuns.FirstAsync(x => x.Id == runId, CancellationToken.None);
            failed.FinishedAt = DateTime.UtcNow;
            failed.Status = "error";
            failed.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            analytics.Invalidate();
            coordinator.Complete(ex.Message);
        }
    }

    private static void SeedCategories(AppDbContext db)
    {
        var existing = db.Categories.Select(c => c.Slug).ToHashSet();
        foreach (var (slug, title) in CatalogConstants.CategoryTitles)
        {
            if (existing.Contains(slug))
                continue;
            db.Categories.Add(new Category { Slug = slug, Title = title });
        }
    }

    private async Task SyncTagsAsync(AppDbContext db, CancellationToken ct)
    {
        SetProgress("Теги", "Загружаю словарь тегов…", 0, 1);
        var tags = await catalog.GetTagsAsync(ct);
        var existing = await db.Tags.ToDictionaryAsync(t => t.Id, ct);
        foreach (var dto in tags)
        {
            if (existing.TryGetValue(dto.Id, out var tag))
            {
                tag.Slug = dto.Slug;
                tag.Title = dto.Title;
                tag.CatalogGamesCount = dto.GamesCount;
            }
            else
            {
                db.Tags.Add(new Tag
                {
                    Id = dto.Id,
                    Slug = dto.Slug,
                    Title = dto.Title,
                    CatalogGamesCount = dto.GamesCount
                });
            }
        }

        await db.SaveChangesAsync(ct);
        SetProgress("Теги", $"Тегов: {tags.Count}", 1, 1);
    }

    private async Task SyncListingAsync(AppDbContext db, SyncMode mode, CancellationToken ct)
    {
        var first = await catalog.GetAllGamesPageAsync(1, ct);
        var maxPages = mode == SyncMode.Sample ? Math.Min(4, first.TotalPages) : first.TotalPages;
        SetProgress("Каталог", $"Страница 1 из {maxPages}", 1, maxPages);
        await UpsertListingAsync(db, first.Games, ct);

        for (var page = 2; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(120, ct);
            var batch = await catalog.GetAllGamesPageAsync(page, ct);
            await UpsertListingAsync(db, batch.Games, ct);
            SetProgress("Каталог", $"Страница {page} из {maxPages}", page, maxPages);
        }

        coordinator.Progress.GamesCollected = await db.Games.CountAsync(ct);
    }

    private async Task SyncRanksAsync(AppDbContext db, SyncMode mode, CancellationToken ct)
    {
        var slugs = CatalogConstants.RankedCategorySlugs;
        var pagesPerCategory = mode == SyncMode.Sample ? 1 : 2;
        var total = slugs.Length * pagesPerCategory + 1;
        var current = 0;

        SetProgress("Витрина", "Рекомендации на главной…", current, total);
        var featured = await catalog.GetFeaturedIdsAsync(ct);
        await db.Games.Where(g => g.Featured).ExecuteUpdateAsync(s => s.SetProperty(g => g.Featured, false), ct);
        if (featured.Count > 0)
        {
            await db.Games.Where(g => featured.Contains(g.AppId))
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Featured, true), ct);
        }

        current++;
        await db.CategoryRankings.ExecuteDeleteAsync(ct);
        await db.Games.Where(g => g.InNewFeed).ExecuteUpdateAsync(s => s.SetProperty(g => g.InNewFeed, false), ct);

        foreach (var slug in slugs)
        {
            var rank = 1;
            var ids = new List<int>();
            for (var page = 1; page <= pagesPerCategory; page++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(120, ct);
                current++;
                SetProgress("Жанры", $"{CatalogConstants.TitleFor(slug)}, стр. {page}", current, total);
                try
                {
                    var pageIds = await catalog.GetCategoryRankingAsync(slug, page, ct);
                    ids.AddRange(pageIds);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Category {Slug} page {Page} failed", slug, page);
                }
            }

            foreach (var id in ids.Distinct())
            {
                db.CategoryRankings.Add(new CategoryRanking
                {
                    CategorySlug = slug,
                    AppId = id,
                    Rank = rank++
                });
            }

            if (slug == "new" && ids.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                await db.Games.Where(g => ids.Contains(g.AppId))
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.InNewFeed, true), ct);
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task EnrichAsync(CancellationToken ct)
    {
        List<int> ids;
        int already;
        CatalogLookup lookup;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            already = await db.Games.CountAsync(g => g.Enriched, ct);
            ids = await db.Games.AsNoTracking()
                .Where(g => !g.Enriched)
                .Select(g => g.AppId)
                .ToListAsync(ct);
            lookup = await CatalogLookup.LoadAsync(db, ct);
        }

        coordinator.Progress.GamesEnriched = already;
        if (ids.Count == 0)
        {
            SetProgress("Карточки игр", $"Все {already} игр уже с подробностями", 1, 1);
            return;
        }

        var total = (int)Math.Ceiling(ids.Count / (double)EnrichBatchSize);
        SetProgress("Карточки игр", $"Докачиваю {ids.Count} без карточки (уже есть {already})", 0, total);

        Task<List<CatalogGameDto>>? inflight = null;
        var done = 0;
        for (var i = 0; i < ids.Count; i += EnrichBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = ids.GetRange(i, Math.Min(EnrichBatchSize, ids.Count - i));
            inflight ??= catalog.GetGamesLongAsync(slice, ct);
            var details = await inflight;

            var next = i + EnrichBatchSize;
            inflight = next < ids.Count
                ? catalog.GetGamesLongAsync(ids.GetRange(next, Math.Min(EnrichBatchSize, ids.Count - next)), ct)
                : null;

            await using var batchDb = await dbFactory.CreateDbContextAsync(ct);
            batchDb.ChangeTracker.AutoDetectChangesEnabled = false;
            await UpsertEnrichedAsync(batchDb, details, lookup, ct);

            done += slice.Count;
            coordinator.Progress.GamesEnriched = already + done;
            SetProgress(
                "Карточки игр",
                $"Подробности {already + done} из {already + ids.Count}",
                i / EnrichBatchSize + 1,
                total);
        }
    }

    private static async Task UpsertListingAsync(AppDbContext db, List<CatalogGameDto> dtos, CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var ids = dtos.Select(d => d.AppId).Where(id => id > 0).ToList();
        var existing = await db.Games.Where(g => ids.Contains(g.AppId)).ToDictionaryAsync(g => g.AppId, ct);
        var now = DateTime.UtcNow;

        foreach (var dto in dtos.Where(d => d.AppId > 0))
        {
            if (!existing.TryGetValue(dto.AppId, out var game))
            {
                game = new Game { AppId = dto.AppId, CollectedAt = now };
                db.Games.Add(game);
            }

            ApplyListing(game, dto, now);
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static async Task UpsertEnrichedAsync(
        AppDbContext db, List<CatalogGameDto> dtos, CatalogLookup lookup, CancellationToken ct)
    {
        if (dtos.Count == 0)
            return;

        var ids = dtos.Select(d => d.AppId).Where(id => id > 0).Distinct().ToList();
        var existing = await db.Games.Where(g => ids.Contains(g.AppId)).ToDictionaryAsync(g => g.AppId, ct);
        var oldCats = await db.GameCategories.Where(x => ids.Contains(x.AppId)).ToListAsync(ct);
        var oldTags = await db.GameTags.Where(x => ids.Contains(x.AppId)).ToListAsync(ct);
        db.GameCategories.RemoveRange(oldCats);
        db.GameTags.RemoveRange(oldTags);

        var now = DateTime.UtcNow;
        var seenCats = new HashSet<(int AppId, string Slug)>();
        var seenTags = new HashSet<(int AppId, int TagId)>();

        foreach (var dto in dtos.Where(d => d.AppId > 0))
        {
            if (!existing.TryGetValue(dto.AppId, out var game))
            {
                game = new Game { AppId = dto.AppId, CollectedAt = now };
                db.Games.Add(game);
                existing[dto.AppId] = game;
            }

            ApplyListing(game, dto, now);
            ApplyEnrichmentScalars(game, dto, now);

            foreach (var (id, slugHint) in dto.Categories)
            {
                var slug = lookup.EnsureCategory(db, id, slugHint);
                if (slug is null)
                    continue;
                if (!seenCats.Add((game.AppId, slug)))
                    continue;
                db.GameCategories.Add(new GameCategory { AppId = game.AppId, CategorySlug = slug });
            }

            foreach (var tagId in dto.TagIds.Distinct())
            {
                lookup.EnsureTag(db, tagId);
                if (!seenTags.Add((game.AppId, tagId)))
                    continue;
                db.GameTags.Add(new GameTag { AppId = game.AppId, TagId = tagId });
            }
        }

        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync(ct);
    }

    private static void ApplyListing(Game game, CatalogGameDto dto, DateTime now)
    {
        game.Title = dto.Title;
        game.AppSlug = dto.AppSlug ?? game.AppSlug;
        game.PlayUrl = dto.PlayUrl ?? game.PlayUrl;
        game.DeveloperId = dto.DeveloperId ?? game.DeveloperId;
        game.DeveloperName = dto.DeveloperName ?? game.DeveloperName;
        game.Rating = dto.Rating ?? game.Rating;
        game.RatingCount = dto.RatingCount;
        game.GqRating = dto.GqRating ?? game.GqRating;
        game.AgeRating = dto.AgeRating ?? game.AgeRating;
        game.IconUrl = dto.IconUrl ?? game.IconUrl;
        game.CoverUrl = dto.CoverUrl ?? game.CoverUrl;
        game.CoverColor = dto.CoverColor ?? game.CoverColor;
        game.HasVideo = game.HasVideo || dto.HasVideo;
        game.CollectedAt = now;
    }

    private static void ApplyEnrichmentScalars(Game game, CatalogGameDto dto, DateTime now)
    {
        game.Enriched = true;
        game.EnrichedAt = now;
        game.FirstPublished = dto.FirstPublished ?? game.FirstPublished;
        game.MinLoadTime = dto.MinLoadTime ?? game.MinLoadTime;
        game.Description = dto.Description ?? game.Description;
        game.Instruction = dto.Instruction ?? game.Instruction;
        game.HasPurchases = dto.HasPurchases;
        game.HasProducts = dto.HasProducts;
        game.HasLeaderboards = dto.HasLeaderboards;
        game.InAppGame = dto.InAppGame;
        game.CloudSave = dto.CloudSave;
        game.HasVideo = dto.HasVideo;
        game.ScreenshotCount = dto.ScreenshotCount;
        game.SupportsDesktop = dto.SupportsDesktop;
        game.SupportsMobile = dto.SupportsMobile;
        game.SupportsTv = dto.SupportsTv;
        game.Orientation = dto.Orientation ?? game.Orientation;
        game.Languages = dto.Languages ?? game.Languages;
        game.LanguageCount = dto.LanguageCount;
        game.Score1 = dto.Score1;
        game.Score2 = dto.Score2;
        game.Score3 = dto.Score3;
        game.Score4 = dto.Score4;
        game.Score5 = dto.Score5;
        game.CategoryCount = dto.Categories.Count;
        game.TagCount = dto.TagIds.Count;
    }

    private static async Task RecomputeDemandAsync(AppDbContext db, CancellationToken ct)
    {
        var games = await db.Games.ToListAsync(ct);
        if (games.Count == 0)
            return;

        var gq = games.Select(g => (double)(g.GqRating ?? 0)).ToList();
        var reviews = games.Select(g => Math.Log(1 + g.RatingCount)).ToList();
        var rating = games.Select(g => g.Rating ?? 0).ToList();
        var recency = games.Select(g => Recency(g.FirstPublished)).ToList();
        var gqMin = gq.Min();
        var gqMax = gq.Max();
        var revMin = reviews.Min();
        var revMax = reviews.Max();
        var ratMin = rating.Min();
        var ratMax = rating.Max();
        var recMin = recency.Min();
        var recMax = recency.Max();

        for (var i = 0; i < games.Count; i++)
        {
            games[i].DemandScore =
                40 * Norm(gq[i], gqMin, gqMax) +
                30 * Norm(reviews[i], revMin, revMax) +
                15 * Norm(rating[i], ratMin, ratMax) +
                15 * Norm(recency[i], recMin, recMax);
        }

        await db.SaveChangesAsync(ct);
    }

    private static double Recency(DateTime? published)
    {
        if (published is null)
            return 0.25;
        var days = Math.Max(0, (DateTime.UtcNow - published.Value).TotalDays);
        return Math.Exp(-days / 180.0);
    }

    private static double Norm(double value, double min, double max)
    {
        if (Math.Abs(max - min) < 1e-9)
            return 0.5;
        return (value - min) / (max - min);
    }

    private void SetProgress(string phase, string message, int current, int total)
    {
        coordinator.Progress.Phase = phase;
        coordinator.Progress.Message = message;
        coordinator.Progress.Current = current;
        coordinator.Progress.Total = total;
    }

    private sealed class CatalogLookup
    {
        private readonly Dictionary<int, string> _slugByYandexId = [];
        private readonly Dictionary<string, Category> _bySlug = new(StringComparer.Ordinal);
        private readonly HashSet<int> _tagIds = [];

        public static async Task<CatalogLookup> LoadAsync(AppDbContext db, CancellationToken ct)
        {
            var lookup = new CatalogLookup();
            foreach (var cat in await db.Categories.AsNoTracking().ToListAsync(ct))
            {
                lookup._bySlug[cat.Slug] = cat;
                if (cat.YandexId is int id)
                    lookup._slugByYandexId[id] = cat.Slug;
            }

            lookup._tagIds.UnionWith(await db.Tags.AsNoTracking().Select(t => t.Id).ToListAsync(ct));
            return lookup;
        }

        public string? EnsureCategory(AppDbContext db, int yandexId, string? slugHint)
        {
            if (_slugByYandexId.TryGetValue(yandexId, out var known))
                return known;

            var slug = slugHint?.Trim();
            if (string.IsNullOrWhiteSpace(slug) || slug.StartsWith("id-", StringComparison.OrdinalIgnoreCase))
                return null;
            if (_bySlug.TryGetValue(slug, out var existing))
            {
                if (existing.YandexId != yandexId)
                {
                    var tracked = db.Categories.Local.FirstOrDefault(c => c.Slug == slug)
                                  ?? db.Categories.Find(slug);
                    if (tracked is not null)
                    {
                        tracked.YandexId = yandexId;
                        if (string.IsNullOrWhiteSpace(tracked.Title) || tracked.Title.StartsWith("Категория ", StringComparison.Ordinal))
                            tracked.Title = CatalogConstants.TitleFor(tracked.Slug);
                        existing = tracked;
                    }
                    else
                    {
                        existing.YandexId = yandexId;
                    }
                }

                _slugByYandexId[yandexId] = existing.Slug;
                return existing.Slug;
            }

            var created = new Category
            {
                Slug = slug,
                YandexId = yandexId,
                Title = CatalogConstants.TitleFor(slug)
            };
            db.Categories.Add(created);
            _bySlug[slug] = created;
            _slugByYandexId[yandexId] = slug;
            return slug;
        }

        public void EnsureTag(AppDbContext db, int tagId)
        {
            if (!_tagIds.Add(tagId))
                return;
            if (db.Tags.Local.Any(t => t.Id == tagId))
                return;
            db.Tags.Add(new Tag
            {
                Id = tagId,
                Slug = $"tag-{tagId}",
                Title = $"тег {tagId}"
            });
        }
    }
}
