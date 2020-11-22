using LeagueSandbox.GameServer.Logging;
using log4net;
using LeagueSandbox.GameServer.Items;
using System;

namespace LeagueSandbox.GameServer
{
    /// <summary>
    /// This class is used by the GameServerApp to launch the server.
    /// Ideally the Program class in this project would be removed entirely, but that's not possible yet.
    /// </summary>
    public class GameServerLauncher
    {
        private readonly ILog _logger;
        private readonly Server _server;

        public Game game;
        public string ExecutingDirectory { get; private set; }
        public string ConfigJson { get; private set; }
        public ushort ServerPort { get; private set; }
        public string ServerHost { get; private set; }

        public GameServerLauncher(string serverHost, ushort serverPort, string configJson,
            string blowfishKey, int human_count, int agent_count, float multiplier,
            string replay_path)
        {
            ConfigJson = configJson;
            ServerPort = serverPort;
            ServerHost = serverHost;
            _logger = LoggerProvider.GetLogger();
            game = new Game(serverHost, human_count, agent_count, multiplier,
                            replay_path);

            _server = new Server(game, serverHost, serverPort, configJson);

#if !DEBUG
            try
            {
#endif
                ExecutingDirectory = ServerContext.ExecutingDirectory;
                _server.Start();
#if !DEBUG
            }
            catch (Exception e)
            {
                _logger.Error(e);
            }
#endif
        }

        public void StartNetworkLoop()
        {
#if !DEBUG
            try
            {
#endif
                _server.StartNetworkLoop();
#if !DEBUG
            }
            catch (Exception e)
            {
                _logger.Error(e);
            }
#endif
        }
    }
}
