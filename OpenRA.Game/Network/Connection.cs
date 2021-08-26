#region Copyright & License Information
/*
 * Copyright 2007-2021 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenRA.Server;

namespace OpenRA.Network
{
	public enum ConnectionState
	{
		PreConnecting,
		NotConnected,
		Connecting,
		Connected,
	}

	public interface IConnection : IDisposable
	{
		int LocalClientId { get; }
		void Send(int frame, IEnumerable<Order> orders);
		void SendImmediate(IEnumerable<Order> orders);
		void SendSync(int frame, int syncHash, ulong defeatState);
		void Receive(OrderManager orderManager);
	}

	public class EchoConnection : IConnection
	{
		protected struct ReceivedPacket
		{
			public int FromClient;
			public byte[] Data;
		}

		readonly ConcurrentQueue<ReceivedPacket> receivedPackets = new ConcurrentQueue<ReceivedPacket>();
		public ReplayRecorder Recorder { get; private set; }

		public virtual int LocalClientId => 1;

		public virtual void Send(int frame, IEnumerable<Order> orders)
		{
			var ms = new MemoryStream();
			ms.WriteArray(BitConverter.GetBytes(frame));
			foreach (var o in orders)
				ms.WriteArray(o.Serialize());
			Send(ms.ToArray());
		}

		public virtual void SendImmediate(IEnumerable<Order> orders)
		{
			foreach (var o in orders)
			{
				var ms = new MemoryStream();
				ms.WriteArray(BitConverter.GetBytes(0));
				ms.WriteArray(o.Serialize());
				Send(ms.ToArray());
			}
		}

		public virtual void SendSync(int frame, int syncHash, ulong defeatState)
		{
			Send(OrderIO.SerializeSync(frame, syncHash, defeatState));
		}

		protected virtual void Send(byte[] packet)
		{
			if (packet.Length == 0)
				throw new NotImplementedException();
			AddPacket(new ReceivedPacket { FromClient = LocalClientId, Data = packet });
		}

		protected void AddPacket(ReceivedPacket packet)
		{
			receivedPackets.Enqueue(packet);
		}

		public virtual void Receive(OrderManager orderManager)
		{
			while (receivedPackets.TryDequeue(out var p))
			{
				if (OrderIO.TryParseDisconnect(p.Data, out var disconnectClient))
					orderManager.ReceiveDisconnect(disconnectClient);
				else if (OrderIO.TryParseSync(p.Data, out var syncFrame, out var syncHash, out var defeatState))
					orderManager.ReceiveSync(syncFrame, syncHash, defeatState);
				else if (OrderIO.TryParseOrderPacket(p.Data, out var ordersFrame, out var orders))
				{
					if (ordersFrame == 0)
						orderManager.ReceiveImmediateOrders(p.FromClient, orders);
					else
						orderManager.ReceiveOrders(p.FromClient, ordersFrame, orders);
				}

				Recorder?.Receive(p.FromClient, p.Data);
			}
		}

		public void StartRecording(Func<string> chooseFilename)
		{
			// If we have a previous recording then save/dispose it and start a new one.
			Recorder?.Dispose();
			Recorder = new ReplayRecorder(chooseFilename);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
				Recorder?.Dispose();
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}
	}

	public sealed class NetworkConnection : EchoConnection
	{
		public readonly ConnectionTarget Target;
		TcpClient tcp;
		IPEndPoint endpoint;
		readonly List<byte[]> queuedSyncPackets = new List<byte[]>();
		volatile ConnectionState connectionState = ConnectionState.Connecting;
		volatile int clientId;
		bool disposed;
		string errorMessage;

		public IPEndPoint EndPoint => endpoint;

		public string ErrorMessage => errorMessage;

		public NetworkConnection(ConnectionTarget target)
		{
			Target = target;
			new Thread(NetworkConnectionConnect)
			{
				Name = $"{GetType().Name} (connect to {target})",
				IsBackground = true
			}.Start();
		}

		void NetworkConnectionConnect()
		{
			var queue = new BlockingCollection<TcpClient>();

			var atLeastOneEndpoint = false;
			foreach (var endpoint in Target.GetConnectEndPoints())
			{
				atLeastOneEndpoint = true;
				new Thread(() =>
				{
					try
					{
						var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
						client.Connect(endpoint.Address, endpoint.Port);

						try
						{
							queue.Add(client);
						}
						catch (InvalidOperationException)
						{
							// Another connection was faster, close this one.
							client.Close();
						}
					}
					catch (Exception ex)
					{
						errorMessage = "Failed to connect";
						Log.Write("client", $"Failed to connect to {endpoint}: {ex.Message}");
					}
				})
				{
					Name = $"{GetType().Name} (connect to {endpoint})",
					IsBackground = true
				}.Start();
			}

			if (!atLeastOneEndpoint)
			{
				errorMessage = "Failed to resolve address";
				connectionState = ConnectionState.NotConnected;
			}

			// Wait up to 5s for a successful connection. This should hopefully be enough because such high latency makes the game unplayable anyway.
			else if (queue.TryTake(out tcp, 5000))
			{
				// Copy endpoint here to have it even after getting disconnected.
				endpoint = (IPEndPoint)tcp.Client.RemoteEndPoint;

				new Thread(NetworkConnectionReceive)
				{
					Name = $"{GetType().Name} (receive from {tcp.Client.RemoteEndPoint})",
					IsBackground = true
				}.Start();
			}
			else
			{
				connectionState = ConnectionState.NotConnected;
			}

			// Close all unneeded connections in the queue and make sure new ones are closed on the connect thread.
			queue.CompleteAdding();
			foreach (var client in queue)
				client.Close();
		}

		void NetworkConnectionReceive()
		{
			try
			{
				var stream = tcp.GetStream();
				var handshakeProtocol = stream.ReadInt32();

				if (handshakeProtocol != ProtocolVersion.Handshake)
					throw new InvalidOperationException($"Handshake protocol version mismatch. Server={handshakeProtocol} Client={ProtocolVersion.Handshake}");

				clientId = stream.ReadInt32();
				connectionState = ConnectionState.Connected;

				while (true)
				{
					var len = stream.ReadInt32();
					var client = stream.ReadInt32();
					var buf = stream.ReadBytes(len);
					if (len == 0)
						throw new NotImplementedException();
					AddPacket(new ReceivedPacket { FromClient = client, Data = buf });
				}
			}
			catch (Exception ex)
			{
				errorMessage = "Connection failed";
				Log.Write("client", $"Connection to {endpoint} failed: {ex.Message}");
			}
			finally
			{
				connectionState = ConnectionState.NotConnected;
			}
		}

		public override int LocalClientId => clientId;
		public ConnectionState ConnectionState => connectionState;

		public override void SendSync(int frame, int syncHash, ulong defeatState)
		{
			queuedSyncPackets.Add(OrderIO.SerializeSync(frame, syncHash, defeatState));
		}

		protected override void Send(byte[] packet)
		{
			base.Send(packet);

			try
			{
				var ms = new MemoryStream();
				ms.WriteArray(BitConverter.GetBytes(packet.Length));
				ms.WriteArray(packet);

				foreach (var q in queuedSyncPackets)
				{
					ms.WriteArray(BitConverter.GetBytes(q.Length));
					ms.WriteArray(q);
					base.Send(q);
				}

				queuedSyncPackets.Clear();
				ms.WriteTo(tcp.GetStream());
			}
			catch (SocketException) { /* drop this on the floor; we'll pick up the disconnect from the reader thread */ }
			catch (ObjectDisposedException) { /* ditto */ }
			catch (InvalidOperationException) { /* ditto */ }
			catch (IOException) { /* ditto */ }
		}

		protected override void Dispose(bool disposing)
		{
			if (disposed)
				return;
			disposed = true;

			// Closing the stream will cause any reads on the receiving thread to throw.
			// This will mark the connection as no longer connected and the thread will terminate cleanly.
			tcp?.Close();

			base.Dispose(disposing);
		}
	}
}
