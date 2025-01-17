using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Corvax.Interfaces.Server;
using Content.Server.Administration.Logs;
using Content.Server.Database;
using Content.Server.Humanoid;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Preferences.Managers
{
    /// <summary>
    /// Sends <see cref="MsgPreferencesAndSettings"/> before the client joins the lobby.
    /// Receives <see cref="MsgSelectCharacter"/> and <see cref="MsgUpdateCharacter"/> at any time.
    /// </summary>
    public sealed class ServerPreferencesManager : IServerPreferencesManager, IPostInjectInit
    {
        [Dependency] private readonly IServerNetManager _netManager = default!;
        [Dependency] private readonly IConfigurationManager _cfg = default!;
        [Dependency] private readonly IServerDbManager _db = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IDependencyCollection _dependencies = default!;
        [Dependency] private readonly IPrototypeManager _protos = default!;
        [Dependency] private readonly IAdminLogManager _logAdmin = default!;
        private IServerSponsorsManager? _sponsorsManager; // Corvax-Sponsors
        [Dependency] private readonly ILogManager _log = default!;
        [Dependency] private readonly UserDbDataManager _userDb = default!;

        // Cache player prefs on the server so we don't need as much async hell related to them.
        private readonly Dictionary<NetUserId, PlayerPrefData> _cachedPlayerPrefs =
            new();

        private ISawmill _sawmill = default!;

        private int MaxCharacterSlots => _cfg.GetCVar(CCVars.GameMaxCharacterSlots);

        public void Init()
        {
            IoCManager.Instance!.TryResolveType(out _sponsorsManager); // Corvax-Sponsors
            _netManager.RegisterNetMessage<MsgPreferencesAndSettings>();
            _netManager.RegisterNetMessage<MsgSelectCharacter>(HandleSelectCharacterMessage);
            _netManager.RegisterNetMessage<MsgUpdateCharacter>(HandleUpdateCharacterMessage);
            _netManager.RegisterNetMessage<MsgDeleteCharacter>(HandleDeleteCharacterMessage);
            _sawmill = _log.GetSawmill("prefs");
        }

        private async void HandleSelectCharacterMessage(MsgSelectCharacter message)
        {
            var index = message.SelectedCharacterIndex;
            var userId = message.MsgChannel.UserId;

            if (!_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) || !prefsData.PrefsLoaded)
            {
                Logger.WarningS("prefs", $"User {userId} tried to modify preferences before they loaded.");
                return;
            }

            if (index < 0 || index >= GetMaxUserCharacterSlots(userId)) // Corvax-Sponsors
            {
                return;
            }

            var curPrefs = prefsData.Prefs!;

            if (!curPrefs.Characters.ContainsKey(index))
            {
                // Non-existent slot.
                return;
            }

            prefsData.Prefs = new PlayerPreferences(curPrefs.Characters, index, curPrefs.AdminOOCColor);

            if (ShouldStorePrefs(message.MsgChannel.AuthType))
            {
                await _db.SaveSelectedCharacterIndexAsync(message.MsgChannel.UserId, message.SelectedCharacterIndex);
            }
        }

        private async void HandleUpdateCharacterMessage(MsgUpdateCharacter message)
        {
            var slot = message.Slot;
            var profile = message.Profile;
            var userId = message.MsgChannel.UserId;

            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (message.Profile == null)
                _sawmill.Error($"User {userId} sent a {nameof(MsgUpdateCharacter)} with a null profile in slot {message.Slot}.");
            else
                await SetProfile(userId, message.Slot, message.Profile);
            if (profile == null)
            {
                Logger.WarningS("prefs",
                    $"User {userId} sent a {nameof(MsgUpdateCharacter)} with a null profile in slot {slot}.");
                return;
            }

            if (!_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) || !prefsData.PrefsLoaded)
            {
                Logger.WarningS("prefs", $"User {userId} tried to modify preferences before they loaded.");
                return;
            }

            if (slot < 0 || slot >= GetMaxUserCharacterSlots(userId)) // Corvax-Sponsors
            {
                return;
            }

            var curPrefs = prefsData.Prefs!;
            var session = _playerManager.GetSessionById(userId);

            if (profile is HumanoidCharacterProfile humanoid)
                if (curPrefs.Characters.TryGetValue(slot, out var storedProfile) && storedProfile is HumanoidCharacterProfile storedHumanoid)
                {
                    if (humanoid.BankBalance != storedHumanoid.BankBalance)
                    {
                        _logAdmin.Add(LogType.UpdateCharacter, LogImpact.High,
                            $"Character update with wrong balance from {message.MsgChannel.UserName}, current balance: {storedHumanoid.BankBalance}, tried to set: {humanoid.BankBalance}");

                        return;
                    }
                }
                else if (humanoid.BankBalance != HumanoidCharacterProfile.DefaultBalance)
                {
                    _logAdmin.Add(LogType.UpdateCharacter, LogImpact.High,
                        $"Character creation with wrong balance from {message.MsgChannel.UserName}, default balance: {HumanoidCharacterProfile.DefaultBalance}, tried to set: {humanoid.BankBalance}");

                    return;
                }

            _logAdmin.Add(LogType.UpdateCharacter, LogImpact.Low, $"Successful character update from {message.MsgChannel.UserName}");

            // Corvax-Sponsors-Start: Ensure removing sponsor markings if client somehow bypassed client filtering
            // WARN! It's not removing markings from DB!
            var sponsorPrototypes = _sponsorsManager != null && _sponsorsManager.TryGetPrototypes(message.MsgChannel.UserId, out var prototypes)
                ? prototypes.ToArray()
                : [];
            profile.EnsureValid(session, _dependencies, sponsorPrototypes);
            // Corvax-Sponsors-End

            var profiles = new Dictionary<int, ICharacterProfile>(curPrefs.Characters)
            {
                [slot] = profile
            };

            prefsData.Prefs = new PlayerPreferences(profiles, slot, curPrefs.AdminOOCColor);

            if (ShouldStorePrefs(message.MsgChannel.AuthType))
            {
                await _db.SaveCharacterSlotAsync(message.MsgChannel.UserId, message.Profile, message.Slot);
            }
        }

        public async Task SetProfile(NetUserId userId, int slot, ICharacterProfile profile)
        {
            if (!_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) || !prefsData.PrefsLoaded)
            {
                _sawmill.Error($"Tried to modify user {userId} preferences before they loaded.");
                return;
            }

            if (slot < 0 || slot >= MaxCharacterSlots)
                return;

            var curPrefs = prefsData.Prefs!;
            var session = _playerManager.GetSessionById(userId);

            var sponsorPrototypes = _sponsorsManager != null && _sponsorsManager.TryGetPrototypes(userId, out var prototypes)
                ? prototypes.ToArray()
                : [];

            profile.EnsureValid(session, _dependencies, sponsorPrototypes);

            var profiles = new Dictionary<int, ICharacterProfile>(curPrefs.Characters)
            {
                [slot] = profile
            };

            prefsData.Prefs = new PlayerPreferences(profiles, slot, curPrefs.AdminOOCColor);

            if (ShouldStorePrefs(session.Channel.AuthType))
                await _db.SaveCharacterSlotAsync(userId, profile, slot);
        }

        private async void HandleDeleteCharacterMessage(MsgDeleteCharacter message)
        {
            var slot = message.Slot;
            var userId = message.MsgChannel.UserId;

            if (!_cachedPlayerPrefs.TryGetValue(userId, out var prefsData) || !prefsData.PrefsLoaded)
            {
                Logger.WarningS("prefs", $"User {userId} tried to modify preferences before they loaded.");
                return;
            }

            if (slot < 0 || slot >= GetMaxUserCharacterSlots(userId)) // Corvax-Sponsors
            {
                return;
            }

            var curPrefs = prefsData.Prefs!;

            // If they try to delete the slot they have selected then we switch to another one.
            // Of course, that's only if they HAVE another slot.
            int? nextSlot = null;
            if (curPrefs.SelectedCharacterIndex == slot)
            {
                // That ! on the end is because Rider doesn't like .NET 5.
                var (ns, profile) = curPrefs.Characters.FirstOrDefault(p => p.Key != message.Slot)!;
                if (profile == null)
                {
                    // Only slot left, can't delete.
                    return;
                }

                nextSlot = ns;
            }

            var arr = new Dictionary<int, ICharacterProfile>(curPrefs.Characters);
            arr.Remove(slot);

            prefsData.Prefs = new PlayerPreferences(arr, nextSlot ?? curPrefs.SelectedCharacterIndex, curPrefs.AdminOOCColor);

            if (ShouldStorePrefs(message.MsgChannel.AuthType))
            {
                if (nextSlot != null)
                {
                    await _db.DeleteSlotAndSetSelectedIndex(userId, slot, nextSlot.Value);
                }
                else
                {
                    await _db.SaveCharacterSlotAsync(userId, null, slot);
                }
            }
        }

        // Should only be called via UserDbDataManager.
        public async Task LoadData(ICommonSession session, CancellationToken cancel)
        {
            if (!ShouldStorePrefs(session.Channel.AuthType))
            {
                // Don't store data for guests.
                var prefsData = new PlayerPrefData
                {
                    PrefsLoaded = true,
                    Prefs = new PlayerPreferences(
                        new[] { new KeyValuePair<int, ICharacterProfile>(0, HumanoidCharacterProfile.Random()) },
                        0, Color.Transparent)
                };

                _cachedPlayerPrefs[session.UserId] = prefsData;
            }
            else
            {
                var prefsData = new PlayerPrefData();
                var loadTask = LoadPrefs();
                _cachedPlayerPrefs[session.UserId] = prefsData;

                await loadTask;

                async Task LoadPrefs()
                {
                    var prefs = await GetOrCreatePreferencesAsync(session.UserId, cancel);
                    prefsData.Prefs = prefs;
                }
            }
        }

        public void FinishLoad(ICommonSession session)
        {
            // This is a separate step from the actual database load.
            // Sanitizing preferences requires play time info due to loadouts.
            // And play time info is loaded concurrently from the DB with preferences.
            var prefsData = _cachedPlayerPrefs[session.UserId];
            DebugTools.Assert(prefsData.Prefs != null);
            prefsData.Prefs = SanitizePreferences(session, prefsData.Prefs, _dependencies);

            prefsData.PrefsLoaded = true;

            var msg = new MsgPreferencesAndSettings();
            msg.Preferences = prefsData.Prefs;
            msg.Settings = new GameSettings
            {
                MaxCharacterSlots = MaxCharacterSlots
            };
            _netManager.ServerSendMessage(msg, session.Channel);
        }

        public void OnClientDisconnected(ICommonSession session)
        {
            _cachedPlayerPrefs.Remove(session.UserId);
        }

        public bool HavePreferencesLoaded(ICommonSession session)
        {
            return _cachedPlayerPrefs.ContainsKey(session.UserId);
        }

        // Corvax-Sponsors-Start: Calculate total available users slots with sponsors
        private int GetMaxUserCharacterSlots(NetUserId userId)
        {
            var maxSlots = _cfg.GetCVar(CCVars.GameMaxCharacterSlots);
            var extraSlots = _sponsorsManager?.GetExtraCharSlots(userId) ?? 0;
            return maxSlots + extraSlots;
        }
        // Corvax-Sponsors-End

        /// <summary>
        /// Tries to get the preferences from the cache
        /// </summary>
        /// <param name="userId">User Id to get preferences for</param>
        /// <param name="playerPreferences">The user preferences if true, otherwise null</param>
        /// <returns>If preferences are not null</returns>
        public bool TryGetCachedPreferences(NetUserId userId,
            [NotNullWhen(true)] out PlayerPreferences? playerPreferences)
        {
            if (_cachedPlayerPrefs.TryGetValue(userId, out var prefs))
            {
                playerPreferences = prefs.Prefs;
                return prefs.Prefs != null;
            }

            playerPreferences = null;
            return false;
        }

        /// <summary>
        /// Retrieves preferences for the given username from storage.
        /// Creates and saves default preferences if they are not found, then returns them.
        /// </summary>
        public PlayerPreferences GetPreferences(NetUserId userId)
        {
            var prefs = _cachedPlayerPrefs[userId].Prefs;
            if (prefs == null)
            {
                throw new InvalidOperationException("Preferences for this player have not loaded yet.");
            }

            return prefs;
        }
        public async Task RefreshPreferencesAsync(ICommonSession session, CancellationToken cancel)
        {
            if (!_cachedPlayerPrefs.TryGetValue(session.UserId, out var prefsData))
                return;

            var loadTask = LoadPrefs();
            _cachedPlayerPrefs[session.UserId] = prefsData;

            await loadTask;
            return;

            async Task LoadPrefs()
            {
                var prefs = await _db.GetPlayerPreferencesAsync(session.UserId, cancel);

                if (prefs != null)
                {
                    prefsData.Prefs = prefs;
                    prefsData.PrefsLoaded = true;

                    var msg = new MsgPreferencesAndSettings
                    {
                        Preferences = prefs,
                        Settings = new GameSettings
                        {
                            MaxCharacterSlots = MaxCharacterSlots
                        }
                    };

                    _netManager.ServerSendMessage(msg, session.Channel);
                }
            }
        }
        private async Task<PlayerPreferences> GetOrCreatePreferencesAsync(NetUserId userId, CancellationToken cancel)
        {
            var prefs = await _db.GetPlayerPreferencesAsync(userId, cancel);
            if (prefs is null)
            {
                return await _db.InitPrefsAsync(userId, HumanoidCharacterProfile.Random(), cancel);
            }

            return prefs;
        }
        /// <summary>
        /// Retrieves preferences for the given username from storage or returns null.
        /// Creates and saves default preferences if they are not found, then returns them.
        /// </summary>
        public PlayerPreferences? GetPreferencesOrNull(NetUserId? userId)
        {
            if (userId == null)
                return null;

            if (_cachedPlayerPrefs.TryGetValue(userId.Value, out var pref))
                return pref.Prefs;
            return null;
        }

        private PlayerPreferences SanitizePreferences(ICommonSession session, PlayerPreferences prefs, IDependencyCollection collection)
        {
            // Clean up preferences in case of changes to the game,
            // such as removed jobs still being selected.
            var sponsorPrototypes = _sponsorsManager != null && _sponsorsManager.TryGetServerPrototypes(session.UserId, out var prototypes) ? prototypes.ToArray() : []; // Corvax-Sponsors
            return new PlayerPreferences(prefs.Characters.Select(p =>
            {
                return new KeyValuePair<int, ICharacterProfile>(p.Key, p.Value.Validated(session, collection, sponsorPrototypes));
            }), prefs.SelectedCharacterIndex, prefs.AdminOOCColor);
        }

        public IEnumerable<KeyValuePair<NetUserId, ICharacterProfile>> GetSelectedProfilesForPlayers(
            List<NetUserId> usernames)
        {
            return usernames
                .Select(p => (_cachedPlayerPrefs[p].Prefs, p))
                .Where(p => p.Prefs != null)
                .Select(p => new KeyValuePair<NetUserId, ICharacterProfile>(p.p, p.Prefs!.SelectedCharacter));
        }

        internal static bool ShouldStorePrefs(LoginType loginType)
        {
            return loginType.HasStaticUserId();
        }

        private sealed class PlayerPrefData
        {
            public bool PrefsLoaded;
            public PlayerPreferences? Prefs;
        }

        void IPostInjectInit.PostInject()
        {
            _userDb.AddOnLoadPlayer(LoadData);
            _userDb.AddOnFinishLoad(FinishLoad);
            _userDb.AddOnPlayerDisconnect(OnClientDisconnected);
        }
    }
}
