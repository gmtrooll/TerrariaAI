using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>One request from a bridge client. Reply or Fail exactly once, from any thread.</summary>
    public sealed class BridgeRequest
    {
        public JToken Id;
        public string Cmd;
        public JObject Args;
        internal BridgeConnection Connection;
        private int replied;

        public void Reply(object result) {
            Send(new JObject { ["id"] = Id, ["ok"] = true, ["result"] = JsonOut.ToToken(result) });
        }

        public void Fail(string error) {
            Send(new JObject { ["id"] = Id, ["ok"] = false, ["error"] = error });
        }

        private void Send(JObject message) {
            if (Interlocked.Exchange(ref replied, 1) == 0) Connection.Send(message);
        }
    }

    internal sealed class BridgeConnection
    {
        private readonly StreamWriter writer;
        private readonly object writeLock = new object();

        public BridgeConnection(TcpClient client) {
            writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public void Send(JObject message) {
            string line = message.ToString(Formatting.None);
            lock (writeLock) {
                try {
                    writer.WriteLine(line);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>
    /// Line-delimited JSON over a local TCP port: {"id": 1, "cmd": "status", "args": {...}} in,
    /// {"id": 1, "ok": true, "result": ...} or {"id": 1, "ok": false, "error": "..."} out.
    /// Network threads only parse and queue; BridgeSystem runs the commands on the game thread between ticks.
    /// </summary>
    public static class BridgeServer
    {
        public static readonly ConcurrentQueue<BridgeRequest> Pending = new ConcurrentQueue<BridgeRequest>();

        private static TcpListener listener;
        private static volatile bool running;

        public static void Start(int port) {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "AutomationBridge" }.Start();
        }

        public static void Stop() {
            running = false;
            try {
                listener?.Stop();
            }
            catch (SocketException) { }
            listener = null;
        }

        private static void AcceptLoop() {
            while (running) {
                TcpClient client;
                try {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception) {
                    if (!running) return;
                    continue;
                }
                client.NoDelay = true;
                new Thread(() => ReadLoop(client)) { IsBackground = true, Name = "AutomationBridgeClient" }.Start();
            }
        }

        private static void ReadLoop(TcpClient client) {
            var connection = new BridgeConnection(client);
            try {
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                string line;
                while (running && (line = reader.ReadLine()) != null) {
                    if (line.Length == 0) continue;
                    JObject message;
                    try {
                        message = JObject.Parse(line);
                    }
                    catch (JsonException e) {
                        connection.Send(new JObject { ["id"] = null, ["ok"] = false, ["error"] = "bad request: " + e.Message });
                        continue;
                    }
                    Pending.Enqueue(new BridgeRequest {
                        Id = message["id"],
                        Cmd = (string)message["cmd"] ?? "",
                        Args = message["args"] as JObject ?? new JObject(),
                        Connection = connection,
                    });
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally {
                client.Close();
            }
        }
    }
}
