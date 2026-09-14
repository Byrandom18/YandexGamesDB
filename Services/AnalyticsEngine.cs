using Microsoft.EntityFrameworkCore;
using YandexGamesAnalytics.Catalog;
using YandexGamesAnalytics.Data;

namespace YandexGamesAnalytics.Services;

public class AnalyticsEngine(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<OverviewStats> GetLiteOverviewAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lastSync = await db.SyncRuns.AsNoTracking().OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        return new OverviewStats
        {
            Games = await db.Games.CountAsync(ct),
            Enriched = await db.Games.CountAsync(g => g.Enriched, ct),
            LastSync = lastSync?.FinishedAt?.ToLocalTime() ?? lastSync?.StartedAt.ToLocalTime(),
            LastMode = lastSync?.Mode
        };
    }

    public async Task<AnalyticsSnapshot> BuildAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var games = await db.Games.AsNoTracking()
            .Include(g => g.Categories).ThenInclude(c => c.Category)
            .Include(g => g.Tags).ThenInclude(t => t.Tag)
            .ToListAsync(ct);

        var rankings = await db.CategoryRankings.AsNoTracking().ToListAsync(ct);
        var lastSync = await db.SyncRuns.AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(ct);

        var snapshot = new AnalyticsSnapshot
        {
            Overview = BuildOverview(games, lastSync)
        };

        if (games.Count == 0)
            return snapshot;

        var now = DateTime.UtcNow;
        snapshot.Genres = BuildGenres(games, rankings, now);
        snapshot.Tags = BuildTags(games, now);
        snapshot.Factors = BuildFactors(games, now);
        snapshot.Correlations = BuildCorrelations(games);
        snapshot.Developers = BuildDevelopers(games);
        snapshot.TitleWords = TitleLanguageAnalyzer.Analyze(games, g => g.Title, 12);
        snapshot.DescriptionWords = TitleLanguageAnalyzer.Analyze(games, g => g.Description, 15);
        snapshot.GenrePairs = BuildGenrePairs(games, now);
        snapshot.TitleLengths = BuildTitleLengths(games);
        snapshot.TopDemand = MapGames(games.OrderByDescending(g => g.DemandScore).Take(12), snapshot.Genres);
        snapshot.TopGq = MapGames(games.OrderByDescending(g => g.GqRating ?? -1).Take(12), snapshot.Genres);
        snapshot.TopReviews = MapGames(games.OrderByDescending(g => g.RatingCount).Take(12), snapshot.Genres);
        snapshot.Newest = MapGames(games.Where(g => g.FirstPublished is not null).OrderByDescending(g => g.FirstPublished).Take(12), snapshot.Genres);
        snapshot.Playbook = BuildPlaybook(snapshot, games, now);
        snapshot.Insights = BuildInsights(snapshot, games);
        return snapshot;
    }

    public async Task<(List<GameListItem> Items, int Total)> SearchGamesAsync(
        string? query, string? category, string? sort, int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Games.AsNoTracking().Include(g => g.Categories).ThenInclude(c => c.Category).AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            q = q.Where(g => g.Title.Contains(term) || (g.DeveloperName != null && g.DeveloperName.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(g => g.Categories.Any(c => c.CategorySlug == category));

        q = sort switch
        {
            "gq" => q.OrderByDescending(g => g.GqRating),
            "rating" => q.OrderByDescending(g => g.Rating),
            "reviews" => q.OrderByDescending(g => g.RatingCount),
            "new" => q.OrderByDescending(g => g.FirstPublished),
            "title" => q.OrderBy(g => g.Title),
            _ => q.OrderByDescending(g => g.DemandScore)
        };

        var total = await q.CountAsync(ct);
        var items = await q.Skip(Math.Max(0, (page - 1) * pageSize)).Take(pageSize).ToListAsync(ct);
        return (MapGames(items, null), total);
    }

    public async Task<Game?> GetGameAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Games.AsNoTracking()
            .Include(g => g.Categories).ThenInclude(c => c.Category)
            .Include(g => g.Tags).ThenInclude(t => t.Tag)
            .FirstOrDefaultAsync(g => g.AppId == id, ct);
    }

    public async Task<List<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Categories.AsNoTracking().OrderBy(c => c.Title).ToListAsync(ct);
    }

    private static OverviewStats BuildOverview(List<Game> games, SyncRun? lastSync)
    {
        return new OverviewStats
        {
            Games = games.Count,
            Enriched = games.Count(g => g.Enriched),
            Categories = games.SelectMany(g => g.Categories).Select(c => c.CategorySlug).Distinct().Count(),
            Tags = games.SelectMany(g => g.Tags).Select(t => t.TagId).Distinct().Count(),
            Developers = games.Select(g => g.DeveloperName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count(),
            AvgRating = Avg(games.Where(g => g.Rating is not null).Select(g => g.Rating!.Value)),
            AvgGq = Avg(games.Where(g => g.GqRating is not null).Select(g => (double)g.GqRating!.Value)),
            MedianGq = Pct(games.Select(g => (double)(g.GqRating ?? 0)), 0.5),
            MedianReviews = Pct(games.Select(g => (double)g.RatingCount), 0.5),
            WithPurchases = games.Count(g => g.HasPurchases),
            WithVideo = games.Count(g => g.HasVideo),
            NewFeed = games.Count(g => g.InNewFeed),
            LastSync = lastSync?.FinishedAt?.ToLocalTime() ?? lastSync?.StartedAt.ToLocalTime(),
            LastMode = lastSync?.Mode
        };
    }

    private static List<GroupStats> BuildGenres(List<Game> games, List<CategoryRanking> rankings, DateTime now)
    {
        var topIds = rankings
            .Where(r => r.Rank <= 24)
            .GroupBy(r => r.CategorySlug)
            .ToDictionary(g => g.Key, g => g.Select(x => x.AppId).ToHashSet());

        var groups = games
            .SelectMany(g => g.Categories.Select(c => (Game: g, Cat: c)))
            .GroupBy(x => x.Cat.CategorySlug)
            .Select(g => ToGroup(
                g.Key,
                g.First().Cat.Category?.Title ?? CatalogConstants.TitleFor(g.Key),
                g.Select(x => x.Game).ToList(),
                topIds.TryGetValue(g.Key, out var set) ? set : [],
                now))
            .ToList();

        ScoreOpportunity(groups);
        return groups.OrderByDescending(x => x.OpportunityScore).ThenByDescending(x => x.MedianGq).ToList();
    }

    private static List<GroupStats> BuildTags(List<Game> games, DateTime now)
    {
        return games
            .SelectMany(g => g.Tags.Select(t => (Game: g, Tag: t.Tag)))
            .Where(x => x.Tag is not null)
            .GroupBy(x => x.Tag!.Id)
            .Select(g => ToGroup(
                g.Key.ToString(),
                g.First().Tag!.Title,
                g.Select(x => x.Game).ToList(),
                [],
                now))
            .Where(x => x.Games >= 15)
            .OrderByDescending(x => x.MedianDemand)
            .ThenByDescending(x => x.MedianGq)
            .ToList();
    }

    private static GroupStats ToGroup(string key, string title, List<Game> games, HashSet<int> top, DateTime now)
    {
        var gq = games.Select(g => (double)(g.GqRating ?? 0)).ToList();
        var reviews = games.Select(g => (double)g.RatingCount).ToList();
        var demand = games.Select(g => g.DemandScore).ToList();
        var ratings = games.Where(g => g.Rating is not null).Select(g => g.Rating!.Value).ToList();
        var fresh = games.Where(g => IsFresh(g, now)).ToList();
        var iap = games.Where(g => g.HasPurchases).ToList();

        return new GroupStats
        {
            Key = key,
            Title = title,
            Games = games.Count,
            AvgGq = Avg(gq),
            AvgRating = Avg(ratings),
            AvgReviews = Avg(reviews),
            AvgDemand = Avg(demand),
            MedianGq = Pct(gq, 0.5),
            MedianDemand = Pct(demand, 0.5),
            MedianRating = Pct(ratings, 0.5),
            MedianReviews = Pct(reviews, 0.5),
            P25Gq = Pct(gq, 0.25),
            P75Gq = Pct(gq, 0.75),
            P25Reviews = Pct(reviews, 0.25),
            P75Reviews = Pct(reviews, 0.75),
            ShareGq50 = SharePct(games, g => (g.GqRating ?? 0) >= 50),
            ShareGq70 = SharePct(games, g => (g.GqRating ?? 0) >= 70),
            FreshGames = fresh.Count,
            FreshMedianGq = Pct(fresh.Select(g => (double)(g.GqRating ?? 0)), 0.5),
            FreshMedianDemand = Pct(fresh.Select(g => g.DemandScore), 0.5),
            OneStarShare = OneStarShare(games),
            SoftAgeShare = SharePct(games, g => g.AgeRating is "0+" or "6+"),
            DualPlatformShare = SharePct(games, g => g.SupportsMobile && g.SupportsDesktop),
            IapMedianReviews = Pct(iap.Select(g => (double)g.RatingCount), 0.5),
            WithPurchases = iap.Count,
            WithVideo = games.Count(g => g.HasVideo),
            InTop = top.Count == 0 ? 0 : games.Count(g => top.Contains(g.AppId))
        };
    }

    private static void ScoreOpportunity(List<GroupStats> genres)
    {
        var eligible = genres.Where(g => g.Games >= 8).ToList();
        if (eligible.Count == 0)
        {
            foreach (var g in genres)
            {
                g.Verdict = "мало данных";
                g.Quadrant = "болото";
            }
            return;
        }

        var gqMin = eligible.Min(g => g.MedianGq);
        var gqMax = eligible.Max(g => g.MedianGq);
        var reviewMin = eligible.Min(g => Math.Log(1 + g.MedianReviews));
        var reviewMax = eligible.Max(g => Math.Log(1 + g.MedianReviews));
        var freshMin = eligible.Min(FreshScore);
        var freshMax = eligible.Max(FreshScore);
        var moneyMin = eligible.Min(MoneyScore);
        var moneyMax = eligible.Max(MoneyScore);
        var compMin = eligible.Min(g => Math.Log(1 + g.Games));
        var compMax = eligible.Max(g => Math.Log(1 + g.Games));
        var easeMin = eligible.Min(EaseScore);
        var easeMax = eligible.Max(EaseScore);

        var attract = new Dictionary<string, double>();
        foreach (var g in genres)
        {
            if (g.Games < 8)
            {
                g.OpportunityScore = 0;
                g.Verdict = "мало данных";
                g.Quadrant = "болото";
                continue;
            }

            var success = 0.48 * Norm(g.MedianGq, gqMin, gqMax)
                          + 0.18 * Norm(Math.Log(1 + g.MedianReviews), reviewMin, reviewMax);
            var fresh = 0.18 * Norm(FreshScore(g), freshMin, freshMax);
            var money = 0.10 * Norm(MoneyScore(g), moneyMin, moneyMax);
            var ease = 0.06 * Norm(EaseScore(g), easeMin, easeMax);
            var pull = success + fresh + money + ease;
            var competition = Norm(Math.Log(1 + g.Games), compMin, compMax);
            var denom = 0.38 + 0.62 * competition;
            g.OpportunityScore = 100.0 * pull / denom;
            attract[g.Key] = pull;
        }

        var scored = genres.Where(g => g.Games >= 8).ToList();
        var medianAttract = Pct(scored.Select(g => attract[g.Key]), 0.5);
        var medianGames = Pct(scored.Select(g => (double)g.Games), 0.5);
        foreach (var g in scored)
        {
            var highPull = attract[g.Key] >= medianAttract;
            var highComp = g.Games >= medianGames;
            var freshWeak = g.FreshGames >= 5 && g.FreshMedianGq < g.MedianGq - 8;

            g.Quadrant = (highPull, highComp, freshWeak) switch
            {
                (true, false, _) => "ниша",
                (true, true, true) => "перегрев",
                (true, true, false) => "звезда",
                _ => "болото"
            };

            g.Verdict = BuildVerdict(g);
        }
    }

    private static string BuildVerdict(GroupStats g)
    {
        if (g.PurchaseShare >= 40 && g.MedianGq < 55)
            return "много инапов, средний GQ";
        if (g.Quadrant == "ниша" && g.FreshGames >= 5 && g.FreshMedianGq >= g.MedianGq - 5)
            return "ниша, новички заходят";
        if (g.Quadrant == "перегрев")
            return "высокий спрос, мало места";
        if (g.Quadrant == "звезда")
            return "типичный успех при высокой конкуренции";
        if (g.Quadrant == "ниша")
            return "ниша: сильнее типичная игра, меньше тайтлов";
        return "слабый типичный результат при насыщенности";
    }

    private static double FreshScore(GroupStats g) =>
        g.FreshGames >= 5 ? g.FreshMedianGq : g.MedianGq * 0.82;

    private static double MoneyScore(GroupStats g) =>
        (g.PurchaseShare / 100.0) * Math.Log(1 + Math.Max(g.IapMedianReviews, g.MedianReviews));

    private static double EaseScore(GroupStats g) =>
        0.55 * (g.SoftAgeShare / 100.0) + 0.45 * (g.DualPlatformShare / 100.0);

    private static List<FactorEffect> BuildFactors(List<Game> games, DateTime now)
    {
        return new List<FactorEffect>
        {
            Compare("Видео на карточке", games, g => g.HasVideo, "Есть видео", "Нет видео"),
            Compare("Инап-покупки", games, g => g.HasPurchases, "Есть покупки", "Без покупок"),
            Compare("Облачные сохранения", games, g => g.CloudSave, "Cloud Save", "Без облака"),
            Compare("Лидерборды", games, g => g.HasLeaderboards, "Есть", "Нет"),
            Compare("Несколько жанров", games, g => g.CategoryCount >= 2, "2 жанра", "1 жанр"),
            Compare("Мультиязычность", games, g => g.LanguageCount >= 2, "2+ языка", "1 язык"),
            Compare("Мобильные + ПК", games, g => g.SupportsMobile && g.SupportsDesktop, "Обе платформы", "Одна платформа"),
            Compare("Свежая игра", games, g => g.FirstPublished is not null && (now - g.FirstPublished.Value).TotalDays <= 60, "До 60 дней", "Старше"),
            Compare("Зрелая игра", games, g => g.FirstPublished is not null && (now - g.FirstPublished.Value).TotalDays >= 365, "Старше года", "Моложе года"),
            Compare("Мягкий возраст", games, g => g.AgeRating is "0+" or "6+", "0+/6+", "12+ и выше"),
            Compare("Быстрая загрузка", games.Where(g => g.MinLoadTime is not null).ToList(), g => g.MinLoadTime < 8, "< 8 сек", "8+ сек"),
            Compare("Много скриншотов", games, g => g.ScreenshotCount >= 4, "4+ скрина", "Меньше 4")
        }.Where(f => f.WithCount >= 8 && f.WithoutCount >= 8).ToList();
    }

    private static FactorEffect Compare(string name, List<Game> games, Func<Game, bool> pred, string with, string without)
    {
        var a = games.Where(pred).ToList();
        var b = games.Where(g => !pred(g)).ToList();
        return new FactorEffect
        {
            Name = name,
            WithLabel = with,
            WithoutLabel = without,
            WithCount = a.Count,
            WithoutCount = b.Count,
            WithAvgGq = Avg(a.Select(g => (double)(g.GqRating ?? 0))),
            WithoutAvgGq = Avg(b.Select(g => (double)(g.GqRating ?? 0))),
            WithMedianGq = Pct(a.Select(g => (double)(g.GqRating ?? 0)), 0.5),
            WithoutMedianGq = Pct(b.Select(g => (double)(g.GqRating ?? 0)), 0.5),
            WithAvgReviews = Avg(a.Select(g => (double)g.RatingCount)),
            WithoutAvgReviews = Avg(b.Select(g => (double)g.RatingCount)),
            WithMedianReviews = Pct(a.Select(g => (double)g.RatingCount), 0.5),
            WithoutMedianReviews = Pct(b.Select(g => (double)g.RatingCount), 0.5),
            WithAvgRating = Avg(a.Where(g => g.Rating is not null).Select(g => g.Rating!.Value)),
            WithoutAvgRating = Avg(b.Where(g => g.Rating is not null).Select(g => g.Rating!.Value))
        };
    }

    private static List<CorrelationRow> BuildCorrelations(List<Game> games)
    {
        var rows = new List<CorrelationRow>
        {
            Corr(games, "Рейтинг платформы (GQ)", "Оценки игроков", g => g.GqRating ?? 0, g => g.Rating ?? 0),
            Corr(games, "Рейтинг платформы (GQ)", "Число отзывов", g => g.GqRating ?? 0, g => Math.Log(1 + g.RatingCount)),
            Corr(games, "Рейтинг платформы (GQ)", "Возраст игры, дни", g => g.GqRating ?? 0, g => g.FirstPublished is null ? 0 : (DateTime.UtcNow - g.FirstPublished.Value).TotalDays),
            Corr(games, "Рейтинг платформы (GQ)", "Время загрузки", g => g.GqRating ?? 0, g => g.MinLoadTime ?? 0, g => g.MinLoadTime is not null),
            Corr(games, "Рейтинг платформы (GQ)", "Число тегов", g => g.GqRating ?? 0, g => g.TagCount),
            Corr(games, "Число отзывов", "Возраст игры, дни", g => Math.Log(1 + g.RatingCount), g => g.FirstPublished is null ? 0 : (DateTime.UtcNow - g.FirstPublished.Value).TotalDays),
            Corr(games, "Оценки игроков", "Доля 5★", g => g.Rating ?? 0, Share5)
        };
        return rows.Where(r => r.N >= 20).OrderByDescending(r => Math.Abs(r.Pearson)).ToList();
    }

    private static double Share5(Game g)
    {
        var total = (g.Score1 ?? 0) + (g.Score2 ?? 0) + (g.Score3 ?? 0) + (g.Score4 ?? 0) + (g.Score5 ?? 0);
        return total == 0 ? 0 : 100.0 * (g.Score5 ?? 0) / total;
    }

    private static CorrelationRow Corr(
        List<Game> games, string feature, string target,
        Func<Game, double> x, Func<Game, double> y, Func<Game, bool>? filter = null)
    {
        var subset = filter is null ? games : games.Where(filter).ToList();
        var xs = subset.Select(x).ToList();
        var ys = subset.Select(y).ToList();
        return new CorrelationRow
        {
            Feature = feature,
            Target = target,
            Pearson = Pearson(xs, ys),
            N = subset.Count
        };
    }

    private static List<DeveloperStats> BuildDevelopers(List<Game> games)
    {
        return games
            .Where(g => !string.IsNullOrWhiteSpace(g.DeveloperName))
            .GroupBy(g => g.DeveloperName!)
            .Select(g => new DeveloperStats
            {
                DeveloperId = g.First().DeveloperId,
                Name = g.Key,
                Games = g.Count(),
                AvgGq = Avg(g.Select(x => (double)(x.GqRating ?? 0))),
                AvgDemand = Avg(g.Select(x => x.DemandScore)),
                TotalReviews = g.Sum(x => x.RatingCount)
            })
            .OrderByDescending(x => x.AvgDemand)
            .Take(40)
            .ToList();
    }

    private static List<GenrePairStats> BuildGenrePairs(List<Game> games, DateTime now)
    {
        var buckets = new Dictionary<(string Left, string Right), (string LeftTitle, string RightTitle, List<Game> Games)>();
        foreach (var game in games)
        {
            var cats = game.Categories
                .Select(c => (Slug: c.CategorySlug, Title: c.Category?.Title ?? CatalogConstants.TitleFor(c.CategorySlug)))
                .DistinctBy(c => c.Slug)
                .OrderBy(c => c.Slug, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < cats.Count; i++)
            for (var j = i + 1; j < cats.Count; j++)
            {
                var key = (cats[i].Slug, cats[j].Slug);
                if (!buckets.TryGetValue(key, out var bucket))
                    bucket = (cats[i].Title, cats[j].Title, []);
                bucket.Games.Add(game);
                buckets[key] = bucket;
            }
        }

        return buckets
            .Where(kv => kv.Value.Games.Count >= 12)
            .Select(kv => ToPair(kv.Key.Left, kv.Key.Right, kv.Value.LeftTitle + " + " + kv.Value.RightTitle, kv.Value.Games, now))
            .OrderByDescending(x => x.MedianDemand)
            .ThenByDescending(x => x.MedianGq)
            .Take(24)
            .ToList();
    }

    private static List<GenrePairStats> BuildGenreTagCombos(List<Game> games, DateTime now)
    {
        var buckets = new Dictionary<(string Genre, int Tag), (string GenreTitle, string TagTitle, List<Game> Games)>();
        foreach (var game in games)
        {
            foreach (var cat in game.Categories)
            {
                var genreTitle = cat.Category?.Title ?? CatalogConstants.TitleFor(cat.CategorySlug);
                foreach (var tagLink in game.Tags)
                {
                    var tag = tagLink.Tag;
                    if (tag is null || IsGenericTag(tag.Title))
                        continue;

                    var key = (cat.CategorySlug, tag.Id);
                    if (!buckets.TryGetValue(key, out var bucket))
                        bucket = (genreTitle, tag.Title, []);
                    bucket.Games.Add(game);
                    buckets[key] = bucket;
                }
            }
        }

        return buckets
            .Where(kv => kv.Value.Games.Count >= 20)
            .Select(kv => ToPair(kv.Key.Genre, kv.Key.Tag.ToString(), kv.Value.GenreTitle + " × " + kv.Value.TagTitle, kv.Value.Games, now))
            .OrderByDescending(x => x.MedianDemand)
            .ThenByDescending(x => x.MedianGq)
            .Take(16)
            .ToList();
    }

    private static GenrePairStats ToPair(string left, string right, string title, List<Game> games, DateTime now)
    {
        _ = now;
        return new GenrePairStats
        {
            LeftKey = left,
            RightKey = right,
            Title = title,
            Games = games.Count,
            MedianGq = Pct(games.Select(g => (double)(g.GqRating ?? 0)), 0.5),
            MedianDemand = Pct(games.Select(g => g.DemandScore), 0.5),
            MedianReviews = Pct(games.Select(g => (double)g.RatingCount), 0.5),
            PurchaseShare = SharePct(games, g => g.HasPurchases)
        };
    }

    private static List<TitleLengthBucket> BuildTitleLengths(List<Game> games)
    {
        return new (string Label, Func<Game, bool> Pred)[]
        {
            ("Короткое (≤12)", g => g.Title.Trim().Length <= 12),
            ("Среднее (13–24)", g => { var n = g.Title.Trim().Length; return n is >= 13 and <= 24; }),
            ("Длинное (25+)", g => g.Title.Trim().Length >= 25)
        }
        .Select(b =>
        {
            var slice = games.Where(b.Pred).ToList();
            return new TitleLengthBucket
            {
                Label = b.Label,
                Games = slice.Count,
                MedianGq = Pct(slice.Select(g => (double)(g.GqRating ?? 0)), 0.5),
                MedianReviews = Pct(slice.Select(g => (double)g.RatingCount), 0.5),
                MedianDemand = Pct(slice.Select(g => g.DemandScore), 0.5)
            };
        })
        .Where(b => b.Games >= 8)
        .ToList();
    }

    private static PlaybookReport BuildPlaybook(AnalyticsSnapshot snapshot, List<Game> games, DateTime now)
    {
        var genres = snapshot.Genres.Where(g => g.Games >= 10).OrderByDescending(g => g.OpportunityScore).ToList();
        var topTake = Math.Max(1, games.Count / 4);
        var top = games.OrderByDescending(g => g.DemandScore).Take(topTake).ToList();

        var report = new PlaybookReport
        {
            TopGenres = genres.Take(5).ToList(),
            Combos = snapshot.GenrePairs.Take(8).ToList(),
            GenreTagCombos = BuildGenreTagCombos(games, now).Take(8).ToList(),
            Ages = BuildAudience(top, g => string.IsNullOrWhiteSpace(g.AgeRating) ? "без рейтинга" : g.AgeRating!),
            Platforms = BuildPlatformAudience(top),
            Languages = BuildLanguageAudience(top),
            EasyWins = snapshot.Factors
                .Where(f => f.WithCount >= 20 && f.WithoutCount >= 20)
                .OrderByDescending(f => f.MedianGqDelta)
                .Take(6)
                .ToList(),
            Monetization = genres
                .Where(g => g.PurchaseShare >= 20)
                .OrderByDescending(g => g.PurchaseShare * Math.Log(1 + Math.Max(g.IapMedianReviews, 1)))
                .Take(5)
                .ToList(),
            Engagement = genres
                .Where(g => g.MedianGq >= 15)
                .OrderBy(g => g.PurchaseShare)
                .ThenByDescending(g => g.MedianGq)
                .Take(5)
                .ToList(),
            TitleWords = snapshot.TitleWords.Take(16).ToList(),
            TitleLengths = snapshot.TitleLengths,
            RiskyGenres = genres.Where(g => g.OneStarShare >= 6).OrderByDescending(g => g.OneStarShare).Take(5).ToList()
        };

        report.Checklist = BuildChecklist(snapshot, report);
        report.Insights = BuildPlaybookInsights(snapshot, report, games, top);
        return report;
    }

    private static List<AudienceSlice> BuildAudience(List<Game> games, Func<Game, string> key)
    {
        return games
            .GroupBy(key)
            .Select(g => new AudienceSlice
            {
                Label = g.Key,
                Games = g.Count(),
                Share = games.Count == 0 ? 0 : 100.0 * g.Count() / games.Count,
                MedianGq = Pct(g.Select(x => (double)(x.GqRating ?? 0)), 0.5)
            })
            .OrderByDescending(x => x.Games)
            .Take(8)
            .ToList();
    }

    private static List<AudienceSlice> BuildPlatformAudience(List<Game> games)
    {
        (string Label, Func<Game, bool> Pred)[] rows =
        [
            ("Мобильные", g => g.SupportsMobile),
            ("ПК", g => g.SupportsDesktop),
            ("ТВ", g => g.SupportsTv),
            ("Мобильные + ПК", g => g.SupportsMobile && g.SupportsDesktop)
        ];

        return rows.Select(r =>
        {
            var slice = games.Where(r.Pred).ToList();
            return new AudienceSlice
            {
                Label = r.Label,
                Games = slice.Count,
                Share = games.Count == 0 ? 0 : 100.0 * slice.Count / games.Count,
                MedianGq = Pct(slice.Select(g => (double)(g.GqRating ?? 0)), 0.5)
            };
        }).ToList();
    }

    private static List<AudienceSlice> BuildLanguageAudience(List<Game> games)
    {
        (string Label, Func<Game, bool> Pred)[] rows =
        [
            ("ru + en", g => HasLang(g, "ru") && HasLang(g, "en")),
            ("только ru", g => HasLang(g, "ru") && !HasLang(g, "en")),
            ("только en", g => HasLang(g, "en") && !HasLang(g, "ru")),
            ("другие наборы", g => !(HasLang(g, "ru") || HasLang(g, "en")) && g.LanguageCount > 0)
        ];

        return rows.Select(r =>
        {
            var slice = games.Where(r.Pred).ToList();
            return new AudienceSlice
            {
                Label = r.Label,
                Games = slice.Count,
                Share = games.Count == 0 ? 0 : 100.0 * slice.Count / games.Count,
                MedianGq = Pct(slice.Select(g => (double)(g.GqRating ?? 0)), 0.5)
            };
        }).Where(x => x.Games > 0).ToList();
    }

    private static bool HasLang(Game g, string code)
    {
        if (string.IsNullOrWhiteSpace(g.Languages))
            return false;
        return g.Languages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(x => x.Equals(code, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ChecklistItem> BuildChecklist(AnalyticsSnapshot snapshot, PlaybookReport report)
    {
        FactorEffect? Find(string part) =>
            snapshot.Factors.FirstOrDefault(f => f.Name.Contains(part, StringComparison.OrdinalIgnoreCase));

        var video = Find("Видео");
        var langs = Find("Мультиязычность");
        var cloud = Find("Облачные");
        var shots = Find("скриншот");
        var age = Find("возраст");
        var load = Find("загрузка");
        var bestTitle = report.TitleLengths.OrderByDescending(t => t.MedianGq).FirstOrDefault();
        var titleAdvice = bestTitle?.Label switch
        {
            null => "Длина названия",
            var s when s.Contains("Корот", StringComparison.Ordinal) => "Короткое название",
            var s when s.Contains("Длинн", StringComparison.Ordinal) => "Длинное название",
            _ => "Название средней длины"
        };

        return
        [
            Item("Видео на карточке", video, "ролик на витрине"),
            Item("Два языка (ru + en)", langs, "карточка не только на одном языке"),
            Item("Облачные сохранения", cloud, "если прогресс имеет смысл"),
            Item("4+ скриншота", shots, "витрина без пустых слотов"),
            Item("Возраст 0+ или 6+", age, "шире охват магазина"),
            new ChecklistItem
            {
                Title = titleAdvice,
                Detail = bestTitle is null
                    ? "Длину названия сравнить после полного сбора."
                    : $"В выборке лучше медиана GQ у «{bestTitle.Label}»: {bestTitle.MedianGq:0.0} ({bestTitle.Games} игр).",
                Recommended = bestTitle is not null
            },
            new ChecklistItem
            {
                Title = "Загрузка быстрее 8 секунд",
                Detail = load is null
                    ? "Мало данных по времени загрузки."
                    : $"Медиана GQ {load.WithMedianGq:0.0} vs {load.WithoutMedianGq:0.0} (Δ {Signed(load.MedianGqDelta)}).",
                Recommended = load is not null && load.MedianGqDelta > 0
            }
        ];

        static ChecklistItem Item(string title, FactorEffect? factor, string hint)
        {
            if (factor is null)
            {
                return new ChecklistItem { Title = title, Detail = "Недостаточно игр для сравнения.", Recommended = false };
            }

            return new ChecklistItem
            {
                Title = title,
                Detail = $"{hint}. Медиана GQ {factor.WithMedianGq:0.0} vs {factor.WithoutMedianGq:0.0} (Δ {Signed(factor.MedianGqDelta)}, n={factor.WithCount}/{factor.WithoutCount}).",
                Recommended = factor.MedianGqDelta >= 1
            };
        }
    }

    private static List<Insight> BuildPlaybookInsights(
        AnalyticsSnapshot snapshot, PlaybookReport report, List<Game> games, List<Game> topQuartile)
    {
        var insights = new List<Insight>();
        var sampleNote = snapshot.Overview.LastMode is "sample" or "Sample"
            ? " Сейчас в базе пробный сбор: старые ID с all-games завышают пасьянсы и словесные игры. Перед решением лучше полный каталог."
            : $" Выборка: {snapshot.Overview.Games} игр. Это корреляция по открытому каталогу, не ARPDAU и не A/B витрины.";

        var lead = report.TopGenres.FirstOrDefault();
        if (lead is not null)
        {
            insights.Add(new Insight
            {
                Title = "Что делать",
                Kind = "ok",
                Body = $"По индексу перспективы лидирует «{lead.Title}»: медиана GQ {lead.MedianGq:0.0}, медиана отзывов {lead.MedianReviews:0}, " +
                       $"новички (≤12 мес., {lead.FreshGames} игр) — медиана GQ {lead.FreshMedianGq:0.0}. Вердикт: {lead.Verdict}. " +
                       $"Шанс не провалиться: GQ≥50 у {lead.ShareGq50:0}%, GQ≥70 у {lead.ShareGq70:0}%.{sampleNote}"
            });
        }

        var combo = report.GenreTagCombos.FirstOrDefault() ?? report.Combos.FirstOrDefault();
        if (combo is not null)
        {
            insights.Add(new Insight
            {
                Title = "Рабочая связка",
                Kind = "ok",
                Body = $"Связка «{combo.Title}»: {combo.Games} игр, медиана GQ {combo.MedianGq:0.0}, медиана отзывов {combo.MedianReviews:0}. " +
                       "Два жанра или жанр×тег часто сильнее одиночной метки — смотрите таблицу ниже, минимум 12–20 игр в ячейке."
            });
        }

        var age = report.Ages.OrderByDescending(a => a.Share).FirstOrDefault();
        var lang = report.Languages.OrderByDescending(a => a.Share).FirstOrDefault();
        var platform = report.Platforms.FirstOrDefault(p => p.Label == "Мобильные + ПК") ?? report.Platforms.OrderByDescending(p => p.Share).FirstOrDefault();
        if (age is not null)
        {
            insights.Add(new Insight
            {
                Title = "На какую аудиторию",
                Kind = "info",
                Body = $"В верхнем квартиле спроса (n={topQuartile.Count}) чаще всего возраст {age.Label} ({age.Share:0}% карточек, медиана GQ {age.MedianGq:0.0}). " +
                       (platform is null ? "" : $"Платформы: {platform.Label} — {platform.Share:0}%. ") +
                       (lang is null ? "" : $"Языки: {lang.Label} — {lang.Share:0}%, медиана GQ {lang.MedianGq:0.0}.")
            });
        }

        var win = report.EasyWins.FirstOrDefault(f => f.MedianGqDelta > 0);
        if (win is not null)
        {
            insights.Add(new Insight
            {
                Title = "Что проще усилить на карточке",
                Kind = "ok",
                Body = $"По медиане, а не по среднему (хиты вроде гигантского хвоста отзывов не должны решать): «{win.Name}» даёт Δ мед. GQ {Signed(win.MedianGqDelta)} " +
                       $"({win.WithLabel} {win.WithMedianGq:0.0} vs {win.WithoutLabel} {win.WithoutMedianGq:0.0}). Среднее по этому фактору {Signed(win.GqDelta)} — его легко раздувает хвост."
            });
        }

        var money = report.Monetization.FirstOrDefault();
        var ads = report.Engagement.FirstOrDefault();
        if (money is not null || ads is not null)
        {
            insights.Add(new Insight
            {
                Title = "Что прибыльнее — только прокси",
                Kind = "warn",
                Body = "Доход, DAU и retention консоль не отдаёт по чужим играм. Прокси: отзывы ≈ охват, GQ ≈ вовлечённость, доля инапов ≈ готовность платить. " +
                       (money is null ? "" : $"Покупки: «{money.Title}» — {money.PurchaseShare:0}% инапов, медиана отзывов у игр с покупками {money.IapMedianReviews:0}. ") +
                       (ads is null ? "" : $"Без покупок, но с GQ: «{ads.Title}» — медиана GQ {ads.MedianGq:0.0}, инапы {ads.PurchaseShare:0}% (скорее рекламная вовлечённость). ") +
                       "Это не ARPDAU."
            });
        }

        var word = report.TitleWords.FirstOrDefault();
        var risk = report.RiskyGenres.FirstOrDefault();
        var load = snapshot.Factors.FirstOrDefault(f => f.Name.Contains("загрузка", StringComparison.OrdinalIgnoreCase));
        insights.Add(new Insight
        {
            Title = "Язык карточки и риски",
            Kind = "info",
            Body = (word is null
                       ? "Слов в названиях пока мало для устойчивого lift (нужно ≥12 игр на токен)."
                       : $"В названиях топ-квартиля спроса чаще встречается «{word.Phrase}»: lift {word.Lift:0.00}, медиана GQ {word.MedianGq:0.0}, {word.Games} игр. CTR иконки Яндекс не отдаёт — это корреляция успешных карточек.") +
                   (risk is null ? "" : $" Доля 1★ выше в «{risk.Title}» ({risk.OneStarShare:0.0}%) — токсичнее отзывы.") +
                   (load is null || load.MedianGqDelta >= 0 ? "" : " Долгая загрузка связана с более слабой медианой GQ.") +
                   " Служебные теги вроде «бесплатные» в выводах игнорируются."
        });

        return insights;
    }

    private static List<Insight> BuildInsights(AnalyticsSnapshot snapshot, List<Game> games)
    {
        var insights = new List<Insight>();
        var genres = snapshot.Genres.Where(g => g.Games >= 10).ToList();
        if (genres.Count > 0)
        {
            var top = genres.OrderByDescending(g => g.OpportunityScore).First();
            var byVolume = genres.OrderByDescending(g => g.Games).First();
            insights.Add(new Insight
            {
                Title = "Самый перспективный жанр",
                Kind = "ok",
                Body = $"{top.Title}: индекс {top.OpportunityScore:0.0}, медиана GQ {top.MedianGq:0.0} (среднее {top.AvgGq:0.0} врёт на хитах), " +
                       $"медиана отзывов {top.MedianReviews:0}, {top.Games} игр. {top.Verdict} " +
                       $"По числу тайтлов лидирует «{byVolume.Title}» ({byVolume.Games})."
            });
        }

        var tags = snapshot.Tags.Where(t => t.Games >= 25 && !IsGenericTag(t.Title)).Take(3).ToList();
        if (tags.Count > 0)
        {
            insights.Add(new Insight
            {
                Title = "Теги с наибольшим спросом",
                Kind = "ok",
                Body = string.Join(" · ", tags.Select(t => $"{t.Title} (мед. GQ {t.MedianGq:0.0}, {t.Games} игр)"))
            });
        }

        var strongest = snapshot.Factors.OrderByDescending(f => Math.Abs(f.MedianGqDelta)).FirstOrDefault();
        if (strongest is not null)
        {
            var direction = strongest.MedianGqDelta >= 0 ? "выше" : "ниже";
            insights.Add(new Insight
            {
                Title = "Что сильнее всего связано с медианой GQ",
                Kind = strongest.MedianGqDelta >= 0 ? "ok" : "warn",
                Body = $"«{strongest.Name}»: у группы «{strongest.WithLabel}» медиана GQ на {Math.Abs(strongest.MedianGqDelta):0.1} пункта {direction} " +
                       $"(среднее Δ {Signed(strongest.GqDelta)}). Хвост хитов больше не решает сравнение."
            });
        }

        var video = snapshot.Factors.FirstOrDefault(f => f.Name.Contains("Видео", StringComparison.OrdinalIgnoreCase));
        var iap = snapshot.Factors.FirstOrDefault(f => f.Name.Contains("Инап", StringComparison.OrdinalIgnoreCase));
        if (video is not null && iap is not null)
        {
            insights.Add(new Insight
            {
                Title = "Карточка и монетизация",
                Kind = "info",
                Body = $"Видео: Δ мед. GQ {Signed(video.MedianGqDelta)}. Покупки: Δ мед. GQ {Signed(iap.MedianGqDelta)}, " +
                       $"медиана отзывов {iap.WithMedianReviews:0} vs {iap.WithoutMedianReviews:0}. " +
                       "Инапы часто идут вместе с более «тяжёлыми» проектами — смотрите срез по жанру."
            });
        }

        var corr = snapshot.Correlations.FirstOrDefault();
        if (corr is not null)
        {
            insights.Add(new Insight
            {
                Title = "Самая сильная корреляция",
                Kind = "info",
                Body = $"{corr.Feature} ↔ {corr.Target}: r = {corr.Pearson:0.00} (n={corr.N}). " +
                       DescribeCorr(corr.Pearson)
            });
        }

        var young = games.Count(g => g.FirstPublished is not null && (DateTime.UtcNow - g.FirstPublished.Value).TotalDays <= 30);
        insights.Add(new Insight
        {
            Title = "Как читать метрики",
            Kind = "info",
            Body = "GQ (0–100) — рейтинг вовлечённости Яндекс Игр. Оценка игроков и число отзывов — публичные. " +
                   "DAU, retention и доход консоль отдаёт только по своим играм. Смотрите медианы и вкладку «Для разработчика». " +
                   $"В текущей выборке новых (до 30 дней): {young}."
        });

        return insights;
    }

    public static bool IsGenericTag(string title)
    {
        string[] generic =
        [
            "бесплатные", "браузерные", "без скачивания", "мобильные", "без регистрации", "десктоп",
            "для компьютера", "high quality", "с оценками игроков"
        ];
        return generic.Any(g => title.Contains(g, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeCorr(double r)
    {
        var a = Math.Abs(r);
        if (a >= 0.7) return "Связь сильная.";
        if (a >= 0.4) return "Связь умеренная.";
        if (a >= 0.2) return "Связь слабая, но заметная.";
        return "Связь почти отсутствует.";
    }

    private static List<GameListItem> MapGames(IEnumerable<Game> games, List<GroupStats>? _)
    {
        return games.Select(g => new GameListItem
        {
            AppId = g.AppId,
            Title = g.Title,
            DeveloperName = g.DeveloperName,
            IconUrl = MediaUrl(g.IconUrl, "orig"),
            Rating = g.Rating,
            RatingCount = g.RatingCount,
            GqRating = g.GqRating,
            DemandScore = g.DemandScore,
            FirstPublished = g.FirstPublished,
            HasPurchases = g.HasPurchases,
            HasVideo = g.HasVideo,
            AgeRating = g.AgeRating,
            Categories = string.Join(", ", g.Categories.Select(c => c.Category?.Title ?? CatalogConstants.TitleFor(c.CategorySlug)))
        }).ToList();
    }

    public static string? MediaUrl(string? prefix, string size)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;
        return prefix.TrimEnd('/') + "/" + size;
    }

    private static bool IsFresh(Game g, DateTime now) =>
        g.FirstPublished is not null && (now - g.FirstPublished.Value).TotalDays <= 365;

    private static double SharePct(IReadOnlyCollection<Game> games, Func<Game, bool> pred) =>
        games.Count == 0 ? 0 : 100.0 * games.Count(pred) / games.Count;

    private static double OneStarShare(IEnumerable<Game> games)
    {
        long ones = 0, total = 0;
        foreach (var g in games)
        {
            ones += g.Score1 ?? 0;
            total += (g.Score1 ?? 0) + (g.Score2 ?? 0) + (g.Score3 ?? 0) + (g.Score4 ?? 0) + (g.Score5 ?? 0);
        }

        return total == 0 ? 0 : 100.0 * ones / total;
    }

    private static double Avg(IEnumerable<double> values)
    {
        var list = values as IList<double> ?? values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }

    private static double Pct(IEnumerable<double> values, double p)
    {
        var ordered = values.OrderBy(x => x).ToList();
        if (ordered.Count == 0)
            return 0;
        var idx = (ordered.Count - 1) * p;
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi)
            return ordered[lo];
        var t = idx - lo;
        return ordered[lo] * (1 - t) + ordered[hi] * t;
    }

    private static double Norm(double value, double min, double max)
    {
        if (Math.Abs(max - min) < 1e-9)
            return 0.5;
        return Math.Clamp((value - min) / (max - min), 0, 1);
    }

    private static string Signed(double v) => v >= 0 ? $"+{v:0.0}" : $"{v:0.0}";

    private static double Pearson(List<double> x, List<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 3)
            return 0;
        var ax = x.Take(n).Average();
        var ay = y.Take(n).Average();
        double num = 0, dx = 0, dy = 0;
        for (var i = 0; i < n; i++)
        {
            var vx = x[i] - ax;
            var vy = y[i] - ay;
            num += vx * vy;
            dx += vx * vx;
            dy += vy * vy;
        }

        var den = Math.Sqrt(dx * dy);
        return den < 1e-9 ? 0 : num / den;
    }
}
