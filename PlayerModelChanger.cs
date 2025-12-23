using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using System.Text.Json;
using System.IO;

namespace MS_PlayerModelChanger
{
    public class PlayerModelChanger : IModSharpModule, IGameListener, IEventListener
    {
        public string DisplayName => "PlayerModelChanger";
        public string DisplayAuthor => "Kianya";

        int IEventListener.ListenerVersion => IEventListener.ApiVersion;
        int IEventListener.ListenerPriority => 0;

        int IGameListener.ListenerVersion => IGameListener.ApiVersion;
        int IGameListener.ListenerPriority => 0;

        public PlayerModelChanger(ISharedSystem sharedSystem, string dllPath, string sharpPath, Version version, IConfiguration coreConfiguration, bool hotReload)
        {
            _dllPath = dllPath;
            _modules = sharedSystem.GetSharpModuleManager();
            _client_Manager = sharedSystem.GetClientManager();
            _hooks = sharedSystem.GetHookManager();
            _hotReload = hotReload;
            _modSharp = sharedSystem.GetModSharp();
            _eventManager = sharedSystem.GetEventManager();
            _logger = sharedSystem.GetLoggerFactory().CreateLogger<PlayerModelChanger>();
        }

        private IDisposable? _callback;
        private readonly string _dllPath;
        private readonly IModSharp _modSharp;
        private readonly ISharpModuleManager _modules;
        private readonly IClientManager _client_Manager;
        private readonly IEventManager _eventManager;
        private readonly IHookManager _hooks;
        private readonly ILogger<PlayerModelChanger> _logger;
        private readonly bool _hotReload;


        private IModSharpModuleInterface<IClientPreference>? _clientpref;

        private readonly string[] precache_models_path = new string[65];

        // New: map player slot -> selected model path (null when none)
        private readonly string?[] playerModelBySlot = new string?[65];

        public bool Init()
        {
            _eventManager.InstallEventListener(this);
            _modSharp.InstallGameListener(this);

            _client_Manager.InstallCommandCallback("model", ModelChangerCommand);
            _client_Manager.InstallCommandCallback("modellist", OnModelListCheck);

            _hooks.PlayerSpawnPost.InstallForward(OnPlayerSpawn);

            return true;
        }

        public void PostInit()
        {
            RegisterEvents();
            LoadPrecacheModels();
        }

        private void RegisterEvents()
        {
            _eventManager.HookEvent("round_start");
        }

        private void LoadPrecacheModels()
        {
            // Determine module directory from the provided dll path.
            var moduleDir = Path.GetDirectoryName(_dllPath);
            if (string.IsNullOrEmpty(moduleDir))
            {
                _logger.LogWarning("Module directory could not be determined from dllPath '{DllPath}'. Will not create or load model-list.json.", _dllPath);
                return;
            }

            // Place model-list.json inside the 'PlayerModelChanger' subfolder under the module directory.
            var moduleJsonPath = Path.Combine(moduleDir, "PlayerModelChanger", "model-list.json");
            var moduleJsonDir = Path.GetDirectoryName(moduleJsonPath) ?? moduleDir;

            // Ensure the target folder exists (safe to call even if it already exists).
            try
            {
                Directory.CreateDirectory(moduleJsonDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create module folder {Folder}. Continuing; file operations may fail.", moduleJsonDir);
            }

            // If file does not exist, create a default template in the PlayerModelChanger folder.
            if (!File.Exists(moduleJsonPath))
            {
                var templateObj = new
                {
                    paths = new[]
                    {
                        "characters/kianya/vrc/lime_obsidian/limeobsidian.vmdl",
                        "characters/models/kianya/vrc/chiffon_marshmallow/chiffon_marshmallow.vmdl"
                    }
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                var templateJson = JsonSerializer.Serialize(templateObj, options);

                try
                {
                    File.WriteAllText(moduleJsonPath, templateJson);
                    _logger.LogInformation("Created default model-list.json at {Path}", moduleJsonPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create model-list.json in {Path}. Will not attempt creation in other directories.", moduleJsonPath);
                    return;
                }
            }

            // At this point moduleJsonPath should exist (either previously or newly created).
            if (!File.Exists(moduleJsonPath))
            {
                _logger.LogWarning("model-list.json not found in PlayerModelChanger folder after attempted creation.");
                return;
            }

            try
            {
                var json = File.ReadAllText(moduleJsonPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("paths", out var pathsElem) && pathsElem.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var p in pathsElem.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.String)
                            continue;

                        var path = p.GetString();
                        if (string.IsNullOrWhiteSpace(path))
                            continue;

                        if (index < precache_models_path.Length)
                        {
                            precache_models_path[index++] = path!;
                        }
                        else
                        {
                            _logger.LogWarning("precache_models_path capacity reached; skipping additional paths.");
                            break;
                        }
                    }

                    // fill remaining entries with empty strings for determinism
                    for (var i = 0; i < precache_models_path.Length; i++)
                    {
                        if (precache_models_path[i] == null)
                            precache_models_path[i] = string.Empty;
                    }

                    _logger.LogInformation("Loaded {Count} precache model paths from {Path}", index, moduleJsonPath);
                }
                else
                {
                    _logger.LogWarning("model-list.json exists but does not contain a 'paths' array.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read or parse model-list.json at {Path}", moduleJsonPath);
            }
        }

        public void OnResourcePrecache()
        {
            // Precache any models loaded from model-list.json
            foreach (var path in precache_models_path)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        _modSharp.PrecacheResource(path);
                        _logger.LogInformation($"[ModelChanger] : PrecacheResource ({path})");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to precache resource {Path}", path);
                    }
                }
            }
        }

        public void OnAllModulesLoaded(string name)
        {
            // Attempt to cache ClientPreferences interface after other modules are loaded.
            try
            {
                _clientpref = _modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
                if (_clientpref?.Instance is { } inst)
                {
                    _callback = inst.ListenOnLoad(OnCookieLoad);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while obtaining ClientPreferences interface in OnAllModulesLoaded.");
            }
        }

        void OnLibraryConnected(string name)
        {
            _logger.LogInformation($"Module {name} is loaded.");

            // If ClientPreferences just connected, cache the required interface and listen for cookie loads.
            if (string.Equals(name, "ClientPreferences", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _clientpref = _modules.GetRequiredSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
                    if (_clientpref?.Instance is { } inst)
                    {
                        _callback = inst.ListenOnLoad(OnCookieLoad);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get required ClientPreferences interface when library connected.");
                }
            }
        }

        public void OnLibraryDisconnect(string name)
        {
            // Clear cached interface when ClientPreferences unloads
            if (string.Equals(name, "ClientPreferences", StringComparison.OrdinalIgnoreCase))
            {
                _callback?.Dispose();
                _callback = null;
                _clientpref = null;
            }
        }

        private void OnCookieLoad(IGameClient client)
        {
            // When ClientPreferences notifies a cookie load, read "PlayerDefaultModel" and apply to our slot cache.
            if (client == null)
                return;

            var cp = _clientpref?.Instance;
            if (cp == null)
                return;

            if (!cp.IsLoaded(client.SteamId))
                return;

            var cookie = cp.GetCookie(client.SteamId, "PlayerDefaultModel");
            if (cookie == null)
                return;

            var modelPath = cookie.GetString();
            if (string.IsNullOrWhiteSpace(modelPath))
                return;

            var player = client.GetPlayerController();
            if (player == null || !player.IsValid())
                return;

            var slot = player.PlayerSlot;
            if (slot < 0 || slot >= playerModelBySlot.Length)
                return;

            playerModelBySlot[slot] = modelPath;
            _logger.LogInformation("[ModelChanger] : Loaded PlayerDefaultModel cookie for {Player} (slot {Slot}) => {Model}", player.PlayerName, slot, modelPath);
        }

        public void Shutdown()
        {
            _client_Manager.RemoveCommandCallback("model", ModelChangerCommand);
            _client_Manager.InstallCommandCallback("modellist", OnModelListCheck);

            _hooks.PlayerSpawnPost.RemoveForward(OnPlayerSpawn);

            _eventManager.RemoveEventListener(this);
            _modSharp.RemoveGameListener(this);
            _callback?.Dispose();
        }

        public void FireGameEvent(IGameEvent e)
        {
            var eventName = e.Name?.ToLowerInvariant();

            switch (eventName)
            {
                case "round_start":
                    OnRoundStart(e);
                    break;
            }
        }

        private void OnRoundStart(IGameEvent e)
        {

        }

        public void OnClientPutInServer(IGameClient client)
        {
            var player = client.GetPlayerController();
            if (player != null && player.IsValid())
            {
                var slot = player.PlayerSlot;
                if (slot >= 0 && slot < playerModelBySlot.Length)
                {
                    // initialize to null (explicitly), modelPath starts as null per your requirement
                    playerModelBySlot[slot] = null;

                    // If ClientPreferences is available and has a saved model cookie, load it into our slot cache.
                    try
                    {
                        var cp = _clientpref?.Instance;
                        if (cp != null && cp.IsLoaded(client.SteamId))
                        {
                            var cookie = cp.GetCookie(client.SteamId, "PlayerDefaultModel");
                            if (cookie != null)
                            {
                                var modelPath = cookie.GetString();
                                if (!string.IsNullOrWhiteSpace(modelPath))
                                {
                                    playerModelBySlot[slot] = modelPath;
                                    _logger.LogInformation("[ModelChanger] : Applied saved cookie model for {Player} (slot {Slot}) => {Model}", player.PlayerName, slot, modelPath);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error while reading ClientPreferences cookie for player {Player}", player.PlayerName);
                    }
                }
            }
        }

        private void OnPlayerSpawn(IPlayerSpawnForwardParams @params)
        {
            if (@params.Client is not { } client)
                return;

            // ignore bots/HLTV
            if (client.IsFakeClient || client.IsHltv)
                return;

            var player = client.GetPlayerController();
            if (player == null || !player.IsValid())
                return;

            var slot = player.PlayerSlot;
            if (slot < 0 || slot >= playerModelBySlot.Length)
                return;

            var recipient = new RecipientFilter(client);
            var playerName = player.PlayerName ?? "<unknown>";
            var storedModel = playerModelBySlot[slot];

            if (string.IsNullOrWhiteSpace(storedModel))
                return;

            // player used the !model command previously — apply and notify
            var shortName = Path.GetFileNameWithoutExtension(storedModel);

            try
            {
                client.GetPlayerController()?.GetPlayerPawn()?.SetModel(storedModel);

                _modSharp.PrintChannelFilter(HudPrintChannel.Chat,
                    $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.White}Applied model: {shortName}",
                    recipient);

                //_logger.LogInformation("[ModelChanger] : Player {Player} (slot {Slot}) applied stored model {Model}", playerName, slot, shortName);
            }
            catch (Exception ex)
            {
                _modSharp.PrintChannelFilter(HudPrintChannel.Chat,
                    $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.Red}Failed to apply model '{shortName}' for {playerName}. See server log.",
                    recipient);

                _logger.LogWarning(ex, "Failed to SetModel for player {Player} (slot {Slot}) with model {Model}", playerName, slot, storedModel);
            }
        }



        public ECommandAction ModelChangerCommand(IGameClient client, StringCommand command)
        {
            var clientEnt = client.GetPlayerController()?.GetPlayerPawn();
            RecipientFilter recipient = new(client);

            if (clientEnt == null || !clientEnt.IsValid())
            {
                _modSharp.PrintChannelFilter(HudPrintChannel.Chat, $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.Red}Player is not valid", recipient);
                return ECommandAction.Stopped;
            }

            string? clientArg;
            try
            {
                // Guard against GetArg throwing ArgumentOutOfRangeException
                clientArg = command.GetArg(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                _modSharp.PrintChannelFilter(HudPrintChannel.Chat,
                    $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.Yellow}Usage: model <index>",
                    recipient);

                return ECommandAction.Stopped;
            }

            if (string.IsNullOrWhiteSpace(clientArg))
            {
                _modSharp.PrintChannelFilter(HudPrintChannel.Chat,
                    $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.Yellow}Usage: model <index>",
                    recipient);
                return ECommandAction.Stopped;
            }

            if (int.TryParse(clientArg, out var idx) &&
                (uint)idx < (uint)precache_models_path.Length &&
                !string.IsNullOrWhiteSpace(precache_models_path[idx]))
            {
                var modelPath = precache_models_path[idx];

                // also store chosen model for this player's slot so OnPlayerSpawn will reapply it
                var player = client.GetPlayerController();
                if (player != null && player.IsValid())
                {
                    var slot = player.PlayerSlot;
                    if (slot >= 0 && slot < playerModelBySlot.Length)
                        playerModelBySlot[slot] = modelPath;
                }

                // Persist choice to ClientPreferences cookie if interface available
                try
                {
                    var cp = _clientpref?.Instance;
                    if (cp != null)
                    {
                        // ensure the module has loaded this client's cookies
                        if (!cp.IsLoaded(client.SteamId))
                        {
                            // If not loaded we still set the cookie; when loaded ListenOnLoad will notify
                            cp.SetCookie(client.SteamId, "PlayerDefaultModel", modelPath);
                        }
                        else
                        {
                            cp.SetCookie(client.SteamId, "PlayerDefaultModel", modelPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save ClientPreferences cookie for client {SteamId}", client.SteamId);
                }

                // extract the last path segment and remove extension (works with '/' or '\')
                var shortName = Path.GetFileNameWithoutExtension(modelPath);

                clientEnt.SetModel(modelPath);

                _modSharp.PrintChannelFilter(HudPrintChannel.Chat,
                    $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.White} Change model into {shortName}",
                    recipient);

                _logger.LogInformation($"[ModelChanger] : Player {client.GetPlayerController()?.PlayerName} changed model into ({shortName})");
            }
            else
            {
                _modSharp.PrintChannelFilter(HudPrintChannel.Chat, "Invalid index or model not found", recipient);
            }

            return ECommandAction.Stopped;
        }

        public ECommandAction OnModelListCheck(IGameClient client, StringCommand command)
        {
            RecipientFilter recipient = new(client);
            _modSharp.PrintChannelFilter(HudPrintChannel.Chat,
                $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.Yellow}Available Models : ",
                recipient);
            for (var i = 0; i < precache_models_path.Length; i++)
            {
                var path = precache_models_path[i];
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var shortName = Path.GetFileNameWithoutExtension(path);
                    _modSharp.PrintChannelFilter(HudPrintChannel.Chat,
                        $"{ChatColor.White}[{ChatColor.Green}PlayerModelChanger{ChatColor.White}] {ChatColor.White}{i} : {shortName}",
                        recipient);
                }
            }
            return ECommandAction.Stopped;
        }


    }
}
