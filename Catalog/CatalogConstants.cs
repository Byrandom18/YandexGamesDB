namespace YandexGamesAnalytics.Catalog;

public static class CatalogConstants
{
    public const string CatalogHost = "https://yandex.ru";

    public static readonly IReadOnlyDictionary<string, string> CategoryTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "Боевики",
            ["simulator"] = "Симуляторы",
            ["arcade"] = "Аркады",
            ["casual"] = "Казуальные",
            ["kids"] = "Детские",
            ["adventure"] = "Приключения",
            ["horrors"] = "Хорроры",
            ["for_boys"] = "Для мальчиков",
            ["quiz"] = "Викторины",
            ["educational"] = "Обучающие",
            ["economic"] = "Экономические",
            ["race"] = "Гонки",
            ["for_girls"] = "Для девочек",
            ["puzzles"] = "Головоломки",
            ["novels"] = "Новеллы",
            ["match3"] = "Три в ряд",
            ["games_io"] = "Игры .io",
            ["midcore"] = "Мидкорные",
            ["role"] = "Ролевые",
            ["cards"] = "Карточные",
            ["tabletop"] = "Настольные",
            ["strategy"] = "Стратегии",
            ["balloons"] = "Шарики",
            ["for_two_persons"] = "Для двоих",
            ["sports"] = "Спорт",
            ["casino"] = "Казино",
            ["for_babies"] = "Для малышей",
            ["new"] = "Новые"
        };

    public static readonly string[] RankedCategorySlugs =
    [
        "new", "action", "simulator", "arcade", "casual", "kids", "adventure", "horrors",
        "for_boys", "quiz", "educational", "economic", "race", "for_girls", "puzzles",
        "novels", "match3", "games_io", "midcore", "role", "cards", "tabletop", "strategy",
        "balloons", "for_two_persons", "sports", "casino", "for_babies"
    ];

    public static string TitleFor(string slug)
    {
        if (CategoryTitles.TryGetValue(slug, out var title))
            return title;

        return slug.Replace('_', ' ');
    }
}
