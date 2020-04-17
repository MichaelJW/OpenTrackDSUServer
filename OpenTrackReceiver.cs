﻿using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace OpenTrackToDSUProtocol
{
    public class OpenTrackData
    {
        public double x, y, z;
        public double yaw, pitch, roll;
    }

    public class OpenTrackReceiver
    {
        private bool Running
        {
            get;
            set;
        } = false;

        private Socket _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private DSUServer _dsu_server = null;

        private Thread _packet_receive_thread;
        private bool _waiting_on_packet = false;

        private Thread _process_queue_thread;

        private ConcurrentQueue<OpenTrackData> _queued_items = new ConcurrentQueue<OpenTrackData>();

        private double _last_received_x = 0.0;
        private double _last_received_y = 0.0;
        private double _last_received_z = 0.0;

        private double _last_received_yaw = 0.0;
        private double _last_received_pitch = 0.0;
        private double _last_received_roll = 0.0;

        private DateTime? _last_time = null;
        private long _total_packets = 0;

        public OpenTrackReceiver(string ip, int port)
        {
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.ReuseAddress, true);
            _socket.Bind(new IPEndPoint(IPAddress.Parse(ip), port));

            Console.WriteLine($"OpenTrack receiver listening on ip '{ip}' and port '{port.ToString()}'");
        }

        public OpenTrackReceiver(string ip, int port, DSUServer dsu_server)
        {
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.ReuseAddress, true);
            _socket.Bind(new IPEndPoint(IPAddress.Parse(ip), port));
            _dsu_server = dsu_server;

            Console.WriteLine($"OpenTrack receiver listening on ip '{ip}' and port '{port.ToString()}'");
        }

        public void Start()
        {
            Running = true;
            _queued_items.Clear();

            _packet_receive_thread = new Thread(() =>
            {
                while (Running)
                {
                    ReceiveUDPPacket();
                }
            });
            _packet_receive_thread.Start();

            _process_queue_thread = new Thread(() =>
            {
                if (_dsu_server != null)
                {
                    _dsu_server.Start();
                }
                while (Running)
                {
                    OpenTrackData data;
                    if (_queued_items.TryDequeue(out data))
                    {
                        ReceiveOpenTrackPacket(data);
                    }
                }
                if (_dsu_server != null)
                {
                    _dsu_server.Stop();
                    _dsu_server = null;
                }
            });
            _process_queue_thread.Start();
        }

        public void Stop()
        {
            Running = false;
            _packet_receive_thread.Join();
            _process_queue_thread.Join();
            _socket.Close();
        }

        private void ReceiveUDPPacket()
        {
            if (_waiting_on_packet)
            {
                Thread.Sleep(1);
                return;
            }

            _waiting_on_packet = true;
            byte[] received_bytes = new byte[100];
            EndPoint client_endpoint = new IPEndPoint(IPAddress.Any, 0);
            _socket.BeginReceiveFrom(received_bytes, 0, received_bytes.Length, SocketFlags.None, ref client_endpoint, (ar) =>
            {
                try
                {
                    int message_size = _socket.EndReceiveFrom(ar, ref client_endpoint);

                    if (message_size < 48)
                    {
                        return;
                    }

                    double x = BitConverter.ToDouble(received_bytes, 0);
                    double y = BitConverter.ToDouble(received_bytes, 8);
                    double z = BitConverter.ToDouble(received_bytes, 16);

                    double yaw = BitConverter.ToDouble(received_bytes, 24);
                    double pitch = BitConverter.ToDouble(received_bytes, 32);
                    double roll = BitConverter.ToDouble(received_bytes, 40);
                    _waiting_on_packet = false;

                    if (_dsu_server == null)
                    {
                        // Debug mode, just send the raw open-track data
                        _queued_items.Enqueue(new OpenTrackData { x = x, y = y, z = z, yaw = yaw, pitch = pitch, roll = roll });
                    }
                    else
                    {
                        // Zeros are a special value, if they are sent, the user is either perfectly still
                        // or "stop" has been pressed
                        // in either case, we should send a packet with no motion

                        if (x == 0 && y == 0 && z == 0 && yaw == 0 && pitch == 0 && roll == 0)
                        {
                            _last_received_x = 0;
                            _last_received_y = 0;
                            _last_received_z = 0;

                            _last_received_yaw = 0;
                            _last_received_pitch = 0;
                            _last_received_roll = 0;

                            _total_packets = 0;
                            _last_time = null;

                            _queued_items.Enqueue(new OpenTrackData {});
                        }
                        else
                        {
                            var x_diff = x - _last_received_x;
                            var y_diff = y - _last_received_y;
                            var z_diff = z - _last_received_z;

                            var yaw_diff = yaw - _last_received_yaw;
                            var pitch_diff = pitch - _last_received_pitch;
                            var roll_diff = roll - _last_received_roll;

                            double value_per_second;
                            if (_last_time == null)
                            {
                                value_per_second = 1.0;
                                _last_time = DateTime.UtcNow;
                            }
                            else
                            {
                                var time_diff = DateTime.UtcNow - _last_time.Value;
                                value_per_second = (_total_packets * 1000) / time_diff.TotalMilliseconds;
                            }

                            _total_packets++;

                            yaw_diff *= value_per_second;
                            pitch_diff *= value_per_second;
                            roll_diff *= value_per_second;

                            _queued_items.Enqueue(new OpenTrackData { x = x_diff, y = y_diff, z = z_diff, yaw = yaw_diff, pitch = pitch_diff, roll = roll_diff });

                            _last_received_x = x;
                            _last_received_y = y;
                            _last_received_z = z;

                            _last_received_yaw = yaw;
                            _last_received_pitch = pitch;
                            _last_received_roll = roll;
                        }
                    }
                }
                catch (SocketException)
                {
                    uint IOC_IN = 0x80000000;
                    uint IOC_VENDOR = 0x18000000;
                    uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                    _socket.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);

                    _last_received_x = 0;
                    _last_received_y = 0;
                    _last_received_z = 0;

                    _last_received_yaw = 0;
                    _last_received_pitch = 0;
                    _last_received_roll = 0;
                }

            }, null);
        }

        private void ReceiveOpenTrackPacket(OpenTrackData data)
        {
            if (_dsu_server == null)
            {
                Console.WriteLine($"OpenTrack debugging...Position: [{data.x}, {data.y}, {data.z}], Rotation: [{data.yaw}°, {data.pitch}°, {data.roll}°]");
            }
            else
            {
                _dsu_server.SendOpenTrackData(data);
            }
        }
    }
}