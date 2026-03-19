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
using Spectre.Console.Rendering;

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

enum VoteState
{
    Idle,
    Voting,
    ChangingMap
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
    CancellationTokenSource? m_map_change_timeout_token;
    
    // RTV data
    float m_vote_remaining = 0.0f; // remaining vote time in seconds
    VoteState m_vote_state = VoteState.Idle;
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

        Core.Event.OnMapLoad += (@event) => {
            ResetVoteState();
            ResetMapChangeState();
            Task.Run(async () => {
                var maps = await ApiGetMaps();
                if (maps.Count > 0) {
                    m_map_pool = maps; // only swap if the api succeeded in fetching maps to avoid emptying the pool on api failure
                } else {
                    Core.Logger.LogWarning("Map pool refresh failed; keeping previous pool.");
                }
            });
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

        if (Core.PlayerManager.PlayerCount == m_player_votes.Count) {
            m_vote_remaining = Math.Min(m_vote_remaining, 5.0f);
        }
        
        if (m_vote_remaining <= 0) {
            EndVote();
            return;
        }
        
        m_menu.Configuration.Title = $"Map Vote ({m_vote_remaining:0}s)";
        m_vote_remaining -= 1.0f;
    }

    private void ResetVoteState() {
        m_vote_remaining = 0.0f;
        m_vote_timer_token?.Cancel();
        m_vote_timer_token = null;

        if (m_menu is not null)
        {
            Core.MenusAPI.CloseMenu(m_menu);
            m_menu = null;
        }

        m_player_votes.Clear();
        m_rtv_players.Clear();
        m_map_nominations.Clear();
        Array.Clear(m_voting_count);
        Array.Clear(m_voting_options);
        m_vote_state = VoteState.Idle;
    }

    private void ResetMapChangeState() {
        m_map_change_timeout_token?.Cancel();
        m_map_change_timeout_token = null;
        m_vote_state = VoteState.Idle;
    }

    private async Task<List<MapEntry>> ApiGetMaps(string? name = null, string? state = "approved", int? limit = null, int? offset = null) {
        try {
            var query = new List<string>();
            if (name is not null) query.Add($"name={Uri.EscapeDataString(name)}");
            if (state is not null) query.Add($"state={Uri.EscapeDataString(state)}");
            if (limit is not null) query.Add($"limit={limit}");
            if (offset is not null) query.Add($"offset={offset}");

            string url = "https://api.cs2kz.org/maps";
            if (query.Count > 0) url += "?" + string.Join("&", query);

            string response = await m_http_client.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(response);

            var maps = new List<MapEntry>();
            foreach (JsonElement map in doc.RootElement.GetProperty("values").EnumerateArray()) {
                if (!map.TryGetProperty("name", out var nameProp) ||
                    !map.TryGetProperty("workshop_id", out var idProp) ||
                    !map.TryGetProperty("courses", out var courses) ||
                    courses.GetArrayLength() == 0) {
                    continue;
                }

                string? mapName = nameProp.GetString();
                long workshopId = idProp.GetInt64();
                string? tier = courses[0]
                    .GetProperty("filters")
                    .GetProperty("classic")
                    .GetProperty("nub_tier")
                    .GetString();

                if (mapName is null || tier is null) continue;
                maps.Add(new MapEntry {
                    Name = mapName,
                    WorkshopID = workshopId,
                    Tier = Utils.MapTierToInt(tier)
                });
            }
            return maps;
        } catch (Exception e) {
            Core.Logger.LogError(e, "Error fetching maps from API");
            return new List<MapEntry>();
        }
    }

    async Task<(string? title, string? rejection)> SteamGetDetails(string steamApiKey, string workshopId)
    {
        if (string.IsNullOrEmpty(steamApiKey)) {
            return (null, "Steam API key is required to query files.");
        }
        var url = $"https://api.steampowered.com/IPublishedFileService/GetDetails/v1/?key={steamApiKey}&publishedfileids[0]={workshopId}";
        try {
            var response = await m_http_client.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);

            var details = doc.RootElement
                .GetProperty("response")
                .GetProperty("publishedfiledetails")[0];

            if (details.TryGetProperty("result", out var result) && result.GetInt32() != 1)
                return (null, "Workshop item not found.");

            if (details.TryGetProperty("incompatible", out var incompat) && incompat.GetBoolean())
                return (null, "This is a CS:GO map, not compatible with CS2.");

            var title = details.GetProperty("title").GetString();
            return (title, null);
        } catch (Exception e) {
            Core.Logger.LogError(e, "Error fetching workshop details");
            return (null, "Failed to query Steam API.");
        }
    }

    async Task<(string? workshopId, string? rejection)> SteamQueryFiles(string steamApiKey, string query)
    {
        if (string.IsNullOrEmpty(steamApiKey)) {
            return (null, "Steam API key is required to query files.");
        }
        var url = $"https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/?key={steamApiKey}&query_type=0&appid=730&search_text={query}&numperpage=1";
        try {
            var response = await m_http_client.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);

            var body = doc.RootElement.GetProperty("response");
            if (body.TryGetProperty("total", out var total) && total.GetInt32() == 0)
                return (null, "Workshop item not found.");
            var details = body.GetProperty("publishedfiledetails")[0];
            if (details.TryGetProperty("result", out var result) && result.GetInt32() != 1)
                return (null, "Workshop item not found.");
            var workshopId = details.GetProperty("publishedfileid").GetString();
            return (workshopId, null);
        } catch (Exception e) {
            Core.Logger.LogError(e, "Error querying Steam files");
            return (null, "Failed to query Steam API.");
        }
    }

    private void BuildMenu() {
        var builder = Core.MenusAPI.CreateBuilder();
        builder
            .SetPlayerFrozen(false)
            .EnableSound()
            .SetSelectButton(KeyBind.E)
            .SetMoveForwardButton(KeyBind.Shift)
            .SetMoveBackwardButton(KeyBind.F)
            .SetExitButton(KeyBind.Tab);

        builder
            .Design.SetMenuTitle("Map Vote")
            .Design.SetMaxVisibleItems(m_options_nb)            // Set max visible items per page (1-5)
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
        int winning_map_votes = m_voting_count.Max();
        int winning_map_idx = Array.IndexOf(m_voting_count, winning_map_votes);
        MapEntry winning_map = m_voting_options[winning_map_idx];

        ResetVoteState();

        if (winning_map_votes == 0) {
            Core.PlayerManager.SendChat("Voting ended and no option was selected.");
            return;
        }

        if (winning_map_idx == m_options_nb-1) {
            Core.PlayerManager.SendChat($"Voting ended and the map won't change with {winning_map_votes} vote(s).");
            return;
        }

        m_vote_state = VoteState.ChangingMap;
        Core.PlayerManager.SendChat($"Voting ended! The selected map is {winning_map.Name} with {winning_map_votes} vote(s).");
        
        m_map_change_timeout_token = Core.Scheduler.DelayBySeconds(90.0f, () => {
            if (m_vote_state != VoteState.ChangingMap) return;
            ResetMapChangeState();
            Core.PlayerManager.SendChat("Map change failed or timed out. RTV is available again.");
        });

        Core.Scheduler.DelayBySeconds(1.0f, () => {
            Core.Engine.ExecuteCommand($"host_workshop_map {winning_map.WorkshopID}");
        });
    }

    public bool StartVote() {
        var pool = m_map_pool; // reference so no risk of modification in nominate thread
        
        // Check that map pool is populated
        if (pool.Count < m_options_nb) {
            Core.PlayerManager.SendChat("Not enough maps in pool to start vote");
            return false;
        }

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
        if (m_menu is null) {
            Core.Logger.LogError("Failed to build menu");
            return false;
        }

        // Set up the countdown timer (updates every second)
        m_vote_state = VoteState.Voting;
        m_vote_remaining = m_config.VoteDuration;
        m_vote_timer_token = Core.Scheduler.RepeatBySeconds(1.0f, UpdateVoteTimer);
        Core.Scheduler.StopOnMapChange(m_vote_timer_token);
        Core.MenusAPI.OpenMenu(m_menu);
        return true;
    }

    [Command("rtv")]
    public void RequestVote(ICommandContext ctx) {
        if (ctx.Sender is null) return;
        if (m_vote_state == VoteState.Voting) {
            IMenuAPI? currentMenu = Core.MenusAPI.GetCurrentMenu(ctx.Sender);
            if (currentMenu != null && currentMenu == m_menu) {
                ctx.Sender.SendChat("Vote already in progress.");
            } else if (m_menu != null) {
                Core.MenusAPI.OpenMenuForPlayer(ctx.Sender, m_menu);
            }
            return;
        }
        if (m_vote_state == VoteState.ChangingMap) {
            ctx.Sender.SendChat("Map is changing, please wait.");
            return;
        }
        m_rtv_players.Add(ctx.Sender.PlayerID);
        int required_votes = Core.PlayerManager.PlayerCount / 2 + 1;
        Core.PlayerManager.SendChat($"RTV requested: ({m_rtv_players.Count}/{required_votes} votes)");

        if (m_rtv_players.Count >= required_votes) {
            if (StartVote()) {
                Core.PlayerManager.SendChat($"Starting map vote for {m_config.VoteDuration} seconds...");
            } else {
                Core.PlayerManager.SendChat("Failed to start map vote.");
                ResetVoteState();
            }
        }
    }

    [Command("nominate")]
    [CommandAlias("nom")]
    public void Nominate(ICommandContext ctx) {
        if (ctx.Sender is null) return;
        if (m_vote_state == VoteState.Voting) {
            ctx.Sender.SendChat("Vote already in progress.");
            return;
        }
        if (m_vote_state == VoteState.ChangingMap) {
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
                var (map_name, rejection_reason) = await SteamGetDetails(m_config.SteamApiKey, input);
                if (map_name is null) {
                    _ = ctx.Sender.SendChatAsync(rejection_reason ?? "Invalid workshop ID.");
                    return;
                }
                if (!map_name.StartsWith("kz_")) {
                    _ = ctx.Sender.SendChatAsync("Only kz_ maps can be nominated.");
                    return;
                }
                entry = new MapEntry { Name = map_name, WorkshopID = long.Parse(input), Tier = -1 };
            } else {
                var matches = await ApiGetMaps(name: input, limit: 2);

                if (matches.Count == 1) {
                    entry = matches[0];
                } else if (matches.Count == 0) {
                    if (!input.StartsWith("kz_")) {
                        _ = ctx.Sender.SendChatAsync("No maps found.");
                        return;
                    } else {
                        var (workshopId, rejectionReason) = await SteamQueryFiles(m_config.SteamApiKey, input);
                        if (workshopId is null) {
                            _ = ctx.Sender.SendChatAsync(rejectionReason ?? "No maps found.");
                            return;
                        }
                        var (map_name, detailsRejection) = await SteamGetDetails(m_config.SteamApiKey, workshopId);
                        if (map_name is null) {
                            _ = ctx.Sender.SendChatAsync(detailsRejection ?? "Failed to get map details.");
                            return;
                        }
                        if (map_name != input) {
                            _ = ctx.Sender.SendChatAsync($"Workshop map not found");
                            return;
                        }
                        entry = new MapEntry { Name = map_name, WorkshopID = long.Parse(workshopId), Tier = -1 };
                    }
                } else {
                    _ = ctx.Sender.SendChatAsync("Multiple maps found.");
                    return;
                }
            }

            // Schedule on main thread to avoid Contains conflict
            Core.Scheduler.NextTick(() => {
                if (m_map_nominations.Any(x => x.WorkshopID == entry.WorkshopID)) {
                    _ = ctx.Sender.SendChatAsync("Map already nominated.");
                    return;
                }
                m_map_nominations.Add(entry);
                Core.PlayerManager.SendChat($"{ctx.Sender.Controller.PlayerName} nominated {entry.Name}");
            });
        });
    }
}