﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lidgren.Network;
using Newtonsoft.Json;
using Sanicball.Data;
using Sanicball.Match;

namespace SanicballServerLib
{
    public class LogArgs : EventArgs
    {
        public LogEntry Entry { get; }

        public LogArgs(LogEntry entry)
        {
            Entry = entry;
        }
    }

    public enum LogType
    {
        Normal,
        Debug,
        Warning,
        Error
    }

    public struct LogEntry
    {
        public DateTime Timestamp { get; }
        public string Message { get; }
        public LogType Type { get; }

        public LogEntry(DateTime timestamp, string message, LogType type)
        {
            Timestamp = timestamp;
            Message = message;
            Type = type;
        }
    }

    public class Server : IDisposable
    {
        private const string SETTINGS_FILENAME = "MatchSettings.json";
        private const int TICKRATE = 20;

        public event EventHandler<LogArgs> OnLog;

        private List<LogEntry> log = new List<LogEntry>();
        private NetServer netServer;
        private bool running;
        private CommandQueue commandQueue;

        //Match state
        private List<MatchClientState> matchClients = new List<MatchClientState>();
        private List<MatchPlayerState> matchPlayers = new List<MatchPlayerState>();
        private MatchSettings matchSettings;

        public bool Running { get { return running; } }

        public Server(CommandQueue commandQueue)
        {
            this.commandQueue = commandQueue;
        }

        public void Start(int port)
        {
            bool defaultSettings = true;
            if (File.Exists(SETTINGS_FILENAME))
            {
                Log("Loading match settings");
                using (StreamReader sr = new StreamReader(SETTINGS_FILENAME))
                {
                    try
                    {
                        matchSettings = JsonConvert.DeserializeObject<MatchSettings>(sr.ReadToEnd());
                        defaultSettings = false;
                    }
                    catch (JsonException ex)
                    {
                        Log("Failed to load " + SETTINGS_FILENAME + ": " + ex.Message);
                    }
                }
            }
            if (defaultSettings)
                matchSettings = MatchSettings.CreateDefault();

            running = true;
            NetPeerConfiguration config = new NetPeerConfiguration(OnlineMatchMessenger.APP_ID);
            config.Port = 25000;
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

            netServer = new NetServer(config);
            netServer.Start();

            Log("Server started on port " + port + "!");

            //Thread messageThread = new Thread(MessageLoop);
            MessageLoop();
        }

        private void MessageLoop()
        {
            while (running)
            {
                Thread.Sleep(1000 / TICKRATE);

                //Check command queue
                Command cmd;
                while ((cmd = commandQueue.ReadNext()) != null)
                {
                    Log("Entered command: " + cmd.Name + ", " + cmd.ArgCount + " arguments", LogType.Debug);

                    if (cmd.Name == "stop")
                    {
                        running = false;
                    }
                }

                //Check network message queue
                NetIncomingMessage msg;
                while ((msg = netServer.ReadMessage()) != null)
                {
                    switch (msg.MessageType)
                    {
                        case NetIncomingMessageType.VerboseDebugMessage:
                        case NetIncomingMessageType.DebugMessage:
                            Log(msg.ReadString(), LogType.Debug);
                            break;

                        case NetIncomingMessageType.WarningMessage:
                            Log(msg.ReadString(), LogType.Warning);
                            break;

                        case NetIncomingMessageType.ErrorMessage:
                            Log(msg.ReadString(), LogType.Error);
                            break;

                        case NetIncomingMessageType.StatusChanged:
                            byte status = msg.ReadByte();
                            string statusMsg = msg.ReadString();
                            Log("Status change recieved: " + (NetConnectionStatus)status + " - Message: " + statusMsg, LogType.Debug);
                            break;

                        case NetIncomingMessageType.ConnectionApproval:
                            string text = msg.ReadString();
                            if (text.Contains("please"))
                            {
                                //Approve for being nice
                                NetOutgoingMessage hailMsg = netServer.CreateMessage();

                                MatchState info = new MatchState(new List<MatchClientState>(matchClients), new List<MatchPlayerState>(matchPlayers), matchSettings);
                                string infoStr = JsonConvert.SerializeObject(info);

                                hailMsg.Write(infoStr);
                                msg.SenderConnection.Approve(hailMsg);
                            }
                            else
                            {
                                msg.SenderConnection.Deny();
                            }
                            break;

                        case NetIncomingMessageType.Data:
                            byte messageType = msg.ReadByte();
                            switch (messageType)
                            {
                                case MessageType.MatchMessage:
                                    MatchMessage matchMessage = JsonConvert.DeserializeObject<MatchMessage>(msg.ReadString(), new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.All });

                                    if (matchMessage is ClientJoinedMessage)
                                    {
                                        var castedMsg = (ClientJoinedMessage)matchMessage;
                                        matchClients.Add(new MatchClientState(castedMsg.ClientGuid, castedMsg.ClientName));
                                        Log("Client " + castedMsg.ClientGuid + " joined", LogType.Debug);
                                    }

                                    if (matchMessage is PlayerJoinedMessage)
                                    {
                                        var castedMsg = (PlayerJoinedMessage)matchMessage;
                                        matchPlayers.Add(new MatchPlayerState(castedMsg.ClientGuid, castedMsg.CtrlType, false, castedMsg.InitialCharacter));
                                        Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " joined", LogType.Debug);
                                    }

                                    if (matchMessage is PlayerLeftMessage)
                                    {
                                        var castedMsg = (PlayerLeftMessage)matchMessage;
                                        matchPlayers.RemoveAll(a => a.ClientGuid == castedMsg.ClientGuid && a.CtrlType == castedMsg.CtrlType);
                                        Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " left", LogType.Debug);
                                    }

                                    if (matchMessage is CharacterChangedMessage)
                                    {
                                        var castedMsg = (CharacterChangedMessage)matchMessage;
                                        MatchPlayerState player = matchPlayers.FirstOrDefault(a => a.ClientGuid == castedMsg.ClientGuid && a.CtrlType == castedMsg.CtrlType);
                                        if (player != null)
                                        {
                                            player = new MatchPlayerState(player.ClientGuid, player.CtrlType, player.ReadyToRace, castedMsg.NewCharacter);
                                        }
                                        Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " set character to " + castedMsg.NewCharacter, LogType.Debug);
                                    }

                                    if (matchMessage is ChangedReadyMessage)
                                    {
                                        var castedMsg = (ChangedReadyMessage)matchMessage;
                                        MatchPlayerState player = matchPlayers.FirstOrDefault(a => a.ClientGuid == castedMsg.ClientGuid && a.CtrlType == castedMsg.CtrlType);
                                        if (player != null)
                                        {
                                            player = new MatchPlayerState(player.ClientGuid, player.CtrlType, castedMsg.Ready, player.CharacterId);
                                        }
                                        Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " set ready to " + castedMsg.Ready, LogType.Debug);
                                    }

                                    if (matchMessage is SettingsChangedMessage)
                                    {
                                        var castedMsg = (SettingsChangedMessage)matchMessage;
                                        matchSettings = castedMsg.NewMatchSettings;
                                        Log("New settings recieved", LogType.Debug);
                                    }

                                    //Forward this message to ALL clients
                                    //This is just for testing, some messages might not need to be forwarded
                                    Log("Forwarding message of type " + matchMessage.GetType(), LogType.Debug);
                                    SendToAll(matchMessage);
                                    break;

                                default:
                                    Log("Recieved data message of unknown type");
                                    break;
                            }
                            break;

                        default:
                            Log("Recieved unhandled message of type " + msg.MessageType, LogType.Debug);
                            break;
                    }
                }
            }
        }

        private void SendToAll(MatchMessage matchMsg)
        {
            string matchMsgSerialized = JsonConvert.SerializeObject(matchMsg, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All });

            NetOutgoingMessage netMsg = netServer.CreateMessage();
            netMsg.Write(MessageType.MatchMessage);
            netMsg.Write(matchMsgSerialized);
            netServer.SendMessage(netMsg, netServer.Connections, NetDeliveryMethod.ReliableOrdered, 0);
        }

        public void Dispose()
        {
            Log("Saving match settings");
            using (StreamWriter sw = new StreamWriter(SETTINGS_FILENAME))
            {
                sw.Write(JsonConvert.SerializeObject(matchSettings));
            }
            netServer.Shutdown("Server was closed.");
            Directory.CreateDirectory("Logs\\");
            using (StreamWriter writer = new StreamWriter("Logs\\" + DateTime.Now.ToString("MM-dd-yyyy_HH-mm-ss") + ".txt"))
            {
                foreach (LogEntry entry in log)
                {
                    string logTypeText = "";
                    switch (entry.Type)
                    {
                        case LogType.Debug:
                            logTypeText = " [DEBUG]";
                            break;

                        case LogType.Warning:
                            logTypeText = " [WARNING]";
                            break;

                        case LogType.Error:
                            logTypeText = " [ERROR]";
                            break;
                    }
                    writer.WriteLine(entry.Timestamp + logTypeText + " - " + entry.Message);
                }
            }
        }

        private void Log(object message, LogType type = LogType.Normal)
        {
            LogEntry entry = new LogEntry(DateTime.Now, message.ToString(), type);
            OnLog?.Invoke(this, new LogArgs(entry));
            log.Add(entry);
        }
    }
}