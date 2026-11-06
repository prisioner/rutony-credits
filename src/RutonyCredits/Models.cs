using System.Text.Json;

namespace RutonyCredits;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed record EventInput(string Source, string Platform, string Kind, string DisplayName,
    string? SourceEventId = null, DateTimeOffset? OccurredAtUtc = null, bool IsTest = false, decimal? Amount = null, string? Currency = null);
public sealed record CreditEvent(string SessionId, string EventId, long Sequence, string Source,
    string Platform, string Kind, string DisplayName, DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? OccurredAtUtc, string? SourceEventId, decimal? Amount = null, string? Currency = null);
public sealed record CreditSection(string Key, string Heading, string[] Names);
public sealed record CreditSettings
{
    public string FontFamily { get; init; } = "Arial";
    public int FontSize { get; init; } = 48;
    public int Speed { get; init; } = 80;
    public string TextColor { get; init; } = "#ffffff";
    public bool Loop { get; init; }
    public bool AggregateDonations { get; init; }
    public string[] ModeratorExclusions { get; init; } = [];
    public Dictionary<string, bool> EnabledSections { get; init; } = new()
    {
        ["vk-raids"] = true, ["twitch-raids"] = true, ["donations"] = true,
        ["followers"] = true, ["subscribers"] = true, ["moderators"] = true
    };
    public Dictionary<string, string> Headings { get; init; } = new()
    {
        ["vk-raids"] = "Рейды VK Video Live", ["twitch-raids"] = "Рейды Twitch",
        ["donations"] = "Спасибо за поддержку", ["followers"] = "Новые фолловеры",
        ["subscribers"] = "Новые подписчики", ["moderators"] = "Модераторы"
    };
}
public sealed record CreditRun(string RunId, string SessionId, string Mode,
    DateTimeOffset StartedAtUtc, CreditSettings Settings, CreditSection[] Sections, string CompletionToken);
public sealed record CreditState(string SessionId, int EventCount, Dictionary<string, int> Counts,
    string? Error, string Playback, string? RunId, string? Mode, CreditSettings Settings,
    DateTimeOffset ServerTimeUtc)
{
    public string HostMode { get; init; } = "rutony";
    public TwitchRaidStatus TwitchRaids { get; init; } = new(false, false, "Не используется", null);
}
