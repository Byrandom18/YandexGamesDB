using System.Text.RegularExpressions;
using YandexGamesAnalytics.Data;

namespace YandexGamesAnalytics.Services;

public static class TitleLanguageAnalyzer
{
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Stop =
    [
        "и", "в", "во", "на", "с", "со", "к", "ко", "о", "об", "от", "до", "из", "за", "по", "для", "при", "про",
        "над", "под", "без", "между", "через", "или", "но", "а", "же", "ли", "бы", "то", "это", "как", "что",
        "не", "ни", "да", "уже", "ещё", "еще", "все", "всё", "его", "её", "их", "мы", "вы", "он", "она", "они",
        "я", "ты", "мой", "моя", "наш", "ваша", "этот", "эта", "эти", "тот", "та", "те", "свой", "своя",
        "игра", "игры", "игру", "игре", "игрой", "играть", "бесплатно", "бесплатная", "бесплатные", "онлайн",
        "браузерная", "браузерные", "скачивания", "регистрации", "яндекс", "яндex", "game", "games", "play",
        "free", "online", "the", "a", "an", "of", "to", "for", "and", "or", "in", "on", "with", "by", "from",
        "new", "best", "fun", "html5", "html", "3d", "2d", "edition", "deluxe", "pro", "hd", "plus", "super",
        "можно", "нужно", "чтобы", "если", "когда", "после", "перед", "только", "очень", "также", "более",
        "самый", "самая", "самое", "ваши", "ваших", "своих", "будет", "быть", "есть", "нет", "тут", "там",
        "где", "кто", "чем", "этой", "этом", "этих", "того", "том", "тех", "вас", "вам", "нас", "нам",
        "один", "одна", "два", "две", "три", "ваш", "ваши", "каждый", "каждой", "всех", "всего"
    ];

    public static List<WordStat> Analyze(IReadOnlyList<Game> games, Func<Game, string?> text, int minGames = 12)
    {
        if (games.Count < minGames * 2)
            return [];

        var ranked = games.OrderByDescending(g => g.DemandScore).ToList();
        var topCut = Math.Max(1, ranked.Count / 4);
        var topIds = ranked.Take(topCut).Select(g => g.AppId).ToHashSet();

        var index = new Dictionary<string, List<Game>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            foreach (var phrase in Phrases(text(game)))
            {
                if (!index.TryGetValue(phrase, out var list))
                {
                    list = [];
                    index[phrase] = list;
                }

                if (list.Count == 0 || list[^1].AppId != game.AppId)
                    list.Add(game);
            }
        }

        var result = new List<WordStat>();
        foreach (var (phrase, list) in index)
        {
            if (list.Count < minGames)
                continue;

            var topHits = list.Count(g => topIds.Contains(g.AppId));
            var shareAll = list.Count / (double)games.Count;
            var shareTop = topHits / (double)topCut;
            if (shareAll < 1e-9)
                continue;

            var gq = list.Select(g => (double)(g.GqRating ?? 0)).OrderBy(x => x).ToList();
            var reviews = list.Select(g => (double)g.RatingCount).OrderBy(x => x).ToList();
            result.Add(new WordStat
            {
                Phrase = phrase,
                Games = list.Count,
                TopGames = topHits,
                Lift = shareTop / shareAll,
                MedianGq = Percentile(gq, 0.5),
                MedianReviews = Percentile(reviews, 0.5),
                Examples = list.OrderByDescending(g => g.DemandScore).Select(g => g.Title).Distinct().Take(3).ToList()
            });
        }

        return result
            .Where(w => w.Lift >= 1.05 || w.MedianGq >= 55)
            .OrderByDescending(w => w.Lift)
            .ThenByDescending(w => w.MedianGq)
            .Take(40)
            .ToList();
    }

    public static IEnumerable<string> Phrases(string? text)
    {
        var tokens = Tokens(text).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            yield return tokens[i];
            if (i + 1 < tokens.Count)
                yield return tokens[i] + " " + tokens[i + 1];
        }
    }

    public static IEnumerable<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var normalized = text.Replace('ё', 'е').Replace('Ё', 'Е');
        foreach (Match match in TokenRegex.Matches(normalized))
        {
            var token = match.Value.ToLowerInvariant();
            if (token.Length < 3 || Stop.Contains(token) || token.All(char.IsDigit))
                continue;
            yield return token;
        }
    }

    private static double Percentile(List<double> ordered, double p)
    {
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
}
