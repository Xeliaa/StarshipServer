﻿/* 
 * Starship Server
 * 
 * This file is a part of Starship Server.
 * Starship Server is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
 * Starship Server is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
 * You should have received a copy of the GNU General Public License along with Starship Server. If not, see http://www.gnu.org/licenses/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using com.goodstuff.Starship.Util;
using com.goodstuff.Starship.Extensions;
using com.goodstuff.Starship.Packets;
using com.goodstuff.Starship.Permissions;

namespace com.goodstuff.Starship
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable")]
    class Client
    {
        public PlayerData playerData = new PlayerData();
        public ClientState state = ClientState.PendingConnect;

        private TcpClient cSocket;
        private BinaryReader cIn;
        private BinaryWriter cOut;

        private TcpClient sSocket;
        private BinaryReader sIn;
        private BinaryWriter sOut;

        private Thread ServerForwarder;
        private Thread ClientForwarder;

        public int connectedTime = 0;

        public int kickTargetTimestamp = 0;
        public bool connectionAlive { get { if (this.cSocket.Connected && this.sSocket.Connected && this.state != ClientState.Disposing) return true; else return false; } }

        public List<Packet11ChatSend> packetQueue = new List<Packet11ChatSend>();

        public Client(TcpClient aClient)
        {
            this.cSocket = aClient;
        }

        public void run()
        {
            try
            {
                this.cIn = new BinaryReader(this.cSocket.GetStream());
                this.cOut = new BinaryWriter(this.cSocket.GetStream());

                IPEndPoint ipep = (IPEndPoint)this.cSocket.Client.RemoteEndPoint;
                IPAddress ipa = ipep.Address;

                this.playerData.ip = ipep.Address.ToString();

                StarshipServer.logInfo("[" + playerData.client + "] Accepting new connection.");

                if((int)StarshipServer.serverState < 3)
                {
                    MemoryStream packet = new MemoryStream();
                    BinaryWriter packetWrite = new BinaryWriter(packet);
                    packetWrite.WriteBE(StarshipServer.ProtocolVersion);
                    this.sendClientPacket(Packet.ProtocolVersion, packet.ToArray());

                    rejectPreConnected("Connection Failed: The server is not ready yet.");
                    return;
                }
                else if ((int)StarshipServer.serverState > 3)
                {
                    MemoryStream packet = new MemoryStream();
                    BinaryWriter packetWrite = new BinaryWriter(packet);
                    packetWrite.WriteBE(StarshipServer.ProtocolVersion);
                    this.sendClientPacket(Packet.ProtocolVersion, packet.ToArray());

                    rejectPreConnected("Connection Failed: The server is shutting down.");
                    return;
                }
                else if (StarshipServer.restartTime != 0)
                {
                    MemoryStream packet = new MemoryStream();
                    BinaryWriter packetWrite = new BinaryWriter(packet);
                    packetWrite.WriteBE(StarshipServer.ProtocolVersion);
                    this.sendClientPacket(Packet.ProtocolVersion, packet.ToArray());

                    rejectPreConnected("Connection Failed: The server is restarting.");
                    return;
                }

                sSocket = new TcpClient();
                sSocket.ReceiveTimeout = StarshipServer.config.internalSocketTimeout * 1000;
                sSocket.SendTimeout = StarshipServer.config.internalSocketTimeout * 1000;
                IAsyncResult result = sSocket.BeginConnect((StarshipServer.config.proxyIP.Equals("0.0.0.0") ? IPAddress.Loopback : IPAddress.Parse(StarshipServer.config.proxyIP)), StarshipServer.config.serverPort, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(3000, true);
                if (!success || !sSocket.Connected)
                {
                    StarshipServer.failedConnections++;
                    if (StarshipServer.failedConnections >= StarshipServer.config.maxFailedConnections)
                    {
                        StarshipServer.logFatal(StarshipServer.failedConnections + " clients failed to connect in a row. Restarting...");
                        StarshipServer.serverState = ServerState.Crashed;
                    }
                    MemoryStream packet = new MemoryStream();
                    BinaryWriter packetWrite = new BinaryWriter(packet);
                    packetWrite.WriteBE(StarshipServer.ProtocolVersion);
                    this.sendClientPacket(Packet.ProtocolVersion, packet.ToArray());
                    rejectPreConnected("Connection Failed: Unable to connect to the parent server.");
                    return;
                }

                this.sIn = new BinaryReader(this.sSocket.GetStream());
                this.sOut = new BinaryWriter(this.sSocket.GetStream());

                this.connectedTime = Utils.getTimestamp();

                // Forwarding for data from SERVER (sIn) to CLIENT (cOut)
                this.ServerForwarder = new Thread(new ThreadStart(new ForwardThread(this, this.sIn, this.cOut, Direction.Server).run));
                ServerForwarder.Start();

                // Forwarding for data from CLIENT (cIn) to SERVER (sOut)
                this.ClientForwarder = new Thread(new ThreadStart(new ForwardThread(this, this.cIn, this.sOut, Direction.Client).run));
                ClientForwarder.Start();

                StarshipServer.failedConnections = 0;
            }
            catch (Exception e)
            {
                StarshipServer.logException("ClientThread Exception: " + e.ToString());
                StarshipServer.failedConnections++;
                MemoryStream packet = new MemoryStream();
                BinaryWriter packetWrite = new BinaryWriter(packet);
                packetWrite.WriteBE(StarshipServer.ProtocolVersion);
                this.sendClientPacket(Packet.ProtocolVersion, packet.ToArray());
                rejectPreConnected("Connection Failed: A internal server error occurred (1)");
            }
        }

        public void sendClientPacket(Packet packetID, byte[] packetData)
        {
            if (this.kickTargetTimestamp != 0) return;
            try
            {
                if (this.cOut.BaseStream.CanWrite)
                {
                    this.cOut.WriteVarUInt32((uint)packetID);
                    this.cOut.WriteVarInt32((int)packetData.Length);
                    this.cOut.Write(packetData);
                    this.cOut.Flush();
                }
                else
                {
                    this.forceDisconnect(Direction.Client, "Cannot write to stream.");
                }
            }
            catch (Exception e)
            {
                this.forceDisconnect(Direction.Client, "Failed to send packet: " + e.Message);
            }
        }

        public void sendServerPacket(Packet packetID, byte[] packetData)
        {
            try
            {
                if (this.sOut.BaseStream.CanWrite)
                {
                    this.sOut.WriteVarUInt32((uint)packetID);
                    this.sOut.WriteVarInt32((int)packetData.Length);
                    this.sOut.Write(packetData);
                    this.sOut.Flush();
                }
                else
                {
                    this.forceDisconnect(Direction.Server, "Cannot write to stream.");
                }
            }
            catch (Exception e)
            {
                this.forceDisconnect(Direction.Server, "Failed to send packet: " + e.Message);
            }
        }

        public void sendCommandMessage(string message)
        {
            sendChatMessage(ChatReceiveContext.CommandResult, "", 0, "", message);
        }

        public void sendChatMessage(string message)
        {
            sendChatMessage(ChatReceiveContext.Broadcast, "", 0, "", message);
        }

        public void sendChatMessage(string name, string message)
        {
            sendChatMessage(ChatReceiveContext.Broadcast, "", 0, name, message);
        }

        public void sendChatMessage(ChatReceiveContext context, string name, string message)
        {
            sendChatMessage(context, "", 0, name, message);
        }

        public void sendChatMessage(ChatReceiveContext context, string world, uint clientID, string name, string message)
        {
            if (state != ClientState.Connected) return;
            Packet11ChatSend packet = new Packet11ChatSend(this, Util.Direction.Client);
            packet.prepare(context, world, clientID, name, message);

            this.packetQueue.Add(packet);
        }

        public void banClient(string reason)
        {
            delayDisconnect("You have been banned from the server for " + reason + ".", this.playerData.name + " has been banned from the server for " + reason + "!");
        }

        public void kickClient(string reason)
        {
            delayDisconnect("You have been kicked from the server for " + reason + ".", this.playerData.name + " has been kicked from the server for " + reason + "!");
        }

        public void kickClient()
        {
            delayDisconnect("You have been kicked from the server.", this.playerData.name + " has been kicked from the server!");
        }

        private void doDisconnect(string logMessage)
        {
            if (this.state == ClientState.Disposing) return;
            this.state = ClientState.Disposing;
            StarshipServer.logInfo("[" + playerData.client + "] " + logMessage);
            try
            {
                if (this.playerData.name != null)
                {
                    Client target = StarshipServer.getClient(this.playerData.name);
                    if (target != null)
                    {
                        Users.SaveUser(this.playerData);
                        StarshipServer.removeClient(this);
                        if (this.kickTargetTimestamp == 0) StarshipServer.sendGlobalMessage(this.playerData.name + " has left the server.");
                    }
                }
            }
            catch (Exception e)
            {
                StarshipServer.logException("Failed to remove client from clients: " + e.ToString());
            }
            try
            {
                this.sendServerPacket(Packet.ClientDisconnect, new byte[1]);
            }
            catch (Exception) { }
            try
            {
                this.cSocket.Close();
                this.sSocket.Close();
            }
            catch (Exception) { }
            try
            {
                this.ClientForwarder.Abort();
                this.ServerForwarder.Abort();
            }
            catch (Exception) { }
        }

        public void delayDisconnect(string reason)
        {
            if (kickTargetTimestamp != 0) return;
            sendChatMessage("^#f75d5d;" + reason);
            kickTargetTimestamp = Utils.getTimestamp() + 6;
            sendServerPacket(Packet.ClientDisconnect, new byte[1]);
            StarshipServer.logInfo("[" + playerData.client + "] is being kicked for " + reason);
        }

        public void delayDisconnect(string reason, string message)
        {
            if (kickTargetTimestamp != 0) return;
            sendChatMessage("^#f75d5d;" + reason);
            kickTargetTimestamp = Utils.getTimestamp() + 6;
            sendServerPacket(Packet.ClientDisconnect, new byte[1]);
            StarshipServer.sendGlobalMessage("^#f75d5d;" + message);
            StarshipServer.logInfo("[" + playerData.client + "] is being kicked for " + message);
        }

        public void rejectPreConnected(string reason)
        {
            Packet2ConnectResponse packet = new Packet2ConnectResponse(this, Util.Direction.Client, reason);
            packet.onSend();
            doDisconnect(reason);
        }

        public void forceDisconnect(Direction direction, string reason)
        {
            if (direction == Direction.Server)
            {
                if (state != ClientState.Connected)
                    rejectPreConnected("Connection Failed: " + reason);
                else
                    delayDisconnect("Dropped by parent server for " + reason);
            }
            else
                doDisconnect("Dropped by parent client for " + reason);
        }

        public void closeConnection()
        {
            doDisconnect("has left the server.");
        }
    }
}
