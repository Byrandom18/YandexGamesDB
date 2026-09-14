using Microsoft.EntityFrameworkCore;
using YandexGamesAnalytics.Catalog;
using YandexGamesAnalytics.Data;

namespace YandexGamesAnalytics.Services;

public class CatalogSyncService(
    IDbContextFactory<AppDbContext> dbFactory,
    YandexCatalogClient catalog,
    SyncCoordinator coordinator,
    ILogger<CatalogSyncService> logger)
{
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
        coordinator.Progress.LastMode = mode.ToString();

        try
        {
            SeedCategories(db);
            await db.SaveChangesAsync(ct);

            await SyncTagsAsync(db, ct);
            await SyncListingAsync(db, mode, ct);
            await SyncRanksAsync(db, mode, ct);
            await EnrichAsync(db, ct);
            await RecomputeDemandAsync(db, ct);

            run.FinishedAt = DateTime.UtcNow;
            run.Status = "ok";
            run.GamesCollected = coordinator.Progress.GamesCollected;
            run.GamesEnriched = coordinator.Progress.GamesEnriched;
            await db.SaveChangesAsync(CancellationToken.None);
            coordinator.Complete();
        }
        catch (OperationCanceledException)
        {
            run.FinishedAt = DateTime.UtcNow;
            run.Status = "cancelled";
            run.Error = "Остановлено пользователем";
            await db.SaveChangesAsync(CancellationToken.None);
            coordinator.Complete("Сбор остановлен.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalog sync failed");
            run.FinishedAt = DateTime.UtcNow;
            run.Status = "error";
            run.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
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
        await UpsertGamesAsync(db, first.Games, ct);

        for (var page = 2; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(160, ct);
            var batch = await catalog.GetAllGamesPageAsync(page, ct);
            await UpsertGamesAsync(db, batch.Games, ct);
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
        db.CategoryRankings.RemoveRange(db.CategoryRankings);
        await db.SaveChangesAsync(ct);

        await db.Games.Where(g => g.InNewFeed).ExecuteUpdateAsync(s => s.SetProperty(g => g.InNewFeed, false), ct);

        foreach (var slug in slugs)
        {
            var rank = 1;
            var ids = new List<int>();
            for (var page = 1; page <= pagesPerCategory; page++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(160, ct);
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
                await db.Games.Where(g => ids.Contains(g.AppId))
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.InNewFeed, true), ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task EnrichAsync(AppDbContext db, CancellationToken ct)
    {
        var ids = await db.Games.Select(g => g.AppId).ToListAsync(ct);
        const int batchSize = 40;
        var total = (int)Math.Ceiling(ids.Count / (double)batchSize);
        var enriched = 0;

        for (var i = 0; i < ids.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = ids.Skip(i).Take(batchSize).ToList();
            SetProgress("Карточки игр", $"Подробности {Math.Min(i + slice.Count, ids.Count)} из {ids.Count}", i / batchSize + 1, total);
            await Task.Delay(120, ct);
            var details = await catalog.GetGamesLongAsync(slice, ct);
            await UpsertGamesAsync(db, details, ct);
            enriched += details.Count;
            coordinator.Progress.GamesEnriched = enriched;
        }
    }

    private async Task UpsertGamesAsync(AppDbContext db, List<CatalogGameDto> dtos, CancellationToken ct)
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
                existing[dto.AppId] = game;
            }

            ApplyListing(game, dto, now);
            if (dto.Enriched)
                await ApplyEnrichmentAsync(db, game, dto, now, ct);
        }

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

    private async Task ApplyEnrichmentAsync(AppDbContext db, Game game, CatalogGameDto dto, DateTime now, CancellationToken ct)
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

        var oldCats = db.GameCategories.Local.Where(x => x.AppId == game.AppId).ToList();
        if (oldCats.Count == 0)
            oldCats = await db.GameCategories.Where(x => x.AppId == game.AppId).ToListAsync(ct);
        db.GameCategories.RemoveRange(oldCats);

        foreach (var (id, slugHint) in dto.Categories)
        {
            var slug = await EnsureCategoryAsync(db, id, slugHint, ct);
            if (db.GameCategories.Local.Any(x => x.AppId == game.AppId && x.CategorySlug == slug))
                continue;
            db.GameCategories.Add(new GameCategory { AppId = game.AppId, CategorySlug = slug });
        }

        var oldTags = db.GameTags.Local.Where(x => x.AppId == game.AppId).ToList();
        if (oldTags.Count == 0)
            oldTags = await db.GameTags.Where(x => x.AppId == game.AppId).ToListAsync(ct);
        db.GameTags.RemoveRange(oldTags);

        foreach (var tagId in dto.TagIds.Distinct())
        {
            await EnsureTagAsync(db, tagId, ct);
            if (db.GameTags.Local.Any(x => x.AppId == game.AppId && x.TagId == tagId))
                continue;
            db.GameTags.Add(new GameTag { AppId = game.AppId, TagId = tagId });
        }
    }

    private static async Task<string> EnsureCategoryAsync(AppDbContext db, int yandexId, string? slugHint, CancellationToken ct)
    {
        var slug = !string.IsNullOrWhiteSpace(slugHint) ? slugHint! : $"id-{yandexId}";
        var byId = await db.Categories.FirstOrDefaultAsync(c => c.YandexId == yandexId, ct);
        if (byId is not null)
        {
            if (!string.IsNullOrWhiteSpace(slugHint) && byId.Slug != slugHint)
            {
                // keep existing slug, just remember id
            }
            byId.YandexId = yandexId;
            if (string.IsNullOrWhiteSpace(byId.Title) || byId.Title.StartsWith("Категория ", StringComparison.Ordinal))
                byId.Title = CatalogConstants.TitleFor(byId.Slug);
            return byId.Slug;
        }

        var bySlug = await db.Categories.FindAsync([slug], ct);
        if (bySlug is not null)
        {
            bySlug.YandexId = yandexId;
            bySlug.Title = CatalogConstants.TitleFor(slug);
            return bySlug.Slug;
        }

        db.Categories.Add(new Category
        {
            Slug = slug,
            YandexId = yandexId,
            Title = CatalogConstants.TitleFor(slug)
        });
        await db.SaveChangesAsync(ct);
        return slug;
    }

    private static async Task EnsureTagAsync(AppDbContext db, int tagId, CancellationToken ct)
    {
        if (await db.Tags.FindAsync([tagId], ct) is not null)
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

    private static async Task RecomputeDemandAsync(AppDbContext db, CancellationToken ct)
    {
        var games = await db.Games.ToListAsync(ct);
        if (games.Count == 0)
            return;

        var gq = games.Select(g => (double)(g.GqRating ?? 0)).ToList();
        var reviews = games.Select(g => Math.Log(1 + g.RatingCount)).ToList();
        var rating = games.Select(g => g.Rating ?? 0).ToList();
        var recency = games.Select(g => Recency(g.FirstPublished)).ToList();

        for (var i = 0; i < games.Count; i++)
        {
            games[i].DemandScore =
                40 * Norm(gq[i], gq) +
                30 * Norm(reviews[i], reviews) +
                15 * Norm(rating[i], rating) +
                15 * Norm(recency[i], recency);
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

    private static double Norm(double value, List<double> all)
    {
        var min = all.Min();
        var max = all.Max();
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
}
