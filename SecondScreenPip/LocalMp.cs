// Встроенный аналог мода LocalMultiplayer (Zehs, лицензия MIT):
// два окна R.E.P.O. под одним Steam-аккаунтом заходят в одну игру через Photon.
// Переписано без внешних зависимостей (без Newtonsoft), совместимо со старым csc из Windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace SecondScreenPip
{
    internal static class LocalMp
    {
        private class Acc
        {
            public string Name;
            public ulong Id;
            public int Color;
        }

        private static ManualLogSource _log;
        private static ConfigEntry<string> _appIdRealtime;
        private static ConfigEntry<string> _appIdVoice;

        private static string _stateFile;
        private static Mutex _mutex;

        private static string _realName = "Player";
        private static ulong _realId;
        private static bool _initialized;

        // текущий поддельный аккаунт этого окна
        private static Acc _spoof;
        private static bool IsSpoof { get { return _spoof != null; } }

        // ---------------- запуск ----------------

        public static void Init(ConfigFile cfg, ManualLogSource log)
        {
            _log = log;

            string dir = Path.Combine(Application.persistentDataPath, "Second Screen PiP");
            try { Directory.CreateDirectory(dir); } catch { }
            _stateFile = Path.Combine(dir, "state.txt");
            try { _mutex = new Mutex(false, "Local\\SecondScreenPip_State"); } catch { }

            // Берём App ID из конфига LocalMultiplayer, если он остался от старой установки.
            string oldCfg = Path.Combine(Path.Combine(Application.persistentDataPath, "LocalMultiplayer"), "global.cfg");
            _appIdRealtime = cfg.Bind("Photon", "AppIdRealtime", ReadOldCfg(oldCfg, "AppIdRealtime"),
                "App ID вашего Photon-приложения типа Pun (одинаковый в обоих окнах).");
            _appIdVoice = cfg.Bind("Photon", "AppIdVoice", ReadOldCfg(oldCfg, "AppIdVoice"),
                "App ID вашего Photon-приложения типа Voice.");

            Harmony h = new Harmony("local.secondscreenpip.localmp");

            Hook(h, "DataDirector", "PhotonSetAppId", "PhotonSetAppId_Post", false);
            Hook(h, "DataDirector", "SaveSettings", "BlockIfSpoof_Pre", true);
            Hook(h, "DataDirector", "ColorSetBody", "ColorSetBody_Pre", true);
            Hook(h, "DataDirector", "ColorGetBody", "ColorGetBody_Pre", true);
            Hook(h, "InputManager", "SaveCurrentKeyBindings", "BlockIfSpoof_Pre", true);
            Hook(h, "MetaManager", "Save", "BlockIfSpoof_Pre", true);
            Hook(h, "NetworkManager", "OnDisconnected", "OnDisconnected_Post", false);
            Hook(h, "PlatformManager", "SetPhotonAuthTicket", "SetPhotonAuthTicket_Post", false);
            Hook(h, "PlayerAvatar", "AddToStatsManager", "AddToStatsManager_Pre", true);
            Hook(h, "MenuPageMain", "ButtonEventJoinGame", "JoinGame_Pre", true);
            Hook(h, "SteamLobbyManager", "OnLobbyCreated", "OnLobbyCreated_Post", false);
            HookSteamLobbyAwake(h);

            try
            {
                MethodBase name = AccessTools.PropertyGetter(typeof(SteamClient), "Name");
                MethodBase sid = AccessTools.PropertyGetter(typeof(SteamClient), "SteamId");
                h.Patch(name, prefix: Hm("SteamName_Pre"));
                h.Patch(sid, prefix: Hm("SteamId_Pre"));
            }
            catch (Exception e) { _log.LogError("LocalMp: SteamClient patch failed: " + e.Message); }

            if (string.IsNullOrEmpty(_appIdRealtime.Value))
                _log.LogWarning("LocalMp: не задан Photon AppIdRealtime в local.secondscreenpip.cfg - вторая копия не сможет зайти в игру.");
        }

        private static HarmonyMethod Hm(string method)
        {
            MethodInfo mi = typeof(LocalMp).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi == null) throw new MissingMethodException("LocalMp." + method);
            return new HarmonyMethod(mi);
        }

        private static void Hook(Harmony h, string type, string method, string patch, bool prefix)
        {
            try
            {
                Type t = AccessTools.TypeByName(type);
                MethodBase m = t == null ? null : AccessTools.Method(t, method);
                if (m == null) { _log.LogWarning("LocalMp: не найден " + type + "." + method); return; }
                if (prefix) h.Patch(m, prefix: Hm(patch));
                else h.Patch(m, postfix: Hm(patch));
            }
            catch (Exception e) { _log.LogError("LocalMp: patch " + type + "." + method + ": " + e.Message); }
        }

        private static void HookSteamLobbyAwake(Harmony h)
        {
            try
            {
                Type t = AccessTools.TypeByName("SteamLobbyManager");
                MethodBase m = t == null ? null : AccessTools.Method(t, "Awake");
                if (m == null) { _log.LogWarning("LocalMp: не найден SteamLobbyManager.Awake"); return; }
                HarmonyMethod hm = Hm("SteamLobbyAwake_Post");
                hm.priority = Priority.First;
                h.Patch(m, postfix: hm);
            }
            catch (Exception e) { _log.LogError("LocalMp: patch SteamLobbyManager.Awake: " + e.Message); }
        }

        private static string ReadOldCfg(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return "";
                foreach (string line in File.ReadAllLines(path))
                {
                    string l = line.Trim();
                    if (l.StartsWith("#") || l.StartsWith("[")) continue;
                    int eq = l.IndexOf('=');
                    if (eq > 0 && l.Substring(0, eq).Trim() == key) return l.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return "";
        }

        // ---------------- патчи ----------------

        private static void PhotonSetAppId_Post()
        {
            AppSettings s = PhotonNetwork.PhotonServerSettings.AppSettings;
            if (!string.IsNullOrEmpty(_appIdRealtime.Value)) s.AppIdRealtime = _appIdRealtime.Value;
            if (!string.IsNullOrEmpty(_appIdVoice.Value)) s.AppIdVoice = _appIdVoice.Value;
        }

        private static bool BlockIfSpoof_Pre()
        {
            return !IsSpoof;
        }

        private static bool ColorSetBody_Pre(int colorID)
        {
            if (!IsSpoof) return true;
            SetSpoofColor(colorID);
            return false;
        }

        private static bool ColorGetBody_Pre(ref int __result)
        {
            if (!IsSpoof) return true;
            __result = _spoof.Color;
            return false;
        }

        private static bool SteamName_Pre(ref string __result)
        {
            if (!IsSpoof) return true;
            __result = _spoof.Name;
            return false;
        }

        private static bool SteamId_Pre(ref SteamId __result)
        {
            if (!IsSpoof) return true;
            __result = _spoof.Id;
            return false;
        }

        private static void OnDisconnected_Post()
        {
            Unassign();
        }

        private static void SetPhotonAuthTicket_Post()
        {
            try
            {
                object pm = Traverse.Create(AccessTools.TypeByName("PlatformManager")).Field("instance").GetValue();
                object platform = Traverse.Create(pm).Field("activePlatform").GetValue();
                bool ok = Traverse.Create(platform).Method("IsInitialised").GetValue<bool>();
                // Отключаем проверку Steam-тикета, иначе Photon не пустит один аккаунт дважды.
                if (ok) PhotonNetwork.AuthValues.AuthType = CustomAuthenticationType.None;
            }
            catch (Exception e) { _log.LogWarning("LocalMp: SetPhotonAuthTicket: " + e.Message); }
        }

        private static void AddToStatsManager_Pre()
        {
            PhotonNetwork.NickName = SteamClient.Name;
        }

        private static void SteamLobbyAwake_Post()
        {
            Initialize();
        }

        private static void OnLobbyCreated_Post(ref Result _result, ref Lobby _lobby)
        {
            if (_result != Result.OK) return;
            ulong id = _lobby.Id;
            WithState(delegate (List<Acc> accounts, List<ulong> inUse, ref ulong lobby)
            {
                lobby = id;
                inUse.Clear();
            });
        }

        private static bool JoinGame_Pre()
        {
            try
            {
                Assign();

                ulong lobbyId = 0;
                WithState(delegate (List<Acc> accounts, List<ulong> inUse, ref ulong lobby) { lobbyId = lobby; });

                Type t = AccessTools.TypeByName("SteamLobbyManager");
                object inst = Traverse.Create(t).Field("instance").GetValue();
                if (inst == null) { _log.LogWarning("LocalMp: SteamLobbyManager.instance == null"); return true; }

                MethodInfo m = AccessTools.Method(t, "OnGameLobbyJoinRequested");
                m.Invoke(inst, new object[] { new Lobby(lobbyId), SteamClient.SteamId });
                return false;
            }
            catch (Exception e)
            {
                _log.LogError("LocalMp: JoinGame: " + e);
                return true;
            }
        }

        // ---------------- аккаунты ----------------

        private static void Initialize()
        {
            if (_initialized) return;
            _realName = SteamClient.Name;
            _realId = SteamClient.SteamId;

            WithState(delegate (List<Acc> accounts, List<ulong> inUse, ref ulong lobby)
            {
                for (int i = accounts.Count; i < 5; i++)
                    accounts.Add(new Acc { Name = "Player " + (i + 2), Id = RandomSteamId(), Color = 0 });
            });

            Application.quitting += Unassign;
            _initialized = true;
        }

        private static void Assign()
        {
            if (IsSpoof) return;

            Acc chosen = null;
            WithState(delegate (List<Acc> accounts, List<ulong> inUse, ref ulong lobby)
            {
                foreach (Acc a in accounts)
                {
                    if (!inUse.Contains(a.Id)) { chosen = a; break; }
                }
                if (chosen == null)
                {
                    chosen = new Acc { Name = "Player " + UnityEngine.Random.Range(100, 999), Id = RandomSteamId(), Color = 0 };
                }
                if (!inUse.Contains(chosen.Id)) inUse.Add(chosen.Id);
            });

            _spoof = chosen;
            PhotonNetwork.NickName = chosen.Name;
            _log.LogInfo("LocalMp: аккаунт 2-го окна: " + chosen.Name + " (" + chosen.Id + ")");
        }

        private static void Unassign()
        {
            if (!IsSpoof) return;
            Acc a = _spoof;
            WithState(delegate (List<Acc> accounts, List<ulong> inUse, ref ulong lobby) { inUse.Remove(a.Id); });
            _spoof = null;
            PhotonNetwork.NickName = _realName;
        }

        private static void SetSpoofColor(int color)
        {
            if (!IsSpoof) return;
            _spoof.Color = color;
            Acc cur = _spoof;
            WithState(delegate (List<Acc> accounts, List<ulong> inUse, ref ulong lobby)
            {
                foreach (Acc a in accounts) if (a.Id == cur.Id) a.Color = color;
            });
        }

        private static ulong RandomSteamId()
        {
            System.Random r = new System.Random();
            ulong part = (ulong)r.Next(0, int.MaxValue) + (ulong)r.Next(0, int.MaxValue);
            return 76561197960265728UL + part;
        }

        // ---------------- общий файл состояния (между двумя окнами) ----------------

        private delegate void StateAction(List<Acc> accounts, List<ulong> inUse, ref ulong lobby);

        private static void WithState(StateAction action)
        {
            bool locked = false;
            try
            {
                if (_mutex != null) locked = _mutex.WaitOne(5000);

                List<Acc> accounts = new List<Acc>();
                List<ulong> inUse = new List<ulong>();
                ulong lobby = 0;
                Load(accounts, inUse, ref lobby);
                action(accounts, inUse, ref lobby);
                Save(accounts, inUse, lobby);
            }
            catch (Exception e)
            {
                _log.LogError("LocalMp state: " + e.Message);
            }
            finally
            {
                if (locked) { try { _mutex.ReleaseMutex(); } catch { } }
            }
        }

        private static void Load(List<Acc> accounts, List<ulong> inUse, ref ulong lobby)
        {
            if (!File.Exists(_stateFile)) return;
            foreach (string line in File.ReadAllLines(_stateFile, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq);
                string v = line.Substring(eq + 1);
                if (k == "lobby") ulong.TryParse(v, out lobby);
                else if (k == "use") { ulong u; if (ulong.TryParse(v, out u)) inUse.Add(u); }
                else if (k == "acc")
                {
                    string[] p = v.Split('|');
                    ulong id; int col;
                    if (p.Length >= 3 && ulong.TryParse(p[1], out id))
                    {
                        int.TryParse(p[2], out col);
                        accounts.Add(new Acc { Name = p[0], Id = id, Color = col });
                    }
                }
            }
        }

        private static void Save(List<Acc> accounts, List<ulong> inUse, ulong lobby)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("lobby=").Append(lobby).Append('\n');
            foreach (Acc a in accounts) sb.Append("acc=").Append(a.Name.Replace('|', '_')).Append('|').Append(a.Id).Append('|').Append(a.Color).Append('\n');
            foreach (ulong u in inUse) sb.Append("use=").Append(u).Append('\n');
            File.WriteAllText(_stateFile, sb.ToString(), Encoding.UTF8);
        }
    }
}
