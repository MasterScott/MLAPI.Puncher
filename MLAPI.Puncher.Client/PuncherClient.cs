﻿using System;
using System.Net;
using System.Threading;
using MLAPI.Puncher.Shared;

namespace MLAPI.Puncher.Client
{
    /// <summary>
    /// A puncher client capable of pucnhing and being punched.
    /// </summary>
    public class PuncherClient : IDisposable
    {
        /// <summary>
        /// Gets or sets the transport used to communicate with puncher server.
        /// </summary>
        /// <value>The transport used to communcate with puncher server.</value>
        public IUDPTransport Transport { get; set; } = new SlimUDPTransport();
        /// <summary>
        /// Gets or sets the amount port predictions to attempt.
        /// </summary>
        /// <value>The amount of port predictions to attempt.</value>
        public int PortPredictions { get; set; } = 8;
        /// <summary>
        /// Gets or sets the amount of punch attempts.
        /// </summary>
        /// <value>The amount of punch attempts.</value>
        public int MaxPunchAttempts { get; set; } = 8;
        /// <summary>
        /// Gets or sets the retry delay in milliseconds.
        /// </summary>
        /// <value>The retry delay in milliseconds.</value>
        public int RetryDelay { get; set; } = 1000;
        /// <summary>
        /// Gets or sets the max response wait time in milliseconds.
        /// </summary>
        /// <value>The max response wait time in milliseconds.</value>
        public int MaxResponseWaitTime { get; set; } = 5000;
        /// <summary>
        /// Gets or sets the max server response attempts (Connector only).
        /// </summary>
        /// <value>The max server response attempts.</value>
        public int MaxServerResponseAttempts { get; set; } = 20;

        private readonly IPEndPoint _puncherServerEndpoint;
        private bool _isRunning = false;

        // Buffers
        private readonly byte[] _buffer = new byte[64];
        private readonly byte[] _tokenBuffer = new byte[64];

        /// <summary>
        /// Initializes a new instance of the <see cref="T:MLAPI.Puncher.Client.PuncherClient"/> class with a specified server endpoint.
        /// </summary>
        /// <param name="puncherServerEndpoint">Puncher server endpoint.</param>
        public PuncherClient(IPEndPoint puncherServerEndpoint)
        {
            _puncherServerEndpoint = puncherServerEndpoint;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:MLAPI.Puncher.Client.PuncherClient"/> class with a specified server host and port.
        /// </summary>
        /// <param name="puncherServerHost">Puncher server host.</param>
        /// <param name="puncherServerPort">Puncher server port.</param>
        public PuncherClient(string puncherServerHost, ushort puncherServerPort)
        {
            _puncherServerEndpoint = new IPEndPoint(Dns.GetHostEntry(puncherServerHost).AddressList[0], puncherServerPort);
        }

        /// <summary>
        /// Listens for incoming punch requests.
        /// </summary>
        /// <param name="listenEndpoint">The endpoint where new players should join.</param>
        public void ListenForPunches(IPEndPoint listenEndpoint)
        {
            // Bind the socket
            Transport.Bind(listenEndpoint);

            _isRunning = true;

            // Register with NAT server
            SendRegister(null, null);

            // Punch
            ExecutePunch(false, false, null);

            _isRunning = false;
        }

        /// <summary>
        /// Listens for a single punch and returns when the punch is successful.
        /// </summary>
        /// <returns>The address of the connector that punched through our NAT.</returns>
        /// <param name="listenEndpoint">The endpoint where new players should join.</param>
        public IPEndPoint ListenForSinglePunch(IPEndPoint listenEndpoint)
        {
            // Bind the socket
            Transport.Bind(listenEndpoint);

            _isRunning = true;

            // Register with NAT server
            SendRegister(null, null);

            // Punch
            IPEndPoint endpoint = ExecutePunch(false, true, null);

            _isRunning = false;

            return endpoint;
        }

        /// <summary>
        /// Starts punching the requested peer.
        /// </summary>
        /// <returns>The remote peer connectable address.</returns>
        /// <param name="connectAddress">The peer connect address.</param>
        public IPEndPoint Punch(IPAddress connectAddress)
        {
            // Bind the socket
            Transport.Bind(new IPEndPoint(IPAddress.Any, 0));

            _isRunning = true;

            byte[] token = new byte[32];
            Random rnd = new Random();
            rnd.NextBytes(token);

            // Register with NAT server
            SendRegister(connectAddress, token);

            // Punch
            IPEndPoint endpoint = ExecutePunch(true, true, token);

            _isRunning = false;

            return endpoint;
        }

        private bool SendRegister(IPAddress connectAddress, byte[] token)
        {
            // Prevent info leaks
            Array.Clear(_buffer, 0, _buffer.Length);

            // Write message type
            _buffer[0] = (byte)MessageType.Register;


            // Flag byte (1 = (isConnector true && isListener false))
            //           (2 = (isConnector false && isListener true))

            bool isConnector = connectAddress != null;

            if (isConnector)
            {
                // Set flag byte
                _buffer[1] = 1;

                // Write target address
                byte[] addressBytes = connectAddress.GetAddressBytes();

                // Write IPv4 Address
                Buffer.BlockCopy(addressBytes, 0, _buffer, 2, 4);

                // Calculate token lenght. Max is 32
                byte tokenLength = (byte)Math.Min(token.Length, 32);

                // Write token length
                _buffer[6] = tokenLength;

                // Write the token
                Buffer.BlockCopy(token, 0, _buffer, 7, token.Length);
            }
            else
            {
                // Set flag byte
                _buffer[1] = 2;
            }

            // Send register
            int size = Transport.SendTo(_buffer, 0, _buffer.Length, 5000, _puncherServerEndpoint);

            return size == _buffer.Length;
        }

        private IPEndPoint ExecutePunch(bool isConnector, bool exitOnPunchSuccess, byte[] token)
        {
            int attempts = 0;

            while ((!isConnector || MaxServerResponseAttempts > attempts) && _isRunning)
            {
                if (attempts > 0)
                    attempts++;

                // Safety cleanup
                Array.Clear(_buffer, 0, _buffer.Length);

                // Receive connectTo
                int size = Transport.ReceiveFrom(_buffer, 0, _buffer.Length, 5000, out IPEndPoint remoteEndPoint);

                // Safety
                if (size == _buffer.Length)
                {
                    // Process connectTo
                    MessageType messageType = (MessageType)_buffer[0];

                    if (messageType == MessageType.ConnectTo && remoteEndPoint.Equals(_puncherServerEndpoint))
                    {
                        // Read incoming target address, port, token length and token
                        IPAddress connectToAddress = new IPAddress(new byte[4] { _buffer[1], _buffer[2], _buffer[3], _buffer[4] });
                        ushort port = (ushort)((ushort)_buffer[5] | (ushort)_buffer[6] << 8);
                        byte tokenSize = _buffer[7];

                        if (tokenSize > _buffer.Length - 6)
                        {
                            // Invalid token size
                            continue;
                        }

                        // Copy token
                        Buffer.BlockCopy(_buffer, 8, _tokenBuffer, 0, tokenSize);

                        // Clear incoming data
                        Array.Clear(_buffer, 0, _buffer.Length);

                        // Validate the request is correct if connector.
                        if (isConnector)
                        {
                            bool correct = true;

                            for (int i = 0; i < tokenSize; i++)
                            {
                                if (_tokenBuffer[i] != token[i])
                                {
                                    correct = false;
                                    break;
                                }
                            }

                            if (!correct)
                            {
                                // The token was incorrect. Dont return yet.
                                // Instead go for further iterations to ensure that there are no clogged messages
                                continue;
                            }
                        }

                        // Write punch
                        _buffer[0] = (byte)MessageType.Punch;

                        // Write token length
                        _buffer[1] = tokenSize;

                        // Write token
                        Buffer.BlockCopy(_tokenBuffer, 0, _buffer, 2, tokenSize);

                        for (int i = 0; i < MaxPunchAttempts; i++)
                        {
                            for (int x = 0; x < PortPredictions; x++)
                            {
                                // Send all punches
                                Transport.SendTo(_buffer, 0, _buffer.Length, 5000, new IPEndPoint(connectToAddress, port + x));
                            }

                            // Safety and validation
                            if (isConnector)
                            {
                                DateTime receiveStart = DateTime.Now;

                                do
                                {
                                    // Receive punch success
                                    size = Transport.ReceiveFrom(_buffer, 0, _buffer.Length, 1000, out remoteEndPoint);

                                    // Santy checks
                                    if (size == _buffer.Length && remoteEndPoint != null && remoteEndPoint.Address.Equals(connectToAddress))
                                    {
                                        if ((MessageType)_buffer[0] == MessageType.Punch)
                                        {
                                            // We got the listeners punch. If they punch us from a port we have not yet punched. We want to punch their new port.
                                            // This improves symmetric NAT succeess rates.

                                            // Make sure token size is the same
                                            if (_buffer[1] == tokenSize)
                                            {
                                                bool correct = true;

                                                for (int x = 0; x < tokenSize; x++)
                                                {
                                                    if (_buffer[2 + x] != _tokenBuffer[x])
                                                    {
                                                        correct = false;
                                                        break;
                                                    }
                                                }

                                                if (correct)
                                                {
                                                    // Token was correct. 

                                                    // If the port we got the punch on is a port we have not yet punched. Use the new port to send new punches (Improves symmetric success)
                                                    bool hasPingedPort = false;
                                                    for (int x = 0; x < PortPredictions; x++)
                                                    {
                                                        if (port + x == remoteEndPoint.Port)
                                                        {
                                                            hasPingedPort = true;
                                                            break;
                                                        }
                                                    }

                                                    if (!hasPingedPort)
                                                    {
                                                        // They got a totally new port that we have not seen before.
                                                        // Lets punch it. We dont need to port predict these new punches
                                                        Transport.SendTo(_buffer, 0, _buffer.Length, 5000, new IPEndPoint(connectToAddress, remoteEndPoint.Port));
                                                    }
                                                }
                                            }
                                        }
                                        else if (((MessageType)_buffer[0]) == MessageType.PunchSuccess)
                                        {
                                            // We got a punch success.

                                            // Make sure token size is the same
                                            if (_buffer[1] == tokenSize)
                                            {
                                                bool correct = true;

                                                for (int x = 0; x < tokenSize; x++)
                                                {
                                                    if (_buffer[2 + x] != _tokenBuffer[x])
                                                    {
                                                        correct = false;
                                                        break;
                                                    }
                                                }

                                                if (correct)
                                                {
                                                    // Success
                                                    return remoteEndPoint;
                                                }
                                            }
                                        }
                                    }

                                } while (isConnector && (DateTime.Now - receiveStart).TotalMilliseconds < MaxResponseWaitTime);
                            }

                            // If another attempt is comming up. Wait for the delay
                            if (MaxPunchAttempts > 0 && i != MaxPunchAttempts - 1 && RetryDelay > 0)
                            {
                                // Sleep for delay
                                Thread.Sleep(RetryDelay);
                            }
                        }
                    }
                    else if (messageType == MessageType.Error && isConnector && remoteEndPoint.Equals(_puncherServerEndpoint))
                    {
                        ErrorType error = (ErrorType)_buffer[1];

                        if (error == ErrorType.ClientNotFound)
                        {
                            // Client was not found on the server. No point continuing.
                            return null;
                        }
                    }
                    else if (messageType == MessageType.Punch && !isConnector)
                    {
                        // Change message type, leave the body the same (token length and token)
                        _buffer[0] = (byte)MessageType.PunchSuccess;

                        // Send punch success
                        Transport.SendTo(_buffer, 0, _buffer.Length, 5000, remoteEndPoint);

                        if (exitOnPunchSuccess)
                        {
                            // Return the connector address that punched through our NAT
                            return remoteEndPoint;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Releases all resource used by the <see cref="T:MLAPI.Puncher.Client.PuncherClient"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the
        /// <see cref="T:MLAPI.Puncher.Client.PuncherClient"/>. The <see cref="Dispose"/> method leaves the
        /// <see cref="T:MLAPI.Puncher.Client.PuncherClient"/> in an unusable state. After calling
        /// <see cref="Dispose"/>, you must release all references to the
        /// <see cref="T:MLAPI.Puncher.Client.PuncherClient"/> so the garbage collector can reclaim the memory that the
        /// <see cref="T:MLAPI.Puncher.Client.PuncherClient"/> was occupying.</remarks>
        public void Dispose()
        {
            _isRunning = false;
            Transport.Close();
        }
    }
}
