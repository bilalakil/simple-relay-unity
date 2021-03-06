// Compatible with SimpleRelay >v2.0.0
// https://github.com/bilalakil/simple-relay

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class SimpleRelay : MonoBehaviour
{
    const string DEFAULT_LOCAL_ID = "1";
    const float HEARTBEAT_PERIOD_STABLE = 7f;
    const float HEARTBEAT_PERIOD_UNSTABLE = 1.5f;
    const int HEARTBEAT_COUNT_UNTIL_UNSTABLE = 2;
    const int HEARTBEAT_COUNT_UNTIL_RECONNECT = 5;
    const float PING_FREQUENCY = 3f;
    const string PP_SAVED_CONFIG_TEMPLATE = "SimpleRelay_SavedConfig_{0}";
    const int WS_CONNECTION_RETRIES = 2;
    const int WS_RETRY_DELAY_MS = 10;
    
    static readonly HttpClient HttpClient;
    static readonly TimeSpan NotifyDisconnectPeriod = TimeSpan.FromSeconds(1f);
    static readonly TimeSpan WSTimeout = TimeSpan.FromSeconds(10f);
    static readonly Regex OutgoingMessageStripPattern = new Regex("\\s*,?\\s*\"pinned\":\\s*false\\s*");

    static readonly HashSet<string> _liveSRIDs = new HashSet<string>();
    static readonly HashSet<SimpleRelay> _waitingForNotifyDisconnect = new HashSet<SimpleRelay>();

    static SimpleRelayConfig _pkgConfig;
    internal static SimpleRelayConfig PkgConfig
    {
        get
        {
            if (_pkgConfig == null)
                _pkgConfig = Resources.Load<SimpleRelayConfig>("SimpleRelayConfig");
            
            return _pkgConfig;
        }
    }

    static SimpleRelay()
    {
        HttpClient = new HttpClient();
        HttpClient.Timeout = TimeSpan.FromSeconds(PING_FREQUENCY);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset()
    {
        _pkgConfig = null;
        _liveSRIDs.Clear();
        _waitingForNotifyDisconnect.Clear();
    }
    
    static void IDebugInfoS(string msg)
    {
        if (PkgConfig.srDebugLevel >= SimpleRelayConfig.DebugLevel.Info)
            Debug.Log(string.Format("INFO SimpleRelay: {0}", msg));
    }

    public static bool CanTryRejoining(string sessionType, string localId = DEFAULT_LOCAL_ID)
    {
        var config = TryLoadConfig(localId);
        return config.HasValue &&
            config.Value.sessionType == sessionType;
    }
    
    public static SimpleRelay Rejoin(string localId = DEFAULT_LOCAL_ID) =>
        Spawn().Init(TryLoadConfig(localId).Value);

    public static SimpleRelay JoinRandom(string sessionType, int targetNumMembers, string localId = DEFAULT_LOCAL_ID) =>
        Spawn().Init(new Config {
            sessionType = sessionType,
            numMembers = targetNumMembers,
            localId = localId
        });

    public static SimpleRelay HostPrivate(string sessionType, int targetNumMembers, string localId = DEFAULT_LOCAL_ID) =>
        Spawn().Init(new Config {
            sessionType = sessionType,
            numMembers = targetNumMembers,
            isPrivate = true,
            isHost = true,
            localId = localId
        });

    public static SimpleRelay JoinPrivate(string sessionType, string sessionId, string localId = DEFAULT_LOCAL_ID) =>
        Spawn().Init(new Config {
            sessionType = sessionType,
            isPrivate = true,
            sessionId = sessionId,
            localId = localId
        });

    static SimpleRelay Spawn()
    {
        var obj = new GameObject("SimpleRelay");
        var simpleRelay = obj.AddComponent<SimpleRelay>();
        DontDestroyOnLoad(obj);
        return simpleRelay;
    }

    static Config? TryLoadConfig(string localId)
    {
        var ppKey = PPForLocalId(localId);
        if (!PlayerPrefs.HasKey(ppKey))
            return null;

        var json = PlayerPrefs.GetString(ppKey);
        IDebugInfoS("Loading config: " + json);

        Config? config = null;
        try { config = JsonUtility.FromJson<Config>(json); }
        catch (Exception e)
        {
            IDebugInfoS("Failed to load config, clearing it: " + e.ToString());
            PlayerPrefs.DeleteKey(ppKey);
            throw e;
        }

        return config;
    }

    static string PPForLocalId(string localId) => string.Format(PP_SAVED_CONFIG_TEMPLATE, localId);

    public event Action onStateChanged;
    public event Action<SRMessage> onMessageReceived;

    public SRState State => _state;

    string PPString => PPForLocalId(_config.localId);
    bool IsWaitingForPassword => _config.isPrivate && string.IsNullOrEmpty(_config.sessionId);

    Config _config;
    SRState _state = new SRState();
    long _lastMessageTime;
    float _lastHeartbeatAt;
    int _heartbeatsPending;
    bool _waitingToDisconnect;
    bool _notifyDisconnection;

    [NonSerialized] IWebSocket _ws;
    [NonSerialized] CancellationTokenSource _wsCancellation;
    [NonSerialized] CancellationTokenSource _objCancellation;

    void IDebugError(string msg) =>
        Debug.LogError(string.Format("ERROR SimpleRelay {0}: {1}", _config.localId, msg));
    
    void IDebugInfo(string msg)
    {
        if (PkgConfig.srDebugLevel >= SimpleRelayConfig.DebugLevel.Info)
            Debug.Log(string.Format("INFO SimpleRelay {0}: {1}", _config.localId, msg));
    }

    void OnEnable()
    {
        if (_objCancellation == null)
            _objCancellation = new CancellationTokenSource();

        if (_config.initd)
        {
            IDebugInfo("Re-enabled");
            Reconnect();

            _liveSRIDs.Add(_config.localId);
        }
    }

    void Update() => TickHeartbeatLoop();

    void OnDisable()
    {
        if (_state.ConnStatus == SRState.ConnectionStatus.Disconnected)
            return;

        IDebugInfo("Disabled");
        Pause();
    }

    void OnDestroy()
    {
        _objCancellation.Cancel(false);

        IDebugInfo("Destroyed");

        if (_state.CurStage == SRState.Stage.ConnectionClosed)
            return;

        var canReconnectLater = _config.isPrivate || _state.CurStage != SRState.Stage.WaitingForMoreMembers;
        Teardown(
            SRState.DisconnectReason.ConnectionDied,
            true,
            !canReconnectLater
        );
    }

    public void Send(string payload, bool pinned = false)
    {
        if (_state.ConnStatus != SRState.ConnectionStatus.Connected)
            return;

        var msg = new OutgoingMessage { payload = payload, pinned = pinned };
        var json = JsonUtility.ToJson(msg);
        var strippedJson = OutgoingMessageStripPattern.Replace(json, "");

        _ws.Send(strippedJson, WSTimeout);
    }

    public void ClearSendQueue()
    {
        if (_state.ConnStatus != SRState.ConnectionStatus.Connected)
            return;

        _ws.ClearSendQueue();
    }

    public void Reconnect(bool force = false)
    {
        if (
            !force &&
            _state.ConnStatus != SRState.ConnectionStatus.Disconnected ||
            (
                _state.DCReason != SRState.DisconnectReason.ConnectionDied &&
                _state.DCReason != SRState.DisconnectReason.InitialConnectionFailed
            )
        ) return;

        IDebugInfo("Reconnecting");

        KillWS();

        _state.connStatus = 
            _state.DCReason == SRState.DisconnectReason.InitialConnectionFailed
                ? SRState.ConnectionStatus.Connecting
                : _state.connStatus = SRState.ConnectionStatus.Disconnected;

        if (_state.memberNum != -1)
            _state.memberPresence[_state.memberNum] = false;
        _state.ready = false;
        _state.disconnectReason = SRState.DisconnectReason.ConnectionDied;

        RunWS();
    }

    public void Pause()
    {
        if (_state.ConnStatus == SRState.ConnectionStatus.Disconnected)
            return;

        IDebugInfo("Pausing");
        Teardown(SRState.DisconnectReason.DisconnectRequested, false, false);
    }

    public void Disconnect(bool endSession)
    {
        // Still needs to run while ConnectionClosed,
        // i.e. to specify close reason and fully tear down 

        if (endSession && _state.CurStage == SRState.Stage.SessionInProgress)
        {
            IDebugInfo("End session requested. Will try to send end session instruction");

            SetWaitingToDisconnect();

            _config.tryingToEndSession = true;
            SendHeartbeat();
            return;
        }

        if (
            _ws != null
            && (
                _state.ConnStatus == SRState.ConnectionStatus.Connecting ||
                _state.ConnStatus == SRState.ConnectionStatus.Reconnecting
            )
        )
        {
            IDebugInfo("Disconnection requested. Waiting until connected for clean disconnection");

            SetWaitingToDisconnect();
            return;
        }

        IDebugInfo("Disconnection request. Acting immediately");
        Teardown(SRState.DisconnectReason.DisconnectRequested);
    }

    SimpleRelay Init(Config config)
    {
        if (string.IsNullOrEmpty(config.localId))
            throw new InvalidOperationException("Invalid local ID");

        if (_liveSRIDs.Contains(config.localId))
            throw new NotSupportedException(
                "Duplicate SimpleRelay ID used. " +
                "Either terminate the existing SimpleRelay, " +
                "or create a new one with a different local ID"
            );

        config.initd = true;
        _config = config;
        _liveSRIDs.Add(_config.localId);

        if (
            !string.IsNullOrEmpty(_config.sessionId) &&
            string.IsNullOrEmpty(_config.memberId)
        ) _state.password = _config.sessionId;
        if (_config.sessionStarted)
            _state.stage = SRState.Stage.SessionInProgress;
        if (_config.isHost) _state.isHost = true;
        
        RunWS();
        
        return this;
    }

    async void RunWS() => await RunWSAsync();
    async Task RunWSAsync()
    {
        var wsConnectionAttemptNum = 0;
        
        while (true)
        {
            _state.connStatus =
                _state.ConnStatus == SRState.ConnectionStatus.Disconnected &&
                _state.disconnectReason == SRState.DisconnectReason.ConnectionDied
                    ? SRState.ConnectionStatus.Reconnecting
                    : SRState.ConnectionStatus.Connecting;
            onStateChanged?.Invoke();

            _wsCancellation?.Cancel();
            _wsCancellation = new CancellationTokenSource();
            var localCancellation = _wsCancellation;

            IDebugInfo("Testing web connectivity via HTTPS ping");
            while (true)
            {
                HttpResponseMessage res = null;
                try { res = await HttpClient.GetAsync(GetPingURL(), localCancellation.Token); }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }

                if (localCancellation.IsCancellationRequested)
                    return;

                if (res != null && res.StatusCode == HttpStatusCode.OK)
                    break;

                if (
                    _state.CurStage == SRState.Stage.WaitingForMoreMembers &&
                    _state.ConnStatus == SRState.ConnectionStatus.Connecting
                )
                {
                    IDebugInfo(
                        "Failed to ping during initial connection. Aborting. Ping URL: " + 
                        GetPingURL()
                    );
                    Teardown(SRState.DisconnectReason.InitialConnectionFailed, false);
                    return;
                }

                await Task.Run(
                    () => localCancellation.Token.WaitHandle.WaitOne(
                        HttpClient.Timeout
                    )
                );
                if (localCancellation.IsCancellationRequested)
                    return;
            }
            IDebugInfo("Ping successful");

            if (_waitingForNotifyDisconnect.Count != 0)
                IDebugInfo("Waiting for previous web socket to notify disconnect");
            while (_waitingForNotifyDisconnect.Count != 0)
            {
                await Task.Run(
                    () => localCancellation.Token.WaitHandle.WaitOne(
                        WS_RETRY_DELAY_MS
                    )
                );
                if (localCancellation.IsCancellationRequested)
                    return;
            }

            var url = GetConnectionURL(_config);
            _ws = WebSocketFactory.Get(url, localCancellation.Token);
            _ws.onMessageReceived += HandleWSMessageReceived;

            IDebugInfo("WS attempting connection to: " + url);

            await _ws.Connect(WSTimeout);
            if (localCancellation.IsCancellationRequested)
                return;

            if (!_ws.IsConnected)
            {
                if (wsConnectionAttemptNum != WS_CONNECTION_RETRIES)
                {
                    IDebugInfo("WS failed to connect. Retrying");

                    ++wsConnectionAttemptNum;
                    SetNotifyDisconnection();

                    continue;
                }

                if (
                    _state.CurStage == SRState.Stage.WaitingForMoreMembers &&
                    !string.IsNullOrEmpty(_config.memberId)
                )
                {
                    IDebugInfo("WS failed to connect. Clearing member ID and trying again");

                    _config.memberId = null;
                    SaveConfig();

                    Reconnect(true);
                    return;
                }

                IDebugInfo(
                    "WS failed to connect. Connection URL: " +
                    GetConnectionURL(_config)
                );
                Teardown(
                    _state.ConnStatus == SRState.ConnectionStatus.Reconnecting
                        ? SRState.DisconnectReason.ConnectionDied
                        : SRState.DisconnectReason.InitialConnectionFailed
                );
                return;
            }

            if (_waitingToDisconnect && !_config.tryingToEndSession)
            {
                IDebugInfo("Disconnecting now that WS is properly connected");
                Teardown(SRState.DisconnectReason.DisconnectRequested);
                return;
            }

            if (_config.tryingToEndSession)
                IDebugInfo("WS successfully connected. Trying to send END_SESSION");
            else
                IDebugInfo("WS successfully connected. Entering message loop. Waiting for SESSION_CONNECT");

            _state.connIsStable = true;
            _state.connStatus = SRState.ConnectionStatus.Connected;
            onStateChanged?.Invoke();

            if (_config.tryingToEndSession)
                SendHeartbeat();
            
            wsConnectionAttemptNum = 0;
            _lastHeartbeatAt = Time.unscaledTime;
            _heartbeatsPending = 0;

            await _ws.ReceiveLoop();
            if (localCancellation.IsCancellationRequested)
                return;

            IDebugInfo("WS receive loop died. Reconnecting");

            Reconnect();
            return;
        }
    }

    void KillWS()
    {
        _state.connStatus = SRState.ConnectionStatus.Disconnected;

        if (_ws != null)
        {
            _ws.onMessageReceived -= HandleWSMessageReceived;
            _ws.Dispose();
            _ws = null;
        }

        if (_wsCancellation != null)
        {
            _wsCancellation.Cancel(false);
            _wsCancellation = null;
        }
    }

    void HandleWSMessageReceived(string json)
    {
        if (
            PkgConfig.srDebugLevel >= SimpleRelayConfig.DebugLevel.Info &&
            json != "[{\"type\":\"HEARTBEAT\"}]"
        ) IDebugInfo("Received message: " + json);

        Messages messages;
        try
        {
            messages = JsonUtility.FromJson<Messages>("{\"messages\":"+json+"}");
            if (messages.messages.Count == 0)
                throw new Exception("JsonUtility silently failed to parse message");
        }
        catch (Exception e)
        {
            IDebugError("Failed to parse raw message: " + e.ToString() + "\n\n" + json);
            return;
        }

        foreach (var message in messages.messages)
            RouteMessage(message);
    }

    void RouteMessage(Message message)
    {
        MsgType msgType;
        try
        {
            msgType = (MsgType)Enum.Parse(typeof(MsgType), message.type);
        }
        catch (ArgumentException)
        {
            IDebugError("Unhandled message type: " + message.type);
            return;
        }

        if (msgType == MsgType.CONNECTION_OVERWRITE)
            HandleConnectionOverwrite();
        else if (msgType == MsgType.INVALID_CONNECTION)
            HandleInvalidConnection();
        else if (msgType == MsgType.SESSION_END)
            HandleSessionEnd();
        else if (!_config.tryingToEndSession)
        {
            if (msgType == MsgType.CONNECTION)
                HandleConnection(message);
            else if (msgType == MsgType.HEARTBEAT)
                HandleHeartbeat();
            else if (msgType == MsgType.MEMBER_DISCONNECT)
                HandleMemberDisconnect(message);
            else if (msgType == MsgType.MEMBER_RECONNECT)
                HandleMemberReconnect(message);
            else if (msgType == MsgType.MESSAGE)
                HandleMessage(message);
            else if (msgType == MsgType.PRIVATE_SESSION_PENDING)
                HandlePrivateSessionPending(message);
            else if (msgType == MsgType.SESSION_CONNECT)
                HandleSessionConnect(message);
        }
    }

    void HandleConnection(Message message)
    {
        IDebugInfo("CONNECTION (memberId) received");

        _config.memberId = message.memberId;
        SaveConfig();
    }

    void HandleConnectionOverwrite()
    {
        IDebugInfo("CONNECTION_OVERWRITE received");
        Teardown(SRState.DisconnectReason.ConnectionOverwritten);
    }

    void HandleHeartbeat()
    {
        _heartbeatsPending = 0;

        if (!_state.ConnIsStable)
        {
            _state.connIsStable = true;
            onStateChanged?.Invoke();
        }
    }

    void HandleInvalidConnection()
    {
        IDebugInfo("INVALID_CONNECTION received");
        Reconnect();
    }

    void HandleMemberDisconnect(Message message)
    {
        _state.memberPresence[message.memberNum] = false;
        onStateChanged?.Invoke();
    }

    void HandleMemberReconnect(Message message)
    {
        _state.memberPresence[message.memberNum] = true;
        onStateChanged?.Invoke();
    }

    void HandleMessage(IMessage message)
    {
        SRMessage srMessage;
        if (message is SRMessage) srMessage = message as SRMessage;
        else srMessage = new SRMessage(message);

        _lastMessageTime = srMessage.Time;

        onMessageReceived?.Invoke(srMessage);

        if (message.Pinned)
        {
            _state.pinnedMessage = srMessage;
            onStateChanged?.Invoke();
        }
    }

    void HandlePrivateSessionPending(Message message)
    {
        // Could be received multiple times due to heartbeat
        if (_state.Ready)
            return;

        _config.sessionId = message.sessionId;
        
        _state.password = message.sessionId;

        SaveConfig();
        onStateChanged?.Invoke();
    }

    void HandleSessionEnd()
    {
        IDebugInfo("SESSION_END received");
        Teardown(SRState.DisconnectReason.SessionEnded);
    }

    void HandleSessionConnect(Message message)
    {
        // Could be received multiple times due to heartbeat
        if (_state.Ready)
            return;

        _config.sessionId = message.sessionId;
        _config.numMembers = message.memberPresence.Length;
        _config.sessionStarted = true;

        _state.memberNum = message.memberNum;
        _state.memberPresence = message.memberPresence;
        _state.pinnedMessage = message.pinnedMessage;
        _state.ready = true;
        _state.stage = SRState.Stage.SessionInProgress;
        _state.password = null;

        _state.memberPresence = new bool[_config.numMembers];
        for (var i = 0; i != _config.numMembers; ++i)
            _state.memberPresence[i] = true;
        
        if (_state.PinnedMessage != null)
            _lastMessageTime = Math.Max(
                _lastMessageTime,
                _state.PinnedMessage.Time
            );

        SaveConfig();
        onStateChanged?.Invoke();
    }

    string GetConnectionURL(Config config)
    {
        var parts = new List<string>();

        var haveSessionId = !string.IsNullOrEmpty(config.sessionId);
        if (haveSessionId)
            parts.Add("sessionId=" + WebUtility.UrlEncode(config.sessionId));

        if (!string.IsNullOrEmpty(config.memberId))
            parts.Add("memberId=" + WebUtility.UrlEncode(config.memberId));
        else
        {
            if (!string.IsNullOrEmpty(config.sessionType))
                parts.Add("sessionType=" + WebUtility.UrlEncode(config.sessionType));

            if (!haveSessionId)
            {
                if (config.numMembers != 0)
                    parts.Add("targetNumMembers=" + config.numMembers);
                if (config.isPrivate)
                    parts.Add("private=true");
            }
        }

        return PkgConfig.simpleRelayWSSURL + "?" + string.Join("&", parts);
    }

    string GetPingURL() => PkgConfig.simpleRelayHTTPSURL + "/ping";

    string GetNotifyDisconnectionURL() => string.Format(
        "{0}/notifyDisconnect/{1}",
        PkgConfig.simpleRelayHTTPSURL,
        _config.memberId
    );

    void SaveConfig()
    {
        if (_waitingToDisconnect)
            return;

        IDebugInfo("Saving config");
        PlayerPrefs.SetString(PPString, JsonUtility.ToJson(_config));
    }

    void Teardown(SRState.DisconnectReason reason, bool closeConnection = true, bool clearConfig = true)
    {
        IDebugInfo("Tearing down");

        KillWS();

        _liveSRIDs.Remove(_config.localId);

        if (closeConnection) _state.stage = SRState.Stage.ConnectionClosed;
        if (clearConfig) ClearConfig();

        _state.connStatus = SRState.ConnectionStatus.Disconnected;
        _state.disconnectReason = reason;
        onStateChanged?.Invoke();

        if (!string.IsNullOrEmpty(_config.memberId)) SetNotifyDisconnection();
        else if (closeConnection)
        {
            IDebugInfo("Goodbye");
            Destroy(gameObject);
        }
    }

    void ClearConfig()
    {
        if (string.IsNullOrEmpty(_config.localId))
            return;

        IDebugInfo("Clearing config");

        PlayerPrefs.DeleteKey(PPString);
    }

    void TickHeartbeatLoop()
    {
        if (
            _state.ConnStatus != SRState.ConnectionStatus.Connected ||
            _wsCancellation.IsCancellationRequested
        ) return;
        
        var heartbeatDelay =
            IsWaitingForPassword ||
            (_state.CurStage == SRState.Stage.SessionInProgress && !_state.Ready) ||
            _heartbeatsPending != 0
                ? HEARTBEAT_PERIOD_UNSTABLE
                : HEARTBEAT_PERIOD_STABLE;
            
        if (Time.unscaledTime < _lastHeartbeatAt + heartbeatDelay)
            return;

        SendHeartbeat();
    }

    void SendHeartbeat()
    {
        if (_state.ConnStatus != SRState.ConnectionStatus.Connected)
            return;

        var waitingFor = new List<string>();
        if (!_state.ready)
        {
            if (string.IsNullOrEmpty(_config.memberId))
                waitingFor.Add("\"CONNECTION\"");

            if (IsWaitingForPassword)
                waitingFor.Add("\"PRIVATE_SESSION_PENDING\"");

            waitingFor.Add("\"SESSION_CONNECT\"");
        }

        _ws.Send(
            _config.tryingToEndSession
                ? "{\"action\":\"END_SESSION\"}"
                : string.Format(
                    "{{" +
                        "\"action\":\"HEARTBEAT\"" +
                        ",\"inclMessagesAfter\":{0}" +
                        (
                            waitingFor.Count == 0 ? ""
                                : ",\"waitingFor\":[" + string.Join(",", waitingFor) + "]"
                        ) +
                        "}}",
                    _lastMessageTime
            ),
            WSTimeout
        );

        _lastHeartbeatAt = Time.unscaledTime;
        ++_heartbeatsPending;

        if (_heartbeatsPending == HEARTBEAT_COUNT_UNTIL_RECONNECT)
        {
            IDebugInfo("Too many missed heartbeats");
            Reconnect(true);
            return;
        }

        if (
            _heartbeatsPending >= HEARTBEAT_COUNT_UNTIL_UNSTABLE &&
            _state.ConnIsStable
        )
        {
            _state.connIsStable = false;
            onStateChanged?.Invoke();
        }
    }

    void SetNotifyDisconnection()
    {
        if (string.IsNullOrEmpty(_config.memberId))
            return;
        _notifyDisconnection = true;

        StartNotifyDisconnectionLoop();
    }

    // This is implemented differently to the heartbeat loop
    // because we want it to continue even if the game object is disabled
    async void StartNotifyDisconnectionLoop()
    {
        if (_waitingForNotifyDisconnect.Contains(this))
            return;
        _waitingForNotifyDisconnect.Add(this);

        while (_notifyDisconnection)
        {
            var timeUp = false;
            var timeUntilNextSend = Task.Run(
                () => _objCancellation.Token.WaitHandle.WaitOne(
                    NotifyDisconnectPeriod
                )
            ).ContinueWith((_) => timeUp = true);

            await Task.WhenAny(
                SendNotifyDisconnection(),
                timeUntilNextSend
            );

            if (_objCancellation.IsCancellationRequested)
                break;
            if (!timeUp)
                await timeUntilNextSend;
            if (_objCancellation.IsCancellationRequested)
                break;
        }

        _waitingForNotifyDisconnect.Remove(this);
    }

    async Task SendNotifyDisconnection()
    {
        HttpResponseMessage res = null;
        try { res = await HttpClient.GetAsync(GetNotifyDisconnectionURL(), _objCancellation.Token); }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }

        if (_objCancellation.Token.IsCancellationRequested)
            return;
        if (res == null || res.StatusCode != HttpStatusCode.OK)
            return;

        _notifyDisconnection = false;
    
        IDebugInfo("Notify disconnect successful");

        if (_state.CurStage == SRState.Stage.ConnectionClosed)
        {
            IDebugInfo("Goodbye");
            Destroy(gameObject);
        }
    }

    void SetWaitingToDisconnect()
    {
        _waitingToDisconnect = true;

        _liveSRIDs.Remove(_config.localId);
        ClearConfig();
        _config.localId = "";
    }

    enum MsgType
    {
        CONNECTION,
        CONNECTION_OVERWRITE,
        HEARTBEAT,
        INVALID_CONNECTION,
        MEMBER_DISCONNECT,
        MEMBER_RECONNECT,
        MESSAGE,
        PRIVATE_SESSION_PENDING,
        SESSION_CONNECT,
        SESSION_END
    }

    [Serializable]
    struct Config
    {
        public bool initd;
        public string localId;
        public string sessionType;
        public int numMembers;
        public bool isPrivate;
        public bool isHost;
        public string sessionId;
        public string memberId;
        public bool sessionStarted;
        public bool tryingToEndSession;
    }

    public interface IMessage
    {
        int MemberNum { get; }
        string Payload { get; }
        bool Pinned { get; }
        ///<summary>Milliseconds since epoch</summary>
        long Time { get; }
    }

    [Serializable]
    struct Messages
    {
#pragma warning disable CS0649
        public List<Message> messages;
#pragma warning restore CS0649
    }

    [Serializable]
    struct Message : SimpleRelay.IMessage
    {
        // SimpleRelay.IMessage
        public int MemberNum => memberNum;
        public String Payload => payload;
        public bool Pinned => pinned;
        public long Time => time;


#pragma warning disable CS0649
        public string type;

        // CONNECTION
        public string memberId;

        // MESSAGE, SESSION_CONNECT
        public int memberNum;

        // MESSAGE
        public string payload;
        public bool pinned;
        public long time;

        // SESSION_CONNECT
        public string sessionType;
        public string sessionId;
        public bool[] memberPresence;
        public SRMessage pinnedMessage;
#pragma warning restore CS0649
    }

    class OutgoingMessage
    {
        public string action = "SEND_MESSAGE";
        public string payload;
        public bool pinned;
    }
}

[Serializable]
public class SRState
{
    public Stage CurStage => stage;
    public ConnectionStatus ConnStatus => connStatus;
    /// <summary>Only relevant during `ConnectionStatus.Connected`</summary>
    public bool ConnIsStable => connIsStable;
    /// <summary>Even when connected, state may be out of date while `!Ready`</summary>
    public bool Ready => ready;
    /// <summary>Not relevant during `ConnectionStatus.Connecting` or `ConnectionStatus.Connected`</summary>
    public DisconnectReason DCReason => disconnectReason;
    public int MemberNum => memberNum;
    public IReadOnlyList<bool> MemberPresence => memberPresence;
    public bool IsHost => isHost;
    public string Password => password == "" ? null : password;

    // Odd check is to avoid deserialisation issues,
    // specifically that missing values are still constructed during deserialisation 
    public SRMessage PinnedMessage =>
        (pinnedMessage?.Time ?? 0) == 0 ? null : pinnedMessage;

    [SerializeField] internal Stage stage =
        Stage.WaitingForMoreMembers;
    [SerializeField] internal ConnectionStatus connStatus =
        ConnectionStatus.Connecting;
    [SerializeField] internal bool connIsStable = true;
    [SerializeField] internal bool ready;
    [SerializeField] internal DisconnectReason disconnectReason;
    [SerializeField] internal int memberNum = -1;
    [SerializeField] internal bool[] memberPresence;
    [SerializeField] internal SRMessage pinnedMessage;
    [SerializeField] internal bool isHost;
    [SerializeField] internal string password;

    public enum Stage
    {
        WaitingForMoreMembers,
        SessionInProgress,
        ConnectionClosed
    }

    public enum ConnectionStatus
    {
        Connecting,
        Connected,
        Reconnecting,
        Disconnected
    }

    public enum DisconnectReason
    {
        InitialConnectionFailed,
        SessionEnded,
        ConnectionOverwritten,
        ConnectionDied,
        DisconnectRequested
    }
}

[Serializable]
public class SRMessage : SimpleRelay.IMessage
{
    // SimpleRelay.IMessage
    public int MemberNum => memberNum;
    public String Payload => payload;
    public bool Pinned => pinned;
    public long Time => time;


#pragma warning disable CS0649
    [SerializeField] internal int memberNum;
    [SerializeField] internal string payload;
    [SerializeField] internal bool pinned;
    [SerializeField] internal long time;
#pragma warning restore CS0649

    internal SRMessage(SimpleRelay.IMessage message)
    {
        memberNum = message.MemberNum;
        payload = message.Payload;
        pinned = message.Pinned;
        time = message.Time;
    }
}
