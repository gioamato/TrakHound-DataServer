﻿// Copyright (c) 2017 TrakHound Inc., All Rights Reserved.

// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using TrakHound.Api.v2;

namespace TrakHound.DataServer
{
    /// <summary>
    /// Handles streams for new connections and adds received JSON data to SQL queue
    /// </summary>
    class StreamConnection
    {
        private Stream stream = null;
        private ManualResetEvent stop;

        private TcpClient _client;
        /// <summary>
        /// The TcpClient Connection used for streaming data
        /// </summary>
        public TcpClient Client { get { return _client; } }

        /// <summary>
        /// Flag whether SSL is used for client connections. Read Only.
        /// </summary>
        public bool UseSSL { get { return _sslCertificate != null; } }

        /// <summary>
        /// Connection Timeout in Milliseconds
        /// </summary>
        public int Timeout { get; set; }

        private X509Certificate2 _sslCertificate;
        /// <summary>
        /// The SSL Certificate to use for client connections
        /// </summary>
        public X509Certificate2 SslCertificate { get { return _sslCertificate; } }

        public StreamConnection(ref TcpClient client, X509Certificate2 sslCertificate)
        {
            _client = client;
            _sslCertificate = sslCertificate;
        }

        public void Start()
        {
            stop = new ManualResetEvent(false);

            GetStream();
            if (stream == null) Log.Write("No Stream Found", this);
            else
            {
                try
                {
                    int i;
                    var bytes = new byte[1048576]; // 1 MB
                    string s = "";

                    // Create & Start Timeout timer
                    var timeoutTimer = new System.Timers.Timer();
                    timeoutTimer.Interval = Timeout;
                    timeoutTimer.Elapsed += TimeoutTimer_Elapsed;
                    timeoutTimer.Enabled = true;

                    // Loop to receive all the data sent by the client.
                    while ((i = stream.Read(bytes, 0, bytes.Length)) != 0 && !stop.WaitOne(0, true))
                    {
                        // Reset Timeout timer
                        timeoutTimer.Stop();
                        timeoutTimer.Start();

                        var d = Encoding.ASCII.GetString(bytes, 0, i);

                        // Add new string to stored string
                        s += d;

                        int b = -1;
                        int e = -1;

                        // Search for JSON Array Brackets
                        b = s.IndexOf("[");
                        if (b >= 0) e = s.IndexOf("]", b);

                        while (b >= 0 && e >= 0)
                        {
                            e++; // Include closing bracket
                            var json = s.Substring(b, e - b);
                            s = s.Remove(b, e - b);

                            // Convert to Json and add to SqlQueue
                            var samples = Requests.FromJson<List<Samples.Sample>>(json);
                            if (samples != null) DataServer.SqlQueue.Add(samples);

                            b = s.IndexOf("[");
                            if (b >= 0) e = s.IndexOf("]", b);
                            else e = -1;
                        }

                        // Send Success Byte
                        var p = new byte[] { 101 };
                        stream.Write(p, 0, p.Length);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write(ex.Message, this);
                }
                finally
                {
                    if (stream != null) stream.Close();
                    Log.Write("Stream Closed", this);
                }
            }
        }

        public void Stop()
        {
            stop.Set();
            if (stream != null) stream.Close();
            if (_client != null) _client.Close();
        }

        private void GetStream()
        {
            try
            {
                if (UseSSL)
                {
                    // Create new SSL Stream from client's NetworkStream
                    var sslStream = new SslStream(_client.GetStream(), false);
                    sslStream.AuthenticateAsServer(_sslCertificate, false, System.Security.Authentication.SslProtocols.Default, true);
                    stream = sslStream;
                }
                else
                {
                    stream = _client.GetStream();
                }
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                Log.Write("Authentication failed - closing the connection.", this);
            }
            catch (Exception ex)
            {
                Log.Write(ex.Message, this);
            }
        }

        private void TimeoutTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            var timer = (System.Timers.Timer)sender;
            timer.Enabled = false;

            Stop();
        }
    }
}
