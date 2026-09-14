namespace YandexGamesAnalytics.Services;

public sealed class OverviewStats
{
    public int Games { get; set; }
    public int Enriched { get; set; }
    public int Categories { get; set; }
    public int Tags { get; set; }
    public int Developers { get; set; }
    public double AvgRating { get; set; }
    public double AvgGq { get; set; }
    public double MedianGq { get; set; }
    public double MedianReviews { get; set; }
    public int WithPurchases { get; set; }
    public int WithVideo { get; set; }
    public int NewFeed { get; set; }
    public DateTime? LastSync { get; set; }
    public string? LastMode { get; set; }
}

public sealed class GroupStats
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public int Games { get; set; }
    public double AvgGq { get; set; }
    public double AvgRating { get; set; }
    public double AvgReviews { get; set; }
    public double AvgDemand { get; set; }
    public double MedianGq { get; set; }
    public double MedianDemand { get; set; }
    public double MedianRating { get; set; }
    public double MedianReviews { get; set; }
    public double P25Gq { get; set; }
    public double P75Gq { get; set; }
    public double P25Reviews { get; set; }
    public double P75Reviews { get; set; }
    public double ShareGq50 { get; set; }
    public double ShareGq70 { get; set; }
    public int FreshGames { get; set; }
    public double FreshMedianGq { get; set; }
    public double FreshMedianDemand { get; set; }
    public double OneStarShare { get; set; }
    public double SoftAgeShare { get; set; }
    public double DualPlatformShare { get; set; }
    public double IapMedianReviews { get; set; }
    public double OpportunityScore { get; set; }
    public string Verdict { get; set; } = "";
    public string Quadrant { get; set; } = "";
    public int WithPurchases { get; set; }
    public int WithVideo { get; set; }
    public int InTop { get; set; }
    public double PurchaseShare => Games == 0 ? 0 : 100.0 * WithPurchases / Games;
    public double VideoShare => Games == 0 ? 0 : 100.0 * WithVideo / Games;
}

public sealed class FactorEffect
{
    public string Name { get; set; } = "";
    public string WithLabel { get; set; } = "Да";
    public string WithoutLabel { get; set; } = "Нет";
    public int WithCount { get; set; }
    public int WithoutCount { get; set; }
    public double WithAvgGq { get; set; }
    public double WithoutAvgGq { get; set; }
    public double GqDelta => WithAvgGq - WithoutAvgGq;
    public double WithMedianGq { get; set; }
    public double WithoutMedianGq { get; set; }
    public double MedianGqDelta => WithMedianGq - WithoutMedianGq;
    public double WithAvgReviews { get; set; }
    public double WithoutAvgReviews { get; set; }
    public double ReviewsDelta => WithAvgReviews - WithoutAvgReviews;
    public double WithMedianReviews { get; set; }
    public double WithoutMedianReviews { get; set; }
    public double WithAvgRating { get; set; }
    public double WithoutAvgRating { get; set; }
}

public sealed class CorrelationRow
{
    public string Feature { get; set; } = "";
    public string Target { get; set; } = "";
    public double Pearson { get; set; }
    public int N { get; set; }
}

public sealed class DeveloperStats
{
    public int? DeveloperId { get; set; }
    public string Name { get; set; } = "";
    public int Games { get; set; }
    public double AvgGq { get; set; }
    public double AvgDemand { get; set; }
    public int TotalReviews { get; set; }
}

public sealed class Insight
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Kind { get; set; } = "info";
}

public sealed class WordStat
{
    public string Phrase { get; set; } = "";
    public int Games { get; set; }
    public int TopGames { get; set; }
    public double Lift { get; set; }
    public double MedianGq { get; set; }
    public double MedianReviews { get; set; }
    public List<string> Examples { get; set; } = [];
}

public sealed class GenrePairStats
{
    public string LeftKey { get; set; } = "";
    public string RightKey { get; set; } = "";
    public string Title { get; set; } = "";
    public int Games { get; set; }
    public double MedianGq { get; set; }
    public double MedianDemand { get; set; }
    public double MedianReviews { get; set; }
    public double PurchaseShare { get; set; }
}

public sealed class TitleLengthBucket
{
    public string Label { get; set; } = "";
    public int Games { get; set; }
    public double MedianGq { get; set; }
    public double MedianReviews { get; set; }
    public double MedianDemand { get; set; }
}

public sealed class AudienceSlice
{
    public string Label { get; set; } = "";
    public int Games { get; set; }
    public double Share { get; set; }
    public double MedianGq { get; set; }
}

public sealed class ChecklistItem
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Recommended { get; set; }
}

public sealed class PlaybookReport
{
    public List<Insight> Insights { get; set; } = [];
    public List<GroupStats> TopGenres { get; set; } = [];
    public List<GenrePairStats> Combos { get; set; } = [];
    public List<GenrePairStats> GenreTagCombos { get; set; } = [];
    public List<AudienceSlice> Ages { get; set; } = [];
    public List<AudienceSlice> Platforms { get; set; } = [];
    public List<AudienceSlice> Languages { get; set; } = [];
    public List<FactorEffect> EasyWins { get; set; } = [];
    public List<GroupStats> Monetization { get; set; } = [];
    public List<GroupStats> Engagement { get; set; } = [];
    public List<WordStat> TitleWords { get; set; } = [];
    public List<ChecklistItem> Checklist { get; set; } = [];
    public List<GroupStats> RiskyGenres { get; set; } = [];
    public List<TitleLengthBucket> TitleLengths { get; set; } = [];
}

public sealed class AnalyticsSnapshot
{
    public OverviewStats Overview { get; set; } = new();
    public List<GroupStats> Genres { get; set; } = [];
    public List<GroupStats> Tags { get; set; } = [];
    public List<FactorEffect> Factors { get; set; } = [];
    public List<CorrelationRow> Correlations { get; set; } = [];
    public List<DeveloperStats> Developers { get; set; } = [];
    public List<Insight> Insights { get; set; } = [];
    public List<GameListItem> TopDemand { get; set; } = [];
    public List<GameListItem> TopGq { get; set; } = [];
    public List<GameListItem> TopReviews { get; set; } = [];
    public List<GameListItem> Newest { get; set; } = [];
    public List<WordStat> TitleWords { get; set; } = [];
    public List<WordStat> DescriptionWords { get; set; } = [];
    public List<GenrePairStats> GenrePairs { get; set; } = [];
    public List<TitleLengthBucket> TitleLengths { get; set; } = [];
    public PlaybookReport Playbook { get; set; } = new();
}

public sealed class GameListItem
{
    public int AppId { get; set; }
    public string Title { get; set; } = "";
    public string? DeveloperName { get; set; }
    public string? IconUrl { get; set; }
    public double? Rating { get; set; }
    public int RatingCount { get; set; }
    public int? GqRating { get; set; }
    public double DemandScore { get; set; }
    public DateTime? FirstPublished { get; set; }
    public bool HasPurchases { get; set; }
    public bool HasVideo { get; set; }
    public string Categories { get; set; } = "";
    public string? AgeRating { get; set; }
}
