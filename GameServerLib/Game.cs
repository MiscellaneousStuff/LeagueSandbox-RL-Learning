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
using System.Reflection;
using System.Threading;
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

        public Game()
        {
            _logger = LoggerProvider.GetLogger();
            ItemManager = new ItemManager();
            ChatCommandManager = new ChatCommandManager(this);
            NetworkIdManager = new NetworkIdManager();
            PlayerManager = new PlayerManager(this);
            ScriptEngine = new CSharpScriptEngine();
            RequestHandler = new NetworkHandler<ICoreRequest>();
            ResponseHandler = new NetworkHandler<ICoreResponse>();
        }

        public void Initialize(Config config, PacketServer server)
        {
            _logger.Info("Loading Config.");
            Config = config;

            _gameScriptTimers = new List<GameScriptTimer>();

            ChatCommandManager.LoadCommands();

            ObjectManager = new ObjectManager(this);
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

            // Fake add second client
            /*
             * KEYS = ["player1", "player2", "playern", etc]
             */
            // ((PlayerManager)PlayerManager)._players[1].Item2.IsStartedClient = true;
            // new HandleStartGame(this).HandlePacket(2, new StartGameRequest());

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

            _logger.Info("Game is ready.");
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

        private readonly static float center = 7000.0f;
        private readonly static float bound_max = 750.0f;
        private readonly float top_bound = center + bound_max;
        private readonly float left_bound = center - bound_max;
        private readonly float right_bound = center + bound_max;
        private readonly float bottom_bound = center - bound_max;

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
            var champion = PlayerManager.GetPeerInfo((ulong) userId).Champion;
            var vMoves = new List<Vector2>
                {
                    new Vector2(champion.X, champion.Y),
                    new Vector2(target.X, target.Y)
                };
            champion.UpdateMoveOrder(MoveOrder.MOVE_ORDER_ATTACKMOVE);
            champion.SetWaypoints(vMoves);
            // champion.SetTargetUnit(target);
            // champion.AutoAttackHit(target);
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
            if (!champion.IsDead)
            {
                var targetObj = ObjectManager.GetObjectById(targetNetId); // Param = NetID ; NOTE: I'm assuming NetId = 0 is special and means nothing in particular
                var targetUnit = targetObj as IAttackableUnit;
                var owner = PlayerManager.GetPeerInfo((ulong)userId).Champion;
                if (owner != null && owner.CanCast())
                {
                    var s = owner.GetSpell(spellSlot);
                    if (s != null)
                    {
                        s.Cast(target.X, target.Y, target.X, target.Y, targetUnit);
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

        struct Champ_Observation
        {
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

        struct Observation
        {
            public float game_time;
            public List<Champ_Observation> champ_units;
        }
        
        public String AIObserve(uint userId, uint targetUserId)
        {
            // Init units list as it's a struct, not an object
            Observation observation = new Observation();
            observation.champ_units = new List<Champ_Observation>();

            // Global data
            observation.game_time = GameTime;
            
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

                // General
                champ_observation.death_count = champ.ChampStats.Deaths;

                // Add unit to observation
                observation.champ_units.Add(champ_observation);
            }

            // Return JSON observation string
            JObject o = (JObject) JToken.FromObject(observation);
            return o.ToString();
        }

        /*
         * ---------------------------------------------------------------------
         * END OF OBSERVATION CODE
         * ---------------------------------------------------------------------
         */

        public void AIStart() {
            // Init Redis Here
            // redis = ConnectionMultiplexer.Connect("localhost");
            redis = ConnectionMultiplexer.Connect("192.168.0.16");
            db = redis.GetDatabase();

            // Setup AI here
            for (uint i=1; i<2+1; i++)
            {
                UserBuy(i, 1055); // NOTE: Doesn't work properly
                for (byte j=0; j<1; j++)
                {
                    UserUpgradeSpell(i, 0); // NOTE: Doesn't work properly
                }

                /*
                if (UserChamp(i).Team == TeamId.TEAM_BLUE)
                {
                    UserTeleport(i, new Vector2(7000.0f - bound_max + 100.0f, 7000.0f - bound_max + 100.0f));
                }
                else
                {
                    UserTeleport(i, new Vector2(7000.0f + bound_max - 100.0f, 7000.0f + bound_max - 100.0f));
                }
                */
            }

            // UserBuy(2, 1055); // NOTE: Doesn't work properly
        }

        public static float speedMultiplier = 4.0f;
        public float limitRate = 1000.0f / speedMultiplier;
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

        struct Spell_Action
        {
            public uint player_id;
            public uint target_player_id;
            public byte spell_slot;
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
                    UserSpell(s.player_id, s.spell_slot, 0, UserPos(s.target_player_id));
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

        bool being_observed = false;

        public void AIUpdate(float diff)
        {
            int curCounter = (int) Math.Floor(GameTime / limitRate);
            if (curCounter > counter)
            {
                // Check for being observed
                String current_command = db.ListLeftPop("command");
                if (current_command != null)
                {
                    if (current_command == "start_observing")
                    {
                        being_observed = true;
                    }
                }

                // Observations for AI agent when agent connects
                if (being_observed)
                {
                    // Only start observing when a client asks to take over
                    db.ListLeftPush("observation", AIObserve(1, 2));

                    // Only accept actions when we're being observed
                    long action_length = db.ListLength("action");
                    while (action_length > 0 && action_length % 2 == 0)
                    {
                        String action_type = db.ListRightPop("action");
                        String action_data = db.ListRightPop("action");
                        AIAct(action_type, action_data);
                        action_length = db.ListLength("action");
                    }
                }

                // Hardcoded behaviour
                // AIBot(1, 2, curCounter);
                AIBot(2, 1, curCounter);
            }
            counter = curCounter;
        }

        // cd "C:\LeagueSandbox\League_Sandbox_Client\RADS\solutions\lol_game_client_sln\releases\0.0.1.68\deploy\" && "League of Legends.exe" "8394" "LoLLauncher.exe" "" "127.0.0.1 5119 17BLOhi6KZsTtldTsizvHg== 2" & "League of Legends.exe" "8394" "LoLLauncher.exe" "" "127.0.0.1 5119 17BLOhi6KZsTtldTsizvHg== 1"

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

        public bool HandleDisconnect(int userId)
        {
            var peerinfo = PlayerManager.GetPeerInfo((ulong)userId);
            if (peerinfo != null)
            {
                if (!peerinfo.IsDisconnected)
                {
                    PacketNotifier.NotifyUnitAnnounceEvent(UnitAnnounces.SUMMONER_DISCONNECTED, peerinfo.Champion);
                }
                peerinfo.IsDisconnected = true;
            }
            return true;
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