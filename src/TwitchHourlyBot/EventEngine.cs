using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TwitchHourlyBot;

public static class EventCategories
{
    public const string Hydration = "hydration";
    public const string Eyes = "eyes";
    public const string Stretch = "stretch";
    public const string Relax = "relax";
    public const string Encouragement = "encouragement";

    public static readonly string[] All =
    {
        Hydration, Eyes, Stretch, Relax, Encouragement
    };

    public static string DisplayName(string category) => category switch
    {
        Hydration => "💧 ดื่มน้ำ",
        Eyes => "👀 พักสายตา",
        Stretch => "💪 ยืดเหยียด",
        Relax => "😮‍💨 ผ่อนคลาย",
        Encouragement => "💜 กำลังใจ",
        _ => category
    };
}

public sealed class EventMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = EventCategories.Hydration;
    public string Text { get; set; } = "💧 อย่าลืมดื่มน้ำสักหน่อยนะ";
}

public sealed class EventCategorySetting
{
    public bool Enabled { get; set; } = true;
    public int CooldownMinutes { get; set; } = 60;
}

public sealed class ChannelEventSettings
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, EventCategorySetting> Categories { get; set; } = new();

    public static ChannelEventSettings CreateDefault()
    {
        var result = new ChannelEventSettings();
        foreach (var category in EventCategories.All)
            result.Categories[category] = new EventCategorySetting();
        return result;
    }

    public EventCategorySetting GetCategory(string category)
    {
        if (!Categories.TryGetValue(category, out var setting))
        {
            setting = new EventCategorySetting();
            Categories[category] = setting;
        }
        return setting;
    }
}

public sealed class EventEngineSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 30;
    public List<EventMessage> Messages { get; set; } = EventMessageDefaults.Create();
}

public static class EventMessageDefaults
{
    public static List<EventMessage> Create() => new()
    {
        new() { Category = EventCategories.Hydration, Text = "💧 อย่าลืมดื่มน้ำสักหน่อยนะ" },
        new() { Category = EventCategories.Hydration, Text = "🥤 Water check! จิบสักหน่อยแล้วค่อยลุยต่อ" },
        new() { Category = EventCategories.Hydration, Text = "💦 เช็กแก้วน้ำข้างตัวหน่อย ยังมีน้ำเหลืออยู่ไหมนะ" },

        new() { Category = EventCategories.Eyes, Text = "👀 พักสายตาจากหน้าจอสักครู่ มองไกล ๆ บ้างนะ" },
        new() { Category = EventCategories.Eyes, Text = "👁️ กระพริบตาและละสายตาจากจอสักแป๊บนะ" },
        new() { Category = EventCategories.Eyes, Text = "🌿 มองอะไรไกล ๆ สักพัก ให้ตาได้พักบ้าง" },

        new() { Category = EventCategories.Stretch, Text = "💪 ลองหมุนไหล่เบา ๆ สัก 2-3 รอบนะ" },
        new() { Category = EventCategories.Stretch, Text = "✋ ขยับข้อมือและนิ้วเบา ๆ กันหน่อย มือทำงานมานานแล้ว" },
        new() { Category = EventCategories.Stretch, Text = "🧍 ถ้านั่งมานานแล้ว ลองลุกยืดตัวแป๊บนึงนะ" },

        new() { Category = EventCategories.Relax, Text = "😮‍💨 หายใจเข้าลึก ๆ แล้วผ่อนออกช้า ๆ สัก 2-3 ครั้งนะ" },
        new() { Category = EventCategories.Relax, Text = "🧘 ผ่อนคลายไหล่กับกรามสักครู่ แล้วค่อยไปต่อ" },
        new() { Category = EventCategories.Relax, Text = "☕ พักจากหน้าจอสักครู่ แล้วค่อยกลับมาเล่นกันต่อก็ได้นะ" },

        new() { Category = EventCategories.Encouragement, Text = "💜 วันนี้ทำได้ดีแล้วนะ ค่อย ๆ เล่นไป ไม่ต้องรีบ" },
        new() { Category = EventCategories.Encouragement, Text = "🌟 ไม่ต้องตอบทุก Chat ก็ได้นะ พักสบาย ๆ บ้าง" },
        new() { Category = EventCategories.Encouragement, Text = "🎮 เล่นต่อได้เลย แต่ถ้าเหนื่อยก็พักได้เสมอนะ" }
    };
}

public sealed class EventEngine
{
    private readonly Random _random = new();

    public EventMessage? PickEvent(ChannelConfig channel, EventEngineSettings globalSettings, DateTimeOffset now)
    {
        if (!globalSettings.Enabled || !channel.IsEnabled || !channel.EventSettings.Enabled)
            return null;

        var available = globalSettings.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Where(m => channel.EventSettings.GetCategory(m.Category).Enabled)
            .Where(m => CooldownStore.IsReady(channel.Channel, m.Category, channel.EventSettings.GetCategory(m.Category).CooldownMinutes, now))
            .ToList();

        if (available.Count == 0) return null;

        // Prefer a category that has not been used most recently, then randomize within the eligible set.
        var grouped = available.GroupBy(x => x.Category).ToList();
        var eligibleCategories = grouped
            .Select(g => g.Key)
            .OrderBy(category => CooldownStore.GetLastSent(channel.Channel, category))
            .ToList();

        var category = eligibleCategories[_random.Next(Math.Min(2, eligibleCategories.Count))];
        var candidates = grouped.First(g => g.Key == category).ToList();
        var lastMessageId = CooldownStore.GetLastMessageId(channel.Channel, category);
        var withoutLast = candidates.Where(x => x.Id != lastMessageId).ToList();
        if (withoutLast.Count > 0) candidates = withoutLast;

        return candidates[_random.Next(candidates.Count)];
    }
}

public static class CooldownStore
{
    private static readonly object Sync = new();
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchHourlyBot");
    private static readonly string FilePath = Path.Combine(Folder, "event-state.json");
    private static readonly string LegacyFilePath = Path.Combine(AppContext.BaseDirectory, "event-state.json");
    private static Dictionary<string, Dictionary<string, EventCooldownState>> Data = Load();

    public static bool IsReady(string channel, string category, int cooldownMinutes, DateTimeOffset now)
    {
        var last = GetLastSent(channel, category);
        return last == DateTimeOffset.MinValue || now - last >= TimeSpan.FromMinutes(Math.Max(0, cooldownMinutes));
    }

    public static DateTimeOffset GetLastSent(string channel, string category)
    {
        lock (Sync)
        {
            if (Data.TryGetValue(channel.ToLowerInvariant(), out var categories) &&
                categories.TryGetValue(category, out var state))
                return state.LastSentUtc;
            return DateTimeOffset.MinValue;
        }
    }

    public static string GetLastMessageId(string channel, string category)
    {
        lock (Sync)
        {
            if (Data.TryGetValue(channel.ToLowerInvariant(), out var categories) &&
                categories.TryGetValue(category, out var state))
                return state.LastMessageId;
            return "";
        }
    }

    public static void MarkSent(string channel, string category, string messageId, DateTimeOffset now)
    {
        lock (Sync)
        {
            var key = channel.ToLowerInvariant();
            if (!Data.TryGetValue(key, out var categories))
            {
                categories = new Dictionary<string, EventCooldownState>();
                Data[key] = categories;
            }

            categories[category] = new EventCooldownState
            {
                LastSentUtc = now,
                LastMessageId = messageId
            };
            Save();
        }
    }

    private static void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, Dictionary<string, EventCooldownState>> Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, EventCooldownState>>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }
}

public sealed class EventCooldownState
{
    public DateTimeOffset LastSentUtc { get; set; } = DateTimeOffset.MinValue;
    public string LastMessageId { get; set; } = "";
}
