using System;
using System.Collections.Generic;
using System.Threading;
using enet;
using NativeCollections;
using Netick.Unity;
using UnityEngine;
using static enet.ENet;
using Socket = System.Net.Sockets.Socket;

namespace Netick.Transport
{
    [CreateAssetMenu(fileName = "ENetTransportProvider", menuName = "Netick/Transport/ENetTransportProvider", order = 1)]
    public sealed class ENetTransportProvider : NetworkTransportProvider
    {
        public override NetworkTransport MakeTransportInstance() => new ENetTransport();
    }

    public unsafe struct ENetEndPoint : IEndPoint
    {
        public ENetEndPoint(ENetPeer* peer)
        {
            IPAddress = peer->address.host.ToString();
            Port = peer->address.port;
        }

        public string IPAddress { get; }
        public int Port { get; }
    }

    public sealed unsafe class ENetConnection : TransportConnection
    {
        private ENetTransport _transport;
        private ENetEndPoint _endPoint;
        internal ENetPeer* Peer;

        public override IEndPoint EndPoint => _endPoint;
        public override int Mtu => (int)ENET_HOST_DEFAULT_MTU;

        public void Initialize(ENetTransport transport, ENetPeer* peer)
        {
            _transport = transport;
            _endPoint = new ENetEndPoint(peer);
            Peer = peer;
        }

        public override void Send(IntPtr ptr, int length) => _transport.Send(Peer, ptr, length, ENetPacketFlag.ENET_PACKET_FLAG_UNSEQUENCED);
        public override void SendUserData(IntPtr ptr, int length, TransportDeliveryMethod transportDeliveryMethod) => _transport.Send(Peer, ptr, length, transportDeliveryMethod == TransportDeliveryMethod.Reliable ? ENetPacketFlag.ENET_PACKET_FLAG_RELIABLE : ENetPacketFlag.ENET_PACKET_FLAG_UNSEQUENCED);
    }

    public sealed unsafe class ENetTransport : NetworkTransport
    {
        private int _state;
        private ENetHost* _host;
        private ENetConnection[] _peers;
        private Queue<ENetConnection> _freePeers;
        private NativeConcurrentQueue<ENetEvent> _incomings;
        private NativeConcurrentQueue<nint> _removedPeers;
        private NativeConcurrentQueue<ENetOutgoing> _outgoings;
        private BitBuffer _buffer;

        internal void Send(ENetPeer* peer, IntPtr ptr, int length, ENetPacketFlag flags) => _outgoings.Enqueue(ENetOutgoing.Create(peer, (byte*)ptr, length, flags));

        public override void Init()
        {
            Application.runInBackground = true;
            _buffer = new BitBuffer(createChunks: false);
        }

        public override void Connect(string address, int port, byte[] connectionData, int connectionDataLength)
        {
            ENetAddress enetAddress;
            enet_set_ip(&enetAddress, address);
            enetAddress.port = (ushort)port;
            var peer = enet_host_connect(_host, &enetAddress, 0, 0);
            enet_peer_ping_interval(peer, 500);
            enet_peer_timeout(peer, 5000, 0, 0);
        }

        public override void Disconnect(TransportConnection connection)
        {
            var enetConnection = (ENetConnection)connection;
            _removedPeers.Enqueue((nint)enetConnection.Peer);
        }

        public override void Run(RunMode mode, int port)
        {
            if (_host != null)
                return;
            _incomings = new NativeConcurrentQueue<ENetEvent>(1, 2);
            _removedPeers = new NativeConcurrentQueue<nint>(1, 2);
            _outgoings = new NativeConcurrentQueue<ENetOutgoing>(1, 2);
            enet_initialize();
            if (mode == RunMode.Client)
            {
                _peers = new ENetConnection[1];
                _host = enet_host_create(null, 1, 0, 0, 0);
            }
            else
            {
                _peers = new ENetConnection[Engine.MaxClients];
                _freePeers = new Queue<ENetConnection>(Engine.MaxClients);
                ENetAddress enetAddress;
                enet_set_ip(&enetAddress, Socket.OSSupportsIPv6 ? "::0" : "0.0.0.0");
                enetAddress.port = (ushort)port;
                _host = enet_host_create(&enetAddress, Engine.MaxClients, 0, 0, 0);
            }

            new Thread(Service) { IsBackground = true }.Start();
        }

        private void Service()
        {
            try
            {
                var @event = new ENetEvent();
                var spinCount = 0;
                Interlocked.Exchange(ref _state, 1);
                while (_state == 1)
                {
                    while (_removedPeers.TryDequeue(out var peer))
                        enet_peer_disconnect_now((ENetPeer*)peer, 0);
                    while (_outgoings.TryDequeue(out var outgoing))
                    {
                        if (enet_peer_send(outgoing.Peer, 0, outgoing.Packet) != 0)
                            enet_packet_destroy(outgoing.Packet);
                    }

                    var polled = false;
                    while (!polled)
                    {
                        if (enet_host_check_events(_host, &@event) <= 0)
                        {
                            if (enet_host_service(_host, &@event, 1) <= 0)
                                break;
                            polled = true;
                        }

                        if (@event.type == ENetEventType.ENET_EVENT_TYPE_NONE)
                            continue;
                        if (@event.type == ENetEventType.ENET_EVENT_TYPE_CONNECT)
                        {
                            var peer = @event.peer;
                            enet_peer_ping_interval(peer, 500);
                            enet_peer_timeout(peer, 5000, 0, 0);
                        }

                        _incomings.Enqueue(@event);
                    }

                    enet_host_flush(_host);
                    if ((spinCount >= 10 && (spinCount - 10) % 2 == 0) || Environment.ProcessorCount == 1)
                    {
                        var yieldsSoFar = spinCount >= 10 ? (spinCount - 10) / 2 : spinCount;
                        if (yieldsSoFar % 5 == 4)
                            Thread.Sleep(0);
                        else
                            Thread.Yield();
                    }
                    else
                    {
                        var iterations = Environment.ProcessorCount / 2;
                        if (spinCount <= 30 && 1 << spinCount < iterations)
                            iterations = 1 << spinCount;
                        Thread.SpinWait(iterations);
                    }

                    spinCount = spinCount == int.MaxValue ? 10 : spinCount + 1;
                }
            }
            finally
            {
                if (_host != null)
                {
                    foreach (var peer in _peers)
                    {
                        if (peer != null)
                            enet_peer_disconnect_now(peer.Peer, 0);
                    }

                    enet_host_flush(_host);
                    enet_host_destroy(_host);
                }

                _removedPeers.Dispose();
                while (_outgoings.TryDequeue(out var outgoing))
                    enet_packet_destroy(outgoing.Packet);
                _outgoings.Dispose();
                while (_incomings.TryDequeue(out var networkEvent))
                    enet_packet_destroy(networkEvent.packet);
                _incomings.Dispose();
                enet_deinitialize();
            }
        }

        public override void Shutdown() => Interlocked.Exchange(ref _state, 0);

        public override void PollEvents()
        {
            while (_incomings.TryDequeue(out var netEvent))
            {
                var peer = netEvent.peer;
                ENetConnection connection;
                switch (netEvent.type)
                {
                    case ENetEventType.ENET_EVENT_TYPE_CONNECT:
                        if (_freePeers == null || !_freePeers.TryDequeue(out connection))
                            connection = new ENetConnection();
                        connection.Initialize(this, peer);
                        _peers[peer->incomingPeerID] = connection;
                        NetworkPeer.OnConnected(connection);
                        continue;
                    case ENetEventType.ENET_EVENT_TYPE_DISCONNECT:
                        connection = _peers[peer->incomingPeerID];
                        if (connection != null)
                        {
                            _peers[peer->incomingPeerID] = null;
                            NetworkPeer.OnDisconnected(connection, TransportDisconnectReason.Shutdown);
                            _freePeers?.Enqueue(connection);
                        }

                        continue;
                    case ENetEventType.ENET_EVENT_TYPE_RECEIVE:
                        connection = _peers[peer->incomingPeerID];
                        if (connection != null)
                        {
                            _buffer.SetFrom(netEvent.packet->data, (int)netEvent.packet->dataLength, (int)netEvent.packet->dataLength);
                            NetworkPeer.Receive(connection, _buffer);
                        }

                        continue;
                    default:
                        continue;
                }
            }
        }
    }
}