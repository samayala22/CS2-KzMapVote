using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net.Http;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Menus;
using SwiftlyS2.Core;
using SwiftlyS2.Core.Menus;
using SwiftlyS2.Core.Menus.OptionsBase;

using Tomlyn.Extensions.Configuration;

namespace KzMapVote;

class MainConfigModel {
    public string SteamApiKey { get; set; } = "";
    public float VoteDuration { get; set; } = 30.0f;
}

static class Utils {
    public static int MapTierToInt(string Tier) {
        return Tier.ToLower() switch {
            "very-easy" => 1,
            "easy" => 2,
            "medium" => 3,
            "advanced" => 4,
            "hard" => 5,
            "very-hard" => 6,
            "extreme" => 7,
            "death" => 8,
            "unfeasible" => 9,
            "impossible" => 10,
            _ => 0,
        };
    }

    public static bool IsWorkshopID(string input) {
        return input.Length == 10 && input.All(char.IsDigit);
    }
}

record MapEntry {
    public string Name { get; set; } = "";
    public long WorkshopID { get; set; } = -1;
    public int Tier { get; set; } = -1;
}

[PluginMetadata(
    Id = "KzMapVote",
    #if WORKFLOW
        Version = "WORKFLOW_VERSION",
    #else
        Version = "Local",
    #endif
    Name = "KzMapVote",
    Author = "Praetor",
    Description = "Map voting system for KZ servers"
)]
public partial class KzMapVote : BasePlugin {
    const int m_options_nb = 5;

    ServiceProvider? m_provider;
    IMenuAPI? m_menu;
    MainConfigModel m_config = new();
    Random m_rng = new();
    HttpClient m_http_client = new();
    CancellationTokenSource? m_vote_timer_token;
    
    // RTV data
    float m_vote_remaining = 0.0f;
    bool m_rtv = false; // is rtv active
    bool m_map_changing = false; // is map changing
    HashSet<int> m_rtv_players = new(64); // playerIDs who requested rtv

    // Voting data
    int[] m_voting_count = new int[m_options_nb]; // voting options counter
    MapEntry[] m_voting_options = new MapEntry[m_options_nb]; // voting options
    Dictionary<long, int> m_player_votes = new(); // key: playerID, value: option index

    // Map pool data
    List<MapEntry> m_map_nominations = new(m_options_nb-1);
    volatile List<MapEntry> m_map_pool = new(); // for cross-thread reference swap

    public KzMapVote(ISwiftlyCore core) : base(core) {
    }

    public override void Load(bool hotReload) {
        Core.Configuration
            .InitializeTomlWithModel<MainConfigModel>("config.toml", "Main")
            .Configure(builder => {
                builder.AddTomlFile("config.toml", optional:false, reloadOnChange:true);
            });

        ServiceCollection services = new();
        services
            .AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<MainConfigModel>()
            .BindConfiguration("Main");

        m_provider = services.BuildServiceProvider();
        m_config = m_provider.GetRequiredService<IOptions<MainConfigModel>>().Value;

        Task.Run(FetchMapPool);
        Core.Event.OnMapLoad += (@event) => {
            m_map_changing = false;
        };
        Core.Event.OnClientDisconnected += (@event) => {
            bool had = m_player_votes.Remove(@event.PlayerId, out int prev);
            if (had) m_voting_count[prev]--;
            m_rtv_players.Remove(@event.PlayerId);
        };
    }

    public override void Unload() {
        m_http_client.Dispose();
    }

    private void UpdateVoteTimer() {
        if (m_menu is null) return;
        
        if (m_vote_remaining <= 0) {
            EndVote();
            return;
        }
        
        m_menu.Configuration.Title = $"Map Vote ({m_vote_remaining:0}s)";
        m_vote_remaining -= 1.0f;
    }

    private async Task FetchMapPool() {
        try {
            string response = await m_http_client.GetStringAsync("https://api.cs2kz.org/maps");
            using JsonDocument doc = JsonDocument.Parse(response);

            var maps = new List<MapEntry>();
            foreach (JsonElement map in doc.RootElement.GetProperty("values").EnumerateArray()) {
                if (!map.TryGetProperty("name", out var nameProp) ||
                    !map.TryGetProperty("workshop_id", out var idProp) ||
                    !map.TryGetProperty("courses", out var courses) ||
                    courses.GetArrayLength() == 0) {
                    continue;
                }

                string? name = nameProp.GetString();
                long workshopId = idProp.GetInt64();
                string? Tier = courses[0]
                    .GetProperty("filters")
                    .GetProperty("classic")
                    .GetProperty("nub_tier")
                    .GetString();

                if (name is null || Tier is null) continue;
                maps.Add(new MapEntry {
                    Name = name,
                    WorkshopID = workshopId,
                    Tier = Utils.MapTierToInt(Tier)
                });
            }
            m_map_pool = maps; // old list is GC'ed
        }
        catch {
            _ = Core.PlayerManager.SendChatAsync("Error fetching map pool");
        }
    }

    async Task<string?> GetWorkshopTitle(string steamApiKey, string workshopId)
    {
        try {
            var key_param = string.IsNullOrEmpty(steamApiKey) ? "" : $"key={steamApiKey}&";
            var response = await m_http_client.GetStringAsync($"https://api.steampowered.com/IPublishedFileService/GetDetails/v1/?{key_param}publishedfileids[0]={workshopId}");
            var doc = JsonDocument.Parse(response);
            
            return doc.RootElement
                .GetProperty("response")
                .GetProperty("publishedfiledetails")[0]
                .GetProperty("title")
                .GetString();
        } catch (Exception e) {
            Core.Logger.LogError(e, $"Error fetching workshop title for workshop ID {workshopId}");
            return null;
        }
    }

    private void BuildMenu() {
        var builder = Core.MenusAPI.CreateBuilder();
        builder
            .SetPlayerFrozen(false)
            .EnableSound()
            .SetSelectButton(KeyBind.E)
            .SetMoveBackwardButton(KeyBind.Shift)
            .SetExitButton(KeyBind.Tab);

        builder
            .Design.SetMenuTitle("Map Vote")
            .Design.SetMaxVisibleItems(5)            // Set max visible items per page (1-5)
            .Design.SetMenuTitleVisible(true)        // Show/hide title
            .Design.SetMenuFooterVisible(true);       // Show/hide footer
        
        for (int i = 0; i < m_options_nb; i++) {
            int optionIndex = i;
            MapEntry option = m_voting_options[i];
            var button = new ButtonMenuOption(option.Name);

            button.BeforeFormat += (sender, args) => {
                string full_name = option.Tier != -1 ? $"{option.Name} (T{option.Tier})" : option.Name;
                string suffix = $"<font color='#4c4c4cff'> : {m_voting_count[optionIndex]}</font>";
                if (m_player_votes.TryGetValue(args.Player.PlayerID, out int votedIndex) && votedIndex == optionIndex) {
                    args.CustomText = $"<font color='#00FF00'> {full_name}</font>{suffix}";
                } else {
                    args.CustomText = $"{full_name}{suffix}";
                }
            };

            button.Click += (sender, args) => {
                bool had = m_player_votes.Remove(args.Player.PlayerID, out int prev);
                if (had) m_voting_count[prev]--;
                if (!had || prev != optionIndex) {
                    m_player_votes[args.Player.PlayerID] = optionIndex;
                    m_voting_count[optionIndex]++;
                }
                return ValueTask.CompletedTask;
            };

            builder.AddOption(button);
        }
        m_menu = builder.Build();
    }

    public void EndVote() {
        m_rtv = false;
        // Stop the countdown timer
        m_vote_timer_token?.Cancel();
        m_vote_timer_token = null;

        if (m_menu is null) return;
        Core.MenusAPI.CloseMenu(m_menu);

        m_map_nominations.Clear();

        int winning_map_votes = m_voting_count.Max();
        int winning_map_idx = Array.IndexOf(m_voting_count, winning_map_votes);

        if (winning_map_votes == 0) {
            Core.PlayerManager.SendChat("Voting ended and no option was selected");
            return;
        }

        if (winning_map_idx == m_options_nb-1) {
            Core.PlayerManager.SendChat($"Voting ended and the map won't change with {winning_map_votes} vote(s).");
            return;
        }

        m_map_changing = true;
        MapEntry winning_map = m_voting_options[winning_map_idx];
        Core.PlayerManager.SendChat($"Voting ended! The selected map is {winning_map.Name} with {winning_map_votes} vote(s).");
        Core.Scheduler.DelayBySeconds(3.0f, () => {
            Core.Engine.ExecuteCommand($"host_workshop_map {winning_map.WorkshopID}");
        });
    }

    public void StartVote() {
        m_rtv = true;
        var pool = m_map_pool; // reference so no risk of modification in nominate thread
        
        // Check that map pool is populated
        if (pool.Count < 5) {
            Core.PlayerManager.SendChat("API error while fetching maps");
            return;
        }

        m_player_votes.Clear();
        Array.Clear(m_voting_options);
        Array.Clear(m_voting_count);

        // Replace the first elements with nominations
        for (int i = 0; i < m_map_nominations.Count; i++) {
            m_voting_options[i] = m_map_nominations[i];
        }
        
        // Fill the rest with random maps from the map pool
        for (int i = m_map_nominations.Count; i < m_options_nb-1; i++) {
            MapEntry randomMap;
            do {
                int randomIndex = m_rng.Next(pool.Count);
                randomMap = pool[randomIndex];
            } while (m_voting_options.Take(i).Contains(randomMap));
            m_voting_options[i] = randomMap;
        }

        // Last option is reserved for "Don't change"
        m_voting_options[m_options_nb-1] = new MapEntry {
            Name = "Don't change",
            WorkshopID = -1,
            Tier = -1
        };

        BuildMenu();
        if (m_menu is null) return;

        // Set up the countdown timer (updates every second)
        m_vote_remaining = m_config.VoteDuration;
        m_vote_timer_token = Core.Scheduler.RepeatBySeconds(1.0f, UpdateVoteTimer);
        Core.Scheduler.StopOnMapChange(m_vote_timer_token);
        Core.MenusAPI.OpenMenu(m_menu);
    }

    [Command("rtv")]
    public void RequestVote(ICommandContext ctx) {
        if (ctx.Sender is null) return;
        if (m_rtv) {
            ctx.Sender.SendChat("Vote already in progress.");
            return;
        }
        if (m_map_changing) {
            ctx.Sender.SendChat("Map is changing, please wait.");
            return;
        }
        m_rtv_players.Add(ctx.Sender.PlayerID);
        int required_votes = Core.PlayerManager.PlayerCount / 2 + 1;
        Core.PlayerManager.SendChat($"RTV requested: ({m_rtv_players.Count}/{required_votes} votes)");

        if (m_rtv_players.Count == required_votes) {
            Core.PlayerManager.SendChat($"Starting map vote for {m_config.VoteDuration} seconds...");
            StartVote();
            m_rtv_players.Clear();
        }
    }

    [Command("nominate")]
    [CommandAlias("nom")]
    public void Nominate(ICommandContext ctx) {
        if (ctx.Sender is null) return;
        if (m_rtv) {
            ctx.Sender.SendChat("Vote already in progress.");
            return;
        }
        if (m_map_changing) {
            ctx.Sender.SendChat("Map is changing, please wait.");
            return;
        }
        if (!(ctx.Args.Length == 1 && ctx.Args[0] != "")) {
            ctx.Sender.SendChat("Usage: !nominate <map_name|workshop_id>");
            return;
        }
        if (m_map_nominations.Count >= m_options_nb - 1) {
            ctx.Sender.SendChat("Map nomination limit reached.");
            return;
        }
        var input = ctx.Args[0];
        Task.Run(async () => {
            MapEntry entry;
            if (Utils.IsWorkshopID(input)) {
                string? map_name = await GetWorkshopTitle(m_config.SteamApiKey, input);
                if (map_name is null) {
                    _ = ctx.Sender.SendChatAsync("Can't find workshop map");
                    return;
                }
                if (!map_name.StartsWith("kz_")) {
                    _ = ctx.Sender.SendChatAsync("Only kz_ maps can be nominated.");
                    return;
                }
                entry = new MapEntry { Name = map_name, WorkshopID = long.Parse(input), Tier = -1 };
            } else {
                await FetchMapPool();
                var pool = m_map_pool; // reference capture
                var matches = pool
                    .Where(m => m.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToList();

                if (matches.Count == 0) {
                    _ = ctx.Sender.SendChatAsync("No maps found.");
                    return;
                }
                if (matches.Count > 1) {
                    _ = ctx.Sender.SendChatAsync("Multiple maps found.");
                    return;
                }
                entry = matches[0];
            }

            // Schedule on main thread to avoid Contains conflict
            Core.Scheduler.NextTick(() => {
                if (m_map_nominations.Contains(entry)) {
                    _ = ctx.Sender.SendChatAsync("Map already nominated.");
                    return;
                }
                m_map_nominations.Add(entry);
                Core.PlayerManager.SendChat($"{ctx.Sender.Controller.PlayerName} nominated {entry.Name}");
            });
        });
    }
}