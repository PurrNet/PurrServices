using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

/// <summary>
/// In-memory transport for a server-only and a client-only NetworkManager in the same process.
/// One instance listens, the other connects to it; packets are delivered in order on the next pump.
/// </summary>
public class PairedTestTransport : GenericTransport, ITransport
{
    static readonly Connection Conn = new Connection(1);
    static readonly List<Connection> ConnectedList = new List<Connection> { Conn };
    static readonly List<Connection> EmptyList = new List<Connection>();

    public PairedTestTransport peer;

    public event OnConnected onConnected;
    public event OnDisconnected onDisconnected;
    public event OnDataReceived onDataReceived;
    public event OnDataSent onDataSent;
    public event OnConnectionState onConnectionState;

    readonly Queue<ByteData> _serverInbox = new Queue<ByteData>();
    readonly Queue<ByteData> _clientInbox = new Queue<ByteData>();

    public override bool isSupported => true;
    public override ITransport transport => this;

    public ConnectionState listenerState { get; private set; } = ConnectionState.Disconnected;
    public ConnectionState clientState { get; private set; } = ConnectionState.Disconnected;

    bool clientLinked => peer && peer.clientState == ConnectionState.Connected;

    public IReadOnlyList<Connection> connections =>
        listenerState == ConnectionState.Connected && clientLinked ? ConnectedList :
        clientState == ConnectionState.Connected ? ConnectedList : EmptyList;

    protected override void StartServerInternal() => Listen(default);

    protected override void StartClientInternal() => Connect(default, default);

    public void Listen(ushort port)
    {
        SetListenerState(ConnectionState.Connected);
    }

    public void StopListening()
    {
        if (clientLinked)
            peer.DropClient(DisconnectReason.ServerRequest);
        SetListenerState(ConnectionState.Disconnected);
    }

    public void Connect(string ip, ushort port)
    {
        if (clientState == ConnectionState.Connected)
            return;

        SetClientState(ConnectionState.Connecting);

        if (!peer || peer.listenerState != ConnectionState.Connected)
        {
            SetClientState(ConnectionState.Disconnected);
            return;
        }

        SetClientState(ConnectionState.Connected);
        peer.onConnected?.Invoke(Conn, true);
        onConnected?.Invoke(Conn, false);
    }

    public void Disconnect()
    {
        if (clientState != ConnectionState.Connected)
        {
            SetClientState(ConnectionState.Disconnected);
            return;
        }

        DropClient(DisconnectReason.ClientRequest);
    }

    void DropClient(DisconnectReason reason)
    {
        SetClientState(ConnectionState.Disconnected);
        _clientInbox.Clear();
        onDisconnected?.Invoke(Conn, reason, false);
        if (peer)
        {
            peer._serverInbox.Clear();
            peer.onDisconnected?.Invoke(Conn, reason, true);
        }
    }

    public void CloseConnection(Connection conn)
    {
        if (clientLinked)
            peer.DropClient(DisconnectReason.ServerRequest);
    }

    public void SendToClient(Connection target, ByteData data, Channel method = Channel.ReliableOrdered)
    {
        if (listenerState != ConnectionState.Connected || !clientLinked)
            return;
        peer._clientInbox.Enqueue(data.Duplicate());
        RaiseDataSent(target, data, true);
    }

    public void SendToServer(ByteData data, Channel method = Channel.ReliableOrdered)
    {
        if (clientState != ConnectionState.Connected || !peer)
            return;
        peer._serverInbox.Enqueue(data.Duplicate());
        RaiseDataSent(default, data, false);
    }

    public void RaiseDataReceived(Connection conn, ByteData data, bool asServer) =>
        onDataReceived?.Invoke(conn, data, asServer);

    public void RaiseDataSent(Connection conn, ByteData data, bool asServer) =>
        onDataSent?.Invoke(conn, data, asServer);

    public void ReceiveMessages(float delta)
    {
        while (_serverInbox.Count > 0)
            onDataReceived?.Invoke(Conn, _serverInbox.Dequeue(), true);

        while (_clientInbox.Count > 0)
            onDataReceived?.Invoke(default, _clientInbox.Dequeue(), false);
    }

    public void SendMessages(float delta) { }

    void SetListenerState(ConnectionState state)
    {
        if (listenerState == state)
            return;
        listenerState = state;
        onConnectionState?.Invoke(state, true);
    }

    void SetClientState(ConnectionState state)
    {
        if (clientState == state)
            return;
        clientState = state;
        onConnectionState?.Invoke(state, false);
    }
}
