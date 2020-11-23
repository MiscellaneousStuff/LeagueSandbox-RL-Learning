using GameServerCore;
using GameServerCore.Enums;
using GameServerCore.Maps;
using GameServerCore.Packets.Handlers;
using GameServerCore.Packets.Interfaces;
using LeagueSandbox.GameServer.API;
using LeagueSandbox.GameServer.Chatbox;
using LeagueSandbox.GameServer.Logging;
using LeagueSandbox.GameServer.Maps;
using LeagueSandbox.GameServer.Packets;
using LeagueSandbox.GameServer.Players;
using LeagueSandbox.GameServer.Scripting.CSharp;
using log4net;
using LeagueSandbox.GameServer.Items;
using PacketDefinitions420;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
// using System.Runtime.Serialization;
using Timer = System.Timers.Timer;
using GameServerCore.Packets.PacketDefinitions;
using GameServerCore.Packets.PacketDefinitions.Requests;
using LeagueSandbox.GameServer.Packets.PacketHandlers;
using System.Numerics;
using GameMaths;
using GameServerCore.Domain.GameObjects;
using LeagueSandbox.GameServer.GameObjects.Other;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;
using Newtonsoft.Json;

namespace LeagueSandbox.GameServer
{
    public class Game : IGame
    {

        private ILog _logger;

        public bool IsRunning { get; private set; }
        public bool IsPaused { get; set; }

        private Timer _pauseTimer;
        public long PauseTimeLeft { get; private set; }
        private bool _autoResumeCheck;
        public bool SetToExit { get; set; }

        // Redis
        ConnectionMultiplexer redis;
        IDatabase db;
        public int PlayersReady { get; private set; }

        public float GameTime { get; private set; }
        private float _nextSyncTime = 10 * 1000;

        private PacketServer _packetServer;
        public IPacketReader PacketReader { get; private set; }
        public NetworkHandler<ICoreRequest> RequestHandler { get; }
        public NetworkHandler<ICoreResponse> ResponseHandler { get; }
        public IPacketNotifier PacketNotifier { get; private set; }
        public IObjectManager ObjectManager { get; private set; }
        public IProtectionManager ProtectionManager { get; private set; }
        public IMap Map { get; private set; }

        public Config Config { get; protected set; }
        protected const double REFRESH_RATE = 1000.0 / 30.0; // 30 fps

        // Object managers
        public ItemManager ItemManager { get; private set; }
        // Other managers
        internal ChatCommandManager ChatCommandManager { get; private set; }
        public IPlayerManager PlayerManager { get; private set; }
        internal NetworkIdManager NetworkIdManager { get; private set; }
        //Script Engine
        internal CSharpScriptEngine ScriptEngine { get; private set; }

        private Stopwatch _lastMapDurationWatch;

        private List<GameScriptTimer> _gameScriptTimers;

        private int _human_count; // Number of lol clients to connect
        private int _agent_count; // Number of AI agents to connect
        private string _serverHost; // Server host IP
        private float _limitRate; // Milliseconds between actions/observations
        private float _multiplier; // Number of actions/observations per second
        private ReplayContainer replay_container;   // Used to record agent actions only ...
                                                    // ... (doesn't currently record human player actions)
        private string _replay_path;
        private ushort _redis_port;
        
        public Game(string serverHost="127.0.0.1", int human_count=1,
            int agent_count=0, float multiplier=4.0f, string replay_path="",
            ushort redis_port=6379)
        {
            _logger = LoggerProvider.GetLogger();
            ItemManager = new ItemManager();
            ChatCommandManager = new ChatCommandManager(this);
            NetworkIdManager = new NetworkIdManager();
            PlayerManager = new PlayerManager(this);
            ScriptEngine = new CSharpScriptEngine();
            RequestHandler = new NetworkHandler<ICoreRequest>();
            ResponseHandler = new NetworkHandler<ICoreResponse>();
            _human_count = human_count;
            _agent_count = agent_count;
            _serverHost = serverHost;
            _multiplier = multiplier;
            _replay_path = replay_path;
            _redis_port = redis_port;
        }

        public void Initialize(Config config, PacketServer server)
        {
            _logger.Info("Loading Config.");
            Config = config;

            _gameScriptTimers = new List<GameScriptTimer>();

            ChatCommandManager.LoadCommands();

            ObjectManager = new ObjectManager(this);
            ProtectionManager = new ProtectionManager(this);
            Map = new Map(this);
            ApiFunctionManager.SetGame(this);
            ApiEventManager.SetGame(this);
            IsRunning = false;

            Map.Init();

            _logger.Info("Add players");
            foreach (var p in Config.Players)
            {
                _logger.Info("Player " + p.Value.Name + " Added: " + p.Value.Champion);
                ((PlayerManager)PlayerManager).AddPlayer(p);
            }

            _pauseTimer = new Timer
            {
                AutoReset = true,
                Enabled = false,
                Interval = 1000
            };
            _pauseTimer.Elapsed += (sender, args) => PauseTimeLeft--;
            PauseTimeLeft = 30 * 60; // 30 minutes

            // TODO: GameApp should send the Response/Request handlers
            _packetServer = server;
            // TODO: switch the notifier with ResponseHandler
            PacketNotifier = new PacketNotifier(_packetServer.PacketHandlerManager, Map.NavigationGrid);
            InitializePacketHandlers();

            // Init Redis Here
            redis = ConnectionMultiplexer.Connect(String.Format("{0}:{1}", _serverHost, _redis_port));
            db = redis.GetDatabase();

            // Inform pylol game has started to human clients can join
            db.KeyDelete("observation");
            db.ListLeftPush("observation", "\"clients_join\"");

            _logger.Info("Game is ready.");

            // Set multiplier here
            _limitRate = 1000.0f / _multiplier;
            
            // Fake add second client
            uint humanObserver = 0; // 0 = false, 1 = true
            uint agentCount = 0;
            int humanId = -1;

            if (_human_count > 0)
            {
                humanObserver = 1;
                humanId = 0;
            }
            else
            {
                humanObserver = 0;
                humanId = -1;
            }

            if (_agent_count > 0)
            {
                agentCount = (uint) _agent_count;
            }
            else
            {
                agentCount = 0;
            }

            Console.WriteLine(
              String.Format("HUMAN COUNT: {0}, AGENT COUNT: {1}, humanId: {2}",
              humanObserver, agentCount, humanId
            ));
            for (int i = 0; i < agentCount + humanObserver; i++) {
                if (i != humanId)
                {
                    ((PlayerManager)PlayerManager)._players[(int)i].Item2.IsStartedClient = true;
                    ((PlayerManager)PlayerManager)._players[(int)i].Item2.IsMatchingVersion = true;
                    new HandleStartGame(this).HandlePacket((int)i + 1, new StartGameRequest());
                    new HandleSync(this).HandlePacket((int)i + 1, new SynchVersionRequest(0, (uint) i, "4.20.0.315"));
                    new HandleSpawn(this).HandlePacket((int)i + 1, new SpawnRequest());
                }
            }

            // Setup AI here
            for (uint i = 0; i < agentCount + humanObserver; i++)
            {
                //if (i != humanId) {
                    UserBuy(i + 1, 1055); // NOTE: Doesn't work properly
                    for (byte j = 0; j < 1; j++)
                    {
                        UserUpgradeSpell(i + 1, 0); // NOTE: Doesn't work properly
                        UserUpgradeSpell(i + 1, 1); // NOTE: Doesn't work properly
                        UserUpgradeSpell(i + 1, 2); // NOTE: Doesn't work properly
                        UserUpgradeSpell(i + 1, 3); // NOTE: Doesn't work properly
                    }
                //}
            }

            // If there's a replay, check the file exists then setup the data here
            if (!String.IsNullOrEmpty(_replay_path))
            {
                Console.WriteLine("REPLAY FILE REQUESTED");
                try
                {
                    Console.WriteLine("ATTEMPTING TO READ REPLAY FILE");
                    using (StreamReader r = new StreamReader(_replay_path))
                    {
                        Console.WriteLine("ATTEMPTING TO DECODE REPLAY FILE");
                        string json = r.ReadToEnd();
                        replay_container = JsonConvert.DeserializeObject<ReplayContainer>(json);
                        
                        /*
                        Console.WriteLine(String.Format("REPLAYING FILE: {0} {1} {2} {3}",
                            _replay_path,
                            replay_container.info.map,
                            replay_container.info.players,
                            replay_container.info.multiplier));
                        */

                        replay_last_game_time = replay_container.actions[0].game_time;
                        _limitRate = 1000.0f / replay_container.info.multiplier;
                        
                        Console.WriteLine("REPLAY FILE DECODED, READY TO GO");
                        Console.WriteLine(String.Format("ACTION COUNT FROM REPLAY FILE {0}", replay_container.actions.Count));
                        /*
                        Console.WriteLine(String.Format(
                            "FIRST ACTION:\n{0}\n{1}\n{2}",
                            replay_container.actions[0].game_time,
                            replay_container.actions[0].action_type,
                            replay_container.actions[0].action_data
                            ));
                        */
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    // Console.WriteLine(String.Format("Replay file: '{0}' does not exist.", _replay_path));
                }
            }
        }
        public void InitializePacketHandlers()
        {
            // maybe use reflection, the problem is that Register is generic and so it needs to know its type at 
            // compile time, maybe just use interface and in runetime figure out the type - and again there is
            // a problem with passing generic delegate to non-generic function, if we try to only constraint the
            // argument to interface ICoreRequest we will get an error cause our generic handlers use generic type
            // even with where statement that doesn't work
            RequestHandler.Register<AttentionPingRequest>(new HandleAttentionPing(this).HandlePacket);
            RequestHandler.Register<AutoAttackOptionRequest>(new HandleAutoAttackOption(this).HandlePacket);
            RequestHandler.Register<BlueTipClickedRequest>(new HandleBlueTipClicked(this).HandlePacket);
            RequestHandler.Register<BuyItemRequest>(new HandleBuyItem(this).HandlePacket);
            RequestHandler.Register<CastSpellRequest>(new HandleCastSpell(this).HandlePacket);
            RequestHandler.Register<ChatMessageRequest>(new HandleChatBoxMessage(this).HandlePacket);
            RequestHandler.Register<ClickRequest>(new HandleClick(this).HandlePacket);
            RequestHandler.Register<CursorPositionOnWorldRequest>(new HandleCursorPositionOnWorld(this).HandlePacket);
            RequestHandler.Register<EmotionPacketRequest>(new HandleEmotion(this).HandlePacket);
            RequestHandler.Register<ExitRequest>(new HandleExit(this).HandlePacket);
            RequestHandler.Register<HeartbeatRequest>(new HandleHeartBeat(this).HandlePacket);
            RequestHandler.Register<PingLoadInfoRequest>(new HandleLoadPing(this).HandlePacket);
            RequestHandler.Register<LockCameraRequest>(new HandleLockCamera(this).HandlePacket);
            RequestHandler.Register<MapRequest>(new HandleMap(this).HandlePacket);
            RequestHandler.Register<MovementRequest>(new HandleMove(this).HandlePacket);
            RequestHandler.Register<MoveConfirmRequest>(new HandleMoveConfirm(this).HandlePacket);
            RequestHandler.Register<PauseRequest>(new HandlePauseReq(this).HandlePacket);
            RequestHandler.Register<QueryStatusRequest>(new HandleQueryStatus(this).HandlePacket);
            RequestHandler.Register<QuestClickedRequest>(new HandleQuestClicked(this).HandlePacket);
            RequestHandler.Register<ScoreboardRequest>(new HandleScoreboard(this).HandlePacket);
            RequestHandler.Register<SellItemRequest>(new HandleSellItem(this).HandlePacket);
            RequestHandler.Register<SkillUpRequest>(new HandleSkillUp(this).HandlePacket);
            RequestHandler.Register<SpawnRequest>(new HandleSpawn(this).HandlePacket);
            RequestHandler.Register<StartGameRequest>(new HandleStartGame(this).HandlePacket);
            RequestHandler.Register<StatsConfirmRequest>(new HandleStatsConfirm(this).HandlePacket);
            RequestHandler.Register<SurrenderRequest>(new HandleSurrender(this).HandlePacket);
            RequestHandler.Register<SwapItemsRequest>(new HandleSwapItems(this).HandlePacket);
            RequestHandler.Register<SynchVersionRequest>(new HandleSync(this).HandlePacket);
            RequestHandler.Register<UnpauseRequest>(new HandleUnpauseReq(this).HandlePacket);
            RequestHandler.Register<UseObjectRequest>(new HandleUseObject(this).HandlePacket);
            RequestHandler.Register<ViewRequest>(new HandleView(this).HandlePacket);
        }

        public bool LoadScripts()
        {
            var scriptLoadingResults = Config.ContentManager.LoadScripts();
            return scriptLoadingResults;
        }

        public void GameLoop()
        {
            _lastMapDurationWatch = new Stopwatch();
            _lastMapDurationWatch.Start();
            while (!SetToExit)
            {
                _packetServer.NetLoop();
                if (IsPaused)
                {
                    _lastMapDurationWatch.Stop();
                    _pauseTimer.Enabled = true;
                    if (PauseTimeLeft <= 0 && !_autoResumeCheck)
                    {
                        PacketNotifier.NotifyUnpauseGame();
                        _autoResumeCheck = true;
                    }
                    continue;
                }

                if (_lastMapDurationWatch.Elapsed.TotalMilliseconds + 1.0 > REFRESH_RATE)
                {
                    var sinceLastMapTime = _lastMapDurationWatch.Elapsed.TotalMilliseconds;
                    _lastMapDurationWatch.Restart();
                    if (IsRunning)
                    {
                        Update((float)sinceLastMapTime);
                    }
                    else
                    {
                        AIStart();
                        // ScriptEngine.CreateObject<IAIScript>("AI", "Ezreal");
                    }
                }
                Thread.Sleep(1);
            }

        }

        /*
         * =====================================================================
         * AI CODE
         * =====================================================================
         */

        private readonly static float center_x = 7000.0f;
        private readonly static float center_y = 7000.0f;
        private readonly static float bound_max = 750.0f;
        private readonly float top_bound = center_y + bound_max;
        private readonly float left_bound = center_x - bound_max;
        private readonly float right_bound = center_x + bound_max;
        private readonly float bottom_bound = center_y - bound_max;

        private readonly Random _random = new Random();

        public bool withinBounds(Vector2 pos) {
            if (pos.X >= left_bound && pos.X <= right_bound)
            {
                if (pos.Y >= bottom_bound && pos.Y <= top_bound)
                {
                    // Console.WriteLine("x legal, y legal");
                    return true;
                }
                else
                {
                    // Console.WriteLine("x legal, y illegal := {0} ({1}, {2})", pos.Y, top_bound, bottom_bound);
                    return false;
                }
            }
            else {
                // Console.WriteLine("x illegal := {0} ({1}, {2}), y unknown", pos.X, left_bound, right_bound);
                return false;
            }
        }

        public IChampion UserChamp(uint userId)
        {
            return PlayerManager.GetPeerInfo((ulong)userId).Champion;
        }

        public Vector2 UserPos(uint userId) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            return new Vector2(champion.X, champion.Y);
        }

        public void UserFarm(uint userId)
        {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            var pos = UserPos(userId);
            var closeUnits = ObjectManager.GetUnitsInRange(new Target(pos), 1000.0f, true);
            foreach (var unit in closeUnits)
            {
                var minion = unit as IMinion;
                if (minion != null)
                {
                    if (minion.Team != champion.Team)
                    {
                        if (champion.Spells[0].CurrentCooldown == 0)
                        {
                            UserSpell(userId, 0, minion.NetId, minion.GetPosition());
                        }
                        else
                        {
                            UserAttack(userId, minion);
                        }
                    }
                }
            }
        }

        public void UserAttack(uint userId, IAttackableUnit target) {
            if (withinBounds(UserPos(userId)) && !UserChamp(userId).IsDead)
            {
                var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
                var vMoves = new List<Vector2>
                {
                    new Vector2(champion.X, champion.Y),
                    new Vector2(target.X, target.Y)
                };
                champion.UpdateMoveOrder(MoveOrder.MOVE_ORDER_ATTACKMOVE);
                champion.SetWaypoints(vMoves);
                champion.SetTargetUnit(target);
                champion.AutoAttackHit(target);
            }
        }

        public void UserMove(uint userId, Vector2 target) {
            if (withinBounds(target) && !UserChamp(userId).IsDead) {
                var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
                var vMoves = new List<Vector2>
                {
                    new Vector2(champion.X, champion.Y),
                    new Vector2(target.X, target.Y)
                };
                champion.UpdateMoveOrder(MoveOrder.MOVE_ORDER_MOVE);
                champion.SetWaypoints(vMoves);
            }
        }

        public void UserBuy(uint userId, int itemId) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            champion.Shop.HandleItemBuyRequest(itemId);
        }

        public void UserTeleport(uint userId, Vector2 target) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            if (!champion.IsDead)
            {
                champion.TeleportTo(target.X, target.Y);
            }
        }

        // Default stalk range is 200 units
        public void UserStalk(uint stalkerId, uint victimId, float stalkRange = 500.0f) {
            // Basic Stalking
            Vector2 stalkerPos = UserPos(stalkerId);
            Vector2 victimPos = UserPos(victimId);
            if (MathExtension.Distance(stalkerPos, victimPos) >= stalkRange)
            {
                float xDiff = 0; //  _random.Next((int) -(stalkRange / 100.0f), (int) +(stalkRange / 100.0f)) * 100.0f;
                float yDiff = 0; // _random.Next((int) -(stalkRange / 100.0f), (int) +(stalkRange / 100.0f)) * 100.0f;
                Vector2 newPos = new Vector2(victimPos.X + xDiff, victimPos.Y + yDiff);
                UserMove(stalkerId, newPos);
            }
        }

        public void UserSpell(uint userId, byte spellSlot, uint targetNetId, Vector2 target) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            if (!champion.IsDead) //  && withinBounds(target))
            {
                var targetObj = ObjectManager.GetObjectById(targetNetId); // Param = NetID ; NOTE: I'm assuming NetId = 0 is special and means nothing in particular
                var targetUnit = targetObj as IAttackableUnit;
                var owner = PlayerManager.GetPeerInfo((ulong)userId).Champion;
                if (owner != null && owner.CanCast())
                {
                    try
                    {
                        var s = owner.GetSpell(spellSlot);
                        if (s != null)
                        {
                            s.Cast(target.X, target.Y, target.X, target.Y, targetUnit);
                        }
                    }
                    catch {
                    
                    }
                }
            }
        }

        public uint UserNetId(uint userId) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            return champion.NetId;
        }

        public void UserUpgradeSpell(uint userId, byte spellSlot) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            champion.Stats.SetSpellEnabled(spellSlot, true);
            champion.LevelUpSpell(spellSlot);
        }

        public bool UserDead(uint userId) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            return champion.IsDead;
        }

        public void UserMoveAway(uint userId, Vector2 avoid, int[] angles) {
            var champPos = new Vector2(UserPos(userId).X, UserPos(userId).Y);
            // var myValues = new int[] { 0 + 90, 360 - 90 }; // Will work with array or list
            var dodgeAngle = angles[_random.Next(angles.Length)];
            champPos.X += (champPos - avoid).Normalized().Rotated(dodgeAngle).X * 4.0f;
            champPos.Y += (champPos - avoid).Normalized().Rotated(dodgeAngle).Y * 4.0f;
            // Console.WriteLine("[NEW DISTANCE] = {0} {1} {2} <{3},{4}>", MathExtension.Distance(avoid, champPos), avoid, champPos, (champPos - avoid).Normalized().Rotated(90).X, (champPos - avoid).Normalized().Rotated(90).Y);
            UserMove(userId, champPos);
        }

        // public bool UserDodge(uint userId) {
        public void UserDodge(uint userId, Action a) {
            // Console.WriteLine("check dodge = {0}", userId);
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            var champPos = new Vector2(UserPos(userId).X, UserPos(userId).Y);
            var temp = ObjectManager.GetObjects();
            foreach (var obj in temp.Values)
            {
                var projectile = obj as IProjectile;
                // Console.WriteLine(projectile);
                if (projectile != null)
                {
                    // Console.WriteLine("FOUND PROJECTILE!!!!!");
                    Vector2 projectilePos = new Vector2(projectile.X, projectile.Y);
                    if (projectile.Team != champion.Team)
                    {
                        if (MathExtension.Distance(projectilePos, champPos) <= 1100.0f)
                        {
                            var newPos = new Vector2(projectilePos.X, projectilePos.Y);
                            UserMoveAway(userId, projectilePos, new int[] { 0 + 45, 360 - 45 });
                        }
                        else {
                            //return false;
                            a();
                        }
                    }
                    else
                    {
                        a();
                    }
                }
                else
                {
                    a();
                }
            }
        }

        public void UserMoveSpeedAdd(uint userId, float n)
        {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            champion.Stats.MoveSpeed.BaseValue += 300;
        }

        public float UserMoveSpeed(uint userId) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            return champion.Stats.MoveSpeed.Total;
        }

        public void UserAddHP(uint userId, float hp) {
            PlayerManager.GetPeerInfo((ulong)userId).Champion.Stats.HealthPoints.FlatBonus += hp;
            PlayerManager.GetPeerInfo((ulong)userId).Champion.Stats.CurrentHealth += hp;
        }

        public float UserGetHP(uint userId) {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            return champion.Stats.CurrentHealth;
        }

        public IChampion UserGetClosestEnemy(uint userId)
        {
            var champion = PlayerManager.GetPeerInfo((ulong)userId).Champion;
            var champs = ObjectManager.GetChampionsInRange(champion.X, champion.Y, 1100.0f, true);
            List<IChampion> enemies = champs.Where(a => a.Team != champion.Team).ToList();
            IChampion closest = null;
            float closestRange = 28000;
            enemies.ForEach(enemy =>
            {
                if (enemy.GetDistanceTo(new Target(UserPos(userId))) < closestRange)
                {
                    closest = enemy;
                    closestRange = enemy.GetDistanceTo(new Target(UserPos(userId)));
                }
            });
            return closest;
        }

        /*
         * ---------------------------------------------------------------------
         * START OF OBSERVATION CODE
         * ---------------------------------------------------------------------
         */

        struct Champ_Actions_Available
        {
            // Whether can noop;
            public bool can_no_op;

            // Whether can move
            public bool can_move;

            // Whether can auto attack
            public bool can_auto;

            // Whether can cast champion abilities
            public bool can_spell_0;
            public bool can_spell_1;
            public bool can_spell_2;
            public bool can_spell_3;

            // Whether can cast summoner spells
            public bool can_spell_4;
            public bool can_spell_5;
        }

        struct Champ_Observation
        {
            // UserID
            public uint user_id;

            // Transform
            public Vector2 position;
            public float facing_angle;
            
            // HP
            public float max_hp;
            public float current_hp;
            public float hp_regen;

            // MP
            public float max_mp;
            public float current_mp;
            public float mp_regen;

            // Stats
            public float attack_damage;
            public float attack_speed;
            public float alive;
            public int level;
            public float armor;
            public float mr;
            public float current_gold;
            public int death_count;
            public float move_speed;

            // Team
            public float my_team;
            public float neutal;

            // Distance
            public float dx_to_me;
            public float dy_to_me;
            public float distance_to_me;

            // Abilities
            public float q_cooldown;
            public int q_level;
            public float w_cooldown;
            public int w_level;
            public float e_cooldown;
            public int e_level;
            public float r_cooldown;
            public int r_level;
            public float sum_1_cooldown;
            public float sum_2_cooldown;
        }

        struct ResponseObservation
        {
            public Observation observation;
        }

        struct Observation
        {
            // Current game time
            public float game_time;

            // Us and others
            public List<Champ_Observation> champ_units;

            // Available Actions
            public Champ_Actions_Available available_actions;
        }
        
        public String AIObserve(uint userId)
        {
            // Init units list as it's a struct, not an object
            ResponseObservation response = new ResponseObservation();
            Observation observation = new Observation();
            Champ_Actions_Available available_actions = new Champ_Actions_Available();

            // All actions available by default
            available_actions.can_no_op     = true;
            available_actions.can_move      = true;
            available_actions.can_auto      = true;
            available_actions.can_spell_0   = true;
            available_actions.can_spell_1   = true;
            available_actions.can_spell_2   = true;
            available_actions.can_spell_3   = true;
            available_actions.can_spell_4   = true;
            available_actions.can_spell_5   = true;

            // Global data
            observation.game_time = GameTime;

            // Set champ unit list
            observation.champ_units = new List<Champ_Observation>();

            // Current Champion
            var champion = PlayerManager.GetPeerInfo((ulong) userId).Champion;

            // Get All Champions
            var champs = ObjectManager.GetChampionsInRange(champion.X, champion.Y, 28000.0f, false);
            
            // NOTE: Removes agent from observation, observe only other units
            // champs.Remove(champion);

            foreach (IChampion champ in champs)
            {
                // Init unit observation
                Champ_Observation champ_observation = new Champ_Observation();

                // Stat: UserID
                for (uint i = 1; i < champs.Count+1; i++)
                {
                    // User id if observation is off the requesting observed player
                    if (UserChamp(i) == champ)
                    {
                        // Set user id to observer
                        champ_observation.user_id = i;

                        // If we're dead, that disallows a lot of actions
                        if (champ.IsDead)
                        {
                            available_actions.can_move      = false;
                            available_actions.can_auto      = false;
                            available_actions.can_spell_0   = false;
                            available_actions.can_spell_1   = false;
                            available_actions.can_spell_2   = false;
                            available_actions.can_spell_3   = false;
                            available_actions.can_spell_4   = false;
                            available_actions.can_spell_5   = false;
                        } else {
                            if (champ.GetSpell(0).CurrentCooldown > 0)
                            { available_actions.can_spell_0 = false; }

                            if (champ.GetSpell(1).CurrentCooldown > 0)
                            { available_actions.can_spell_1 = false; }

                            if (champ.GetSpell(2).CurrentCooldown > 0)
                            { available_actions.can_spell_2 = false; }

                            if (champ.GetSpell(3).CurrentCooldown > 0)
                            { available_actions.can_spell_3 = false; }

                            if (champ.GetSpell(4).CurrentCooldown > 0)
                            { available_actions.can_spell_4 = false; }

                            if (champ.GetSpell(5).CurrentCooldown > 0)
                            { available_actions.can_spell_5 = false; }
                        }
                    }
                }

                // Stat: Transform
                champ_observation.position = champ.GetPosition();
                float angle = MathExtension.GetAngleBetween(new Vector2(0, 1), champ.GetPosition().Normalized());
                champ_observation.facing_angle = angle;

                // Stat: HP
                champ_observation.max_hp = champ.Stats.HealthPoints.Total;
                champ_observation.current_hp = champ.Stats.CurrentHealth;
                champ_observation.hp_regen = champ.Stats.HealthRegeneration.Total;

                // Stat: Mana
                champ_observation.max_mp = champ.Stats.ManaPoints.Total;
                champ_observation.current_mp = champ.Stats.CurrentMana;
                champ_observation.mp_regen = champ.Stats.ManaRegeneration.Total;

                // Stat: AD + AS
                champ_observation.attack_damage = champ.Stats.AttackDamage.Total;
                champ_observation.attack_speed = champ.Stats.GetTotalAttackSpeed();
                champ_observation.alive = Convert.ToSingle(!champ.IsDead);
                champ_observation.level = champ.Stats.Level;
                champ_observation.armor = champ.Stats.Armor.Total;
                champ_observation.mr = champ.Stats.Armor.Total;
                champ_observation.current_gold = champ.Stats.Gold;
                champ_observation.move_speed = champ.Stats.MoveSpeed.Total;

                // Team
                champ_observation.my_team = Convert.ToSingle(champ.Team == champion.Team);
                champ_observation.neutal = Convert.ToSingle(false);

                // Distance
                champ_observation.dx_to_me = champ.X - champion.X;
                champ_observation.dy_to_me = champ.Y - champion.Y;
                champ_observation.distance_to_me = MathExtension.Distance(champ.GetPosition(), champion.GetPosition());

                // Abilities (First 4 are Q,W,E,R and last 2 are summoners)
                //try
                //{
                    champ_observation.q_cooldown = champ.GetSpell(0).CurrentCooldown;
                    champ_observation.q_level = champ.GetSpell(0).Level;
                    champ_observation.w_cooldown = champ.GetSpell(1).CurrentCooldown;
                    champ_observation.w_level = champ.GetSpell(1).Level;
                    champ_observation.e_cooldown = champ.GetSpell(2).CurrentCooldown;
                    champ_observation.e_level = champ.GetSpell(2).Level;
                    champ_observation.r_cooldown = champ.GetSpell(3).CurrentCooldown;
                    champ_observation.r_level = champ.GetSpell(3).Level;
                    champ_observation.sum_1_cooldown = champ.GetSpell(4).CurrentCooldown;
                    champ_observation.sum_2_cooldown = champ.GetSpell(5).CurrentCooldown;
                //}

                // General
                champ_observation.death_count = champ.ChampStats.Deaths;

                // Add unit to observation
                observation.champ_units.Add(champ_observation);
            }

            // Return JSON observation string
            observation.available_actions = available_actions;
            response.observation = observation;
            JObject o = (JObject) JToken.FromObject(response);
            return o.ToString();
        }

        /*
         * ---------------------------------------------------------------------
         * END OF OBSERVATION CODE
         * ---------------------------------------------------------------------
         */

        public void AIStart() {
            /*
            // Setup AI here
            for (uint i=1; i<4+1; i++)
            {
                UserBuy(i, 1055); // NOTE: Doesn't work properly
                for (byte j=0; j<5; j++)
                {
                    UserUpgradeSpell(i, 0); // NOTE: Doesn't work properly
                }
            }
            */
        }

        public int counter = -1;
        public void AIBot(uint userId, uint targetUserId, int curCounter)
        {
            IChampion closestEnemy = UserGetClosestEnemy(userId);
            if (closestEnemy != null)
            {
                uint playerId = (uint)PlayerManager.GetClientInfoByChampion(closestEnemy).PlayerId;
                targetUserId = playerId;
            }
            if (!UserDead(userId) && !UserDead(targetUserId))
            {
                // Attack player if in range
                // if (MathExtension.Distance(UserPos(userId), UserPos(targetUserId)) <= 1000.0f) // NOTE: This is just for Q
                if (MathExtension.Distance(UserPos(userId), UserPos(targetUserId)) < 1000.0f)
                {
                    Vector2 target = UserPos(userId);
                    float x = (float)_random.Next(-4, +4) * 100;
                    float y = (float)_random.Next(-4, +4) * 100;
                    target.X += x; target.Y += y;
                    UserSpell(userId, 0, 0, UserPos(targetUserId));
                    // UserAttack(userId, UserChamp(targetUserId));
                    Action a = () => { };
                    if (MathExtension.Distance(target, UserPos(targetUserId)) > 525.0f)
                    {
                        a = () => UserMove(userId, new Vector2(target.X, target.Y)); // UserMoveAway(targetUserId, UserPos(targetUserId), new int[] { 180 });
                    }
                    else
                    {
                        UserMoveAway(userId, UserPos(targetUserId), new int[] { 180 });
                    }
                    UserDodge(userId, a);
                    //UserFarm(userId);
                }
                // Console.WriteLine("Player 2 = X: {0}, Y: {1}", x.ToString(), y.ToString());
                else
                {
                    // Stalk player
                    UserStalk(userId, targetUserId, 1000.0f);
                }
            }
        }

        struct Move_Action
        {
            public uint player_id;
            public float x;
            public float y;
        }

        struct Teleport_Action
        {
            public uint player_id;
            public float x;
            public float y;
        }

        struct Attack_Action
        {
            public uint player_id;
            public uint target_player_id;
        }

        struct Spell_Action
        {
            public uint player_id;
            public uint target_player_id;
            public byte spell_slot;
            public float x;
            public float y;
        }

        struct Change_Champion_Command
        {
            public uint player_id;
            public string champion_name;
        }

        struct Save_Replay_Info
        {
            public string map;
            public string players;
            public float multiplier;
        }

        // =====================================================================================
        // Replay Structures
        // =====================================================================================

        struct ReplayContainer // : ISerializable 
        {
            public Save_Replay_Info info;
            public List<ReplayAction> actions;
        }

        struct ReplayAction // : ISerializable 
        {
            public float game_time;
            public string action_type;
            public string action_data;
        }

        public void AIAct(String action_type, String action_data)
        {
            switch (action_type)
            {
                case "noop":
                    break;
                case "move":
                    Move_Action m = JsonConvert.DeserializeObject<Move_Action>(action_data);
                    UserMove(m.player_id, UserPos(m.player_id) + new Vector2(m.x, m.y));
                    break;
                case "move_to":
                    Move_Action mt = JsonConvert.DeserializeObject<Move_Action>(action_data);
                    UserMove(mt.player_id, new Vector2(mt.x, mt.y));
                    break;
                case "teleport":
                    Teleport_Action t = JsonConvert.DeserializeObject<Teleport_Action>(action_data);
                    UserTeleport(t.player_id, new Vector2(t.x, t.y));
                    break;
                case "spell":
                    Spell_Action s = JsonConvert.DeserializeObject<Spell_Action>(action_data);
                    UserSpell(s.player_id, s.spell_slot, 0, new Vector2(s.x, s.y));
                    break;
                case "attack":
                    Attack_Action a = JsonConvert.DeserializeObject<Attack_Action>(action_data);
                    UserAttack(a.player_id, UserChamp(a.target_player_id));
                    break;
                case "reset":
                    var champs = ObjectManager.GetChampionsInRange(0, 0, 28000.0f, false);
                    foreach (IChampion champ in champs)
                    {
                        champ.Respawn();
                    }
                    break;
            }
        }

        //bool being_observed = false;
        bool being_observed = true;

        float replay_last_game_time;
        private int last_action_index = 0;

        public void AIUpdate(float diff)
        {
            int curCounter = (int) Math.Floor(GameTime / _limitRate);
            if (curCounter > counter)
            {
                
                String current_command = db.ListRightPop("command");
                if (current_command != null)
                {
                    if (current_command == "start_observing")
                    {
                        being_observed = true;
                    }
                    else if (current_command == "save_replay")
                    {
                        String command_data = db.ListRightPop("command");
                        Console.WriteLine(String.Format("save_replay data: {0}", command_data==null, command_data));
                        Save_Replay_Info info = JsonConvert.DeserializeObject<Save_Replay_Info>(command_data);
                        
                        // Generate replay json data
                        replay_container.info = info;
                        JObject o = (JObject) JToken.FromObject(replay_container);
                        String data = (String) o.ToString();
                        Console.WriteLine(String.Format("REPLAY DATA FROM GAMESERVER: {0} {1}", replay_container.actions.Count, o));
                        
                        db.ListLeftPush("command_data", data);
                    }
                }

                // Observations for AI agent when agent connects
                if (being_observed)
                {   
                    //Console.WriteLine("BEING OBSERVED");
                    // Run replay if there's a valid file provided
                    if (!String.IsNullOrEmpty(_replay_path))
                    {
                        Console.WriteLine(String.Format("SHITE: {0} {1}", last_action_index, replay_container.actions.Count));
                        if (last_action_index < replay_container.actions.Count)
                        {
                            Console.WriteLine("ACTIONS LEFT TO REPLAY");
                            // Get actions for current batch
                            Console.WriteLine(String.Format("LENGTH OF ACTION BUFFER: {0}", replay_container.actions.Count));
                            Console.WriteLine(String.Format("FIRST GAME TIME: {0}", replay_container.actions[last_action_index].game_time));
                            replay_last_game_time = replay_container.actions[last_action_index].game_time;
                            List<ReplayAction> current_actions = new List<ReplayAction>();
                            ReplayAction current_action = replay_container.actions[last_action_index];
                            while ( current_action.game_time == replay_last_game_time &&
                                    last_action_index < replay_container.actions.Count)
                            {
                                // Add current action to batch
                                Console.WriteLine(String.Format("LAST ACTION INDEX: {0} {1} {2}", last_action_index, current_action.game_time, replay_last_game_time));
                                current_actions.Add(current_action);

                                // Go to next action
                                last_action_index += 1;
                                if (last_action_index < replay_container.actions.Count)
                                {
                                    current_action = replay_container.actions[last_action_index];
                                }
                            }

                            Console.WriteLine(String.Format("ACTION INDEX: {0}, ACTION COUNT: {1}", last_action_index, current_actions.Count));

                            // Execute actions for current batch
                            foreach (ReplayAction action in current_actions)
                            {
                                AIAct(action.action_type, action.action_data);
                            }
                            if (last_action_index >= replay_container.actions.Count)
                            {
                                SetToExit = true;
                            }
                        }
                        else
                        {
                            SetToExit = true;
                        }
                    }

                    // Otherwise play game
                    else
                    {
                        // Only start observing when a client asks to take over
                        // Console.WriteLine(String.Format("OBSERVING: {0} NUMBER OF AGENTS", _human_count + _agent_count));
                        for (uint i=0; i<_human_count + _agent_count; i++)
                        {
                            db.ListLeftPush("observation", AIObserve(i+1));
                        }

                        // Only accept actions when we're being observed
                        long action_length = db.ListLength("action");
                        while (action_length > 0 && action_length % 2 == 0)
                        {
                            // Get action type and data
                            String action_type = db.ListRightPop("action");
                            String action_data = db.ListRightPop("action");

                            // Replay current action
                            ReplayAction cur_action;
                            cur_action.game_time = GameTime;
                            cur_action.action_type = action_type;
                            cur_action.action_data = action_data;

                            replay_container.actions.Add(cur_action);
                            // Console.WriteLine(String.Format("CURRENT ACTION: {0} {1} {2}", cur_action.game_time, cur_action.action_type, cur_action.action_data));
                            // Perform AI agent action
                            AIAct(action_type, action_data);
                            action_length = db.ListLength("action");
                        }
                    }
                }

                // Hardcoded behaviour
                // AIBot(1, 2, curCounter);
                // AIBot(2, 1, curCounter);
            }
            counter = curCounter;
        }

        // 2 clients (win):  cd "C:\LeagueSandbox\League_Sandbox_Client\RADS\solutions\lol_game_client_sln\releases\0.0.1.68\deploy\" && "League of Legends.exe" "8394" "LoLLauncher.exe" "" "127.0.0.1 5119 17BLOhi6KZsTtldTsizvHg== 2" & "League of Legends.exe" "8394" "LoLLauncher.exe" "" "127.0.0.1 5119 17BLOhi6KZsTtldTsizvHg== 1"
        // 1 client (win):   cd "C:\LeagueSandbox\League_Sandbox_Client\RADS\solutions\lol_game_client_sln\releases\0.0.1.68\deploy\" && "League of Legends.exe" "8394" "LoLLauncher.exe" "" "127.0.0.1 5119 17BLOhi6KZsTtldTsizvHg== 1"
        // 1 client (linux): cd /home/joe/League-of-Legends-4-20/RADS/solutions/lol_game_client_sln/releases/0.0.1.68/deploy && wine ./League\ of\ Legends.exe "8394" "../../../../../../LoLLauncher.exe" "" "192.168.0.100 5119 17BLOhi6KZsTtldTsizvHg== 1"

        /*
         * =====================================================================
         * END OF AI CODE
         * =====================================================================
         */

        public void Update(float diff)
        {
            GameTime += diff;
            AIUpdate(diff); // NOTE: Add AI handler
            ObjectManager.Update(diff);
            ProtectionManager.Update(diff);
            Map.Update(diff);
            _gameScriptTimers.ForEach(gsTimer => gsTimer.Update(diff));
            _gameScriptTimers.RemoveAll(gsTimer => gsTimer.IsDead());

            // By default, synchronize the game time every 10 seconds
            _nextSyncTime += diff;
            if (_nextSyncTime >= 10 * 1000)
            {
                PacketNotifier.NotifyGameTimer(GameTime);
                _nextSyncTime = 0;
            }
        }

        public void AddGameScriptTimer(GameScriptTimer timer)
        {
            _gameScriptTimers.Add(timer);
        }

        public void RemoveGameScriptTimer(GameScriptTimer timer)
        {
            _gameScriptTimers.Remove(timer);
        }

        public void IncrementReadyPlayers()
        {
            PlayersReady++;
        }

        public void Start()
        {
            IsRunning = true;

            db.KeyDelete("observation");
            db.ListLeftPush("observation", "\"game_started\"");

            // Start recording from here as this is the earliest that agents can start issuing commands
            if (String.IsNullOrEmpty(_replay_path))
            {
                replay_container = new ReplayContainer();
                replay_container.actions = new List<ReplayAction>();
            }
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Pause()
        {
            if (PauseTimeLeft <= 0)
            {
                return;
            }
            IsPaused = true;
            PacketNotifier.NotifyPauseGame((int)PauseTimeLeft, true);
        }

        public void Unpause()
        {
            _lastMapDurationWatch.Start();
            IsPaused = false;
            _pauseTimer.Enabled = false;
        }

        private static List<T> GetInstances<T>(IGame g)
        {
            return Assembly.GetCallingAssembly()
                .GetTypes()
                .Where(t => t.BaseType == typeof(T))
                .Select(t => (T)Activator.CreateInstance(t, g)).ToList();
        }

        public void SetGameToExit()
        {
            _logger.Info("Game is over. Game Server will exit in 10 seconds.");
            var timer = new Timer(10000) { AutoReset = false };
            timer.Elapsed += (a, b) => SetToExit = true;
            timer.Start();
        }
    }
}