﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dache.CacheHost.Communication
{
    /// <summary>
    /// Makes sockets rock a lot harder than they do out-of-the-box. Easy, extremely efficient, scalable and fast methods for intuitive client-server communication.
    /// This class can operate as a client or server.
    /// </summary>
    public sealed class SocketRocker : IDisposable
    {
        // The function that creates a new socket
        private readonly Func<Socket> _socketFunc = null;
        // The currently used socket
        private Socket _socket = null;
        // The message buffer size to use for send/receive
        private readonly int _messageBufferSize = 0;
        // The maximum connections to allow to use the socket simultaneously
        private readonly int _maximumConnections = 0;
        // The semaphore that enforces the maximum numbers of simultaneous connections
        private readonly Semaphore _maxConnectionsSemaphore;

        // The receive buffer queue
        private readonly BlockingQueue<KeyValuePair<byte[], int>> _receiveBufferQueue = null;

        // The function that handles received messages
        private Action<ReceivedMessage> _receivedMessageFunc = null;
        // The number of currently connected clients
        private int _currentlyConnectedClients = 0;
        // Whether or not a connection currently exists
        private volatile bool _isDoingSomething = false;
        // Whether or not to use the multiplexer
        private bool _useClientMultiplexer = false;

        // The client multiplexer
        private readonly Dictionary<int, MultiplexerData> _clientMultiplexer = null;
        // The client multiplexer reader writer lock
        private readonly ReaderWriterLockSlim _clientMultiplexerLock = new ReaderWriterLockSlim();
        // The pool of manual reset events
        private readonly Pool<ManualResetEvent> _manualResetEventPool = null;
        // The pool of message states
        private readonly Pool<MessageState> _messageStatePool = null;
        // The pool of buffers
        private readonly Pool<byte[]> _bufferPool = null;
        // The pool of receive messages
        private readonly Pool<ReceivedMessage> _receiveMessagePool = null;

        // The control bytes placeholder - the first 4 bytes are little endian message length, the last 4 are thread id
        private static readonly byte[] _controlBytesPlaceholder = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="socketFunc">The function that creates a new socket. Use this to specify your socket constructor and initialize settings.</param>
        /// <param name="messageBufferSize">The message buffer size to use for send/receive.</param>
        /// <param name="maximumConnections">The maximum connections to allow to use the socket simultaneously.</param>
        public SocketRocker(Func<Socket> socketFunc, int messageBufferSize, int maximumConnections)
        {
            // Sanitize
            if (socketFunc == null)
            {
                throw new ArgumentNullException("socketFunc");
            }
            if (messageBufferSize < 256)
            {
                throw new ArgumentException("must be >= 256", "messageBufferSize");
            }
            if (maximumConnections <= 0)
            {
                throw new ArgumentException("must be > 0", "maximumConnections");
            }

            _socketFunc = socketFunc;
            _messageBufferSize = messageBufferSize;
            _maximumConnections = maximumConnections;
            _maxConnectionsSemaphore = new Semaphore(maximumConnections, maximumConnections);

            _receiveBufferQueue = new BlockingQueue<KeyValuePair<byte[], int>>(maximumConnections * 10);

            // Initialize the client multiplexer
            _clientMultiplexer = new Dictionary<int, MultiplexerData>(maximumConnections);

            // Create the pools
            _messageStatePool = new Pool<MessageState>(maximumConnections, () => new MessageState(), messageState =>
            {
                if (messageState.Data != null)
                {
                    messageState.Data.Dispose();
                }
                messageState.Buffer = null;
                messageState.Handler = null;
                messageState.ThreadId = -1;
                messageState.TotalBytesToRead = -1;
            });
            _manualResetEventPool = new Pool<ManualResetEvent>(maximumConnections, () => new ManualResetEvent(false), manualResetEvent => manualResetEvent.Reset());
            _bufferPool = new Pool<byte[]>(maximumConnections * 10, () => new byte[messageBufferSize], null);
            _receiveMessagePool = new Pool<ReceivedMessage>(maximumConnections, () => new ReceivedMessage(), receivedMessage =>
            {
                receivedMessage.Message = null;
                receivedMessage.Socket = null;
            });

            // Populate the pools
            for (int i = 0; i < maximumConnections; i++)
            {
                _messageStatePool.Push(new MessageState());
                _manualResetEventPool.Push(new ManualResetEvent(false));
                _bufferPool.Push(new byte[messageBufferSize]);
                _receiveMessagePool.Push(new ReceivedMessage());
            }
        }

        /// <summary>
        /// Gets the currently connected client count.
        /// </summary>
        public int CurrentlyConnectedClients
        {
            get
            {
                return _currentlyConnectedClients;
            }
        }

        /// <summary>
        /// Connects to an endpoint. Once this is called, you must call Close before calling Connect or Listen again.
        /// </summary>
        /// <param name="endPoint">The endpoint.</param>
        public void Connect(EndPoint endPoint)
        {
            if (_isDoingSomething)
            {
                throw new InvalidOperationException("socket is already in use");
            }

            _isDoingSomething = true;
            _useClientMultiplexer = true;

            // Create socket
            _socket = _socketFunc();
            // Set appropriate Nagle
            //_socket.NoDelay = !_useNagleAlgorithm;

            // Post a connect to the socket synchronously
            _socket.Connect(endPoint);

            // Get a message state from the pool
            var messageState = _messageStatePool.Pop();
            messageState.Data = new MemoryStream();
            messageState.Handler = _socket;
            // Get a buffer from the buffer pool
            var buffer = _bufferPool.Pop();

            // Post a receive to the socket as the client will be continuously receiving messages to be pushed to the queue
            _socket.BeginReceive(buffer, 0, buffer.Length, 0, ReceiveCallback, new KeyValuePair<MessageState, byte[]>(messageState, buffer));

            // Process all incoming messages
            Task.Factory.StartNew(() => ProcessReceivedMessage(messageState));
        }

        /// <summary>
        /// Begin listening for incoming connections. Once this is called, you must call Close before calling Connect or Listen again.
        /// </summary>
        /// <param name="localEndpoint">The local endpoint to listen on.</param>
        /// <param name="receivedMessageFunc">The method that handles received messages.</param>
        public void Listen(EndPoint localEndpoint, Action<ReceivedMessage> receivedMessageFunc)
        {
            if (_isDoingSomething)
            {
                throw new InvalidOperationException("socket is already in use");
            }

            _isDoingSomething = true;

            // Set up the function that handles received messages
            _receivedMessageFunc = receivedMessageFunc;

            // Create socket
            _socket = _socketFunc();

            _socket.Bind(localEndpoint);
            _socket.Listen(_maximumConnections);

            // Post accept on the listening socket
            _socket.BeginAccept(AcceptCallback, null);
        }

        /// <summary>
        /// Closes the connection. Once this is called, you can call Connect or Listen again to start a new socket connection.
        /// </summary>
        public void Close()
        {
            // Close the socket
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore
            }

            _socket.Close();
            // No longer doing something
            _isDoingSomething = false;
        }

        // TODO: implement
        private void HandleError(Socket socket)
        {
            // Close the socket
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore
            }

            socket.Close();

            // Release one from the semaphore
            _maxConnectionsSemaphore.Release();

            // Decrement the counter keeping track of the total number of clients connected to the server
            Interlocked.Decrement(ref _currentlyConnectedClients);
        }

        private void AcceptCallback(IAsyncResult asyncResult)
        {
            Interlocked.Increment(ref _currentlyConnectedClients);

            // Get the client handler socket
            Socket handler = _socket.EndAccept(asyncResult);
            //handler.NoDelay = !_useNagleAlgorithm;

            // Post accept on the listening socket
            _socket.BeginAccept(AcceptCallback, null);

            // Do not proceed until we have room to do so
            _maxConnectionsSemaphore.WaitOne();

            // Get message state
            var messageState = _messageStatePool.Pop();
            messageState.Data = new MemoryStream();
            messageState.Handler = handler;

            // Post receive on the handler socket
            var buffer = _bufferPool.Pop();
            handler.BeginReceive(buffer, 0, buffer.Length, 0, ReceiveCallback, new KeyValuePair<MessageState, byte[]>(messageState, buffer));

            // Process all incoming messages
            ProcessReceivedMessage(messageState);
        }

        private void ReceiveCallback(IAsyncResult asyncResult)
        {
            // Get the message state and buffer
            var messageStateAndBuffer = (KeyValuePair<MessageState, byte[]>)asyncResult.AsyncState;
            MessageState messageState = messageStateAndBuffer.Key;
            byte[] buffer = messageStateAndBuffer.Value;
            // TODO: check error code

            // Read the data
            int bytesRead = messageState.Handler.EndReceive(asyncResult);

            if (bytesRead > 0)
            {
                // Add buffer to queue
                _receiveBufferQueue.Enqueue(new KeyValuePair<byte[], int>(buffer, bytesRead));
                
                // Post receive on the handler socket
                buffer = _bufferPool.Pop();
                messageState.Handler.BeginReceive(buffer, 0, buffer.Length, 0, ReceiveCallback, new KeyValuePair<MessageState, byte[]>(messageState, buffer));
            }
        }

        private void ProcessReceivedMessage(MessageState messageState)
        {
            int currentOffset = 0;
            int bytesRead = 0;

            while (_isDoingSomething)
            {
                // Check if we need a buffer
                if (messageState.Buffer == null)
                {
                    // Get the next buffer
                    var receiveBufferEntry = _receiveBufferQueue.Dequeue();
                    messageState.Buffer = receiveBufferEntry.Key;
                    currentOffset = 0;
                    bytesRead = receiveBufferEntry.Value;
                }

                // Check if we need to get our control byte values
                if (messageState.TotalBytesToRead == -1)
                {
                    // We do, see if we have enough bytes received to get them
                    if (currentOffset + _controlBytesPlaceholder.Length > currentOffset + bytesRead)
                    {
                        // We don't yet have enough bytes to read the control bytes, so get more bytes
                        
                        // TODO: loop until we have all data

                        // Combine the buffers
                        var nextBufferEntry = _receiveBufferQueue.Dequeue();
                        var combinedBuffer = new byte[bytesRead + nextBufferEntry.Value];
                        Buffer.BlockCopy(messageState.Buffer, currentOffset, combinedBuffer, 0, bytesRead);
                        Buffer.BlockCopy(nextBufferEntry.Key, 0, combinedBuffer, bytesRead, nextBufferEntry.Value);
                        // Set the new combined buffer and appropriate bytes read
                        messageState.Buffer = combinedBuffer;
                        // Reset bytes read and current offset
                        currentOffset = 0;
                        bytesRead = combinedBuffer.Length;
                    }

                    // Parse out control bytes
                    ExtractControlBytes(messageState.Buffer, currentOffset, out messageState.TotalBytesToRead, out messageState.ThreadId);
                    // Offset the index by the control bytes
                    currentOffset += _controlBytesPlaceholder.Length;
                    // Take control bytes off of bytes read
                    bytesRead -= _controlBytesPlaceholder.Length;
                }

                int numberOfBytesToRead = Math.Min(bytesRead, messageState.TotalBytesToRead);
                messageState.Data.Write(messageState.Buffer, currentOffset, numberOfBytesToRead);

                // Set total bytes read
                int originalTotalBytesToRead = messageState.TotalBytesToRead;
                messageState.TotalBytesToRead -= numberOfBytesToRead;

                // Check if we're done
                if (messageState.TotalBytesToRead == 0)
                {
                    // Done, add to complete received messages
                    CompleteMessage(messageState.Handler, messageState.ThreadId, messageState.Data.ToArray());
                }

                // Check if we have an overlapping message frame in our message AKA if the bytesRead was larger than the total bytes to read
                if (bytesRead > originalTotalBytesToRead)
                {
                    // Get the number of bytes remaining to be read
                    int bytesRemaining = bytesRead - numberOfBytesToRead;

                    // Set total bytes to read to default
                    messageState.TotalBytesToRead = -1;
                    // Dispose and reinitialize data stream
                    messageState.Data.Dispose();
                    messageState.Data = new MemoryStream();

                    // Now we have the next message, so recursively process it
                    currentOffset += numberOfBytesToRead;
                    bytesRead = bytesRemaining;
                    continue;
                }

                // Only create a new message state if we are done with this message
                if (!(bytesRead < originalTotalBytesToRead))
                {
                    // Get new state for the next message but transfer over handler
                    Socket handler = messageState.Handler;
                    _messageStatePool.Push(messageState);
                    _bufferPool.Push(messageState.Buffer);
                    messageState = _messageStatePool.Pop();
                    messageState.Data = new MemoryStream();
                    messageState.Handler = handler;
                    messageState.TotalBytesToRead = -1;
                    messageState.ThreadId = -1;
                }

                // Reset buffer for next message
                messageState.Buffer = null;
            }
        }

        /// <summary>
        /// Sends a message to the server.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <param name="registerForResponse">Whether or not to register for a response. Set this false if you don't care about the response. If you do care, set this to true and then call ClientReceive.</param>
        public void ClientSend(byte[] message, bool registerForResponse)
        {
            if (!_useClientMultiplexer)
            {
                throw new InvalidOperationException("Cannot call ClientSend when listening for connections");
            }

            // Sanitize
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            int threadId = Thread.CurrentThread.ManagedThreadId;

            // Check if we need to register with the multiplexer
            if (registerForResponse)
            {
                EnrollMultiplexer(threadId);
            }

            // Create room for the control bytes
            var messageWithControlBytes = new byte[message.Length + _controlBytesPlaceholder.Length];
            Buffer.BlockCopy(message, 0, messageWithControlBytes, _controlBytesPlaceholder.Length, message.Length);
            // Set the control bytes on the message
            SetControlBytes(messageWithControlBytes, threadId);

            // Do the send
            _socket.BeginSend(messageWithControlBytes, 0, messageWithControlBytes.Length, 0, SendCallback, _socket);
        }

        private void SendCallback(IAsyncResult asyncResult)
        {
            // Get the socket to complete on
            Socket socket = (Socket)asyncResult.AsyncState;

            // Complete the send
            socket.EndSend(asyncResult);
        }

        /// <summary>
        /// Receives a message from the server.
        /// </summary>
        /// <returns>The message.</returns>
        public byte[] ClientReceive()
        {
            if (!_useClientMultiplexer)
            {
                throw new InvalidOperationException("Cannot call ClientReceive when listening for connections");
            }

            // Get this thread's message state object and manual reset event
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var multiplexerData = GetMultiplexerData(threadId);

            // Wait for our message to go ahead from the receive callback
            multiplexerData.ManualResetEvent.WaitOne();

            // Now get the command string
            var result = multiplexerData.Message;

            // Finally remove the thread from the multiplexer
            UnenrollMultiplexer(threadId);

            return result;
        }

        /// <summary>
        /// Sends a message back to the client.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <param name="receivedMessage">The received message.</param>
        public void ServerSend(byte[] message, ReceivedMessage receivedMessage)
        {
            if (_useClientMultiplexer)
            {
                throw new InvalidOperationException("Cannot call ServerSend when connected to a remote server");
            }

            // Sanitize
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }
            if (receivedMessage.Socket == null)
            {
                throw new ArgumentException("contains corrupted data", "receivedMessageState");
            }

            // Create room for the control bytes
            var messageWithControlBytes = new byte[message.Length + _controlBytesPlaceholder.Length];
            Buffer.BlockCopy(message, 0, messageWithControlBytes, _controlBytesPlaceholder.Length, message.Length);
            // Set the control bytes on the message
            SetControlBytes(messageWithControlBytes, receivedMessage.ThreadId);

            // Do the send to the appropriate client
            receivedMessage.Socket.BeginSend(messageWithControlBytes, 0, messageWithControlBytes.Length, 0, SendCallback, receivedMessage.Socket);

            // Put received message back in the pool
            _receiveMessagePool.Push(receivedMessage);
        }

        /// <summary>
        /// Disposes the class.
        /// </summary>
        public void Dispose()
        {
            // Close/dispose the socket
            _socket.Close();
        }

        private void CompleteMessage(Socket handler, int threadId, byte[] message)
        {
            // For server, notify the server to do something
            if (!_useClientMultiplexer)
            {
                var receivedMessage = _receiveMessagePool.Pop();
                receivedMessage.Socket = handler;
                receivedMessage.ThreadId = threadId;
                receivedMessage.Message = message;

                _receivedMessageFunc(receivedMessage);
                return;
            }

            // For client, set and signal multiplexer
            var multiplexerData = GetMultiplexerData(threadId);
            multiplexerData.Message = message;

            SignalMultiplexer(threadId);
        }

        private class MessageState
        {
            public byte[] Buffer = null;
            public Socket Handler = null;
            public MemoryStream Data = null;
            public int ThreadId = -1;
            public int TotalBytesToRead = -1;
        }

        public class ReceivedMessage
        {
            internal Socket Socket;
            internal int ThreadId;
            public byte[] Message;
        }

        private class MultiplexerData
        {
            public byte[] Message { get; set; }
            public ManualResetEvent ManualResetEvent { get; set; }
        }

        private static void SetControlBytes(byte[] buffer, int threadId)
        {
            var length = buffer.Length;
            // Set little endian message length
            buffer[0] = (byte)length;
            buffer[1] = (byte)((length >> 8) & 0xFF);
            buffer[2] = (byte)((length >> 16) & 0xFF);
            buffer[3] = (byte)((length >> 24) & 0xFF);
            // Set little endian thread id
            buffer[4] = (byte)threadId;
            buffer[5] = (byte)((threadId >> 8) & 0xFF);
            buffer[6] = (byte)((threadId >> 16) & 0xFF);
            buffer[7] = (byte)((threadId >> 24) & 0xFF);
        }

        private static void ExtractControlBytes(byte[] buffer, int offset, out int messageLength, out int threadId)
        {
            messageLength = (buffer[offset + 3] << 24) | (buffer[offset + 2] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 0] - _controlBytesPlaceholder.Length;
            threadId = (buffer[offset + 7] << 24) | (buffer[offset + 6] << 16) | (buffer[offset + 5] << 8) | buffer[offset + 4];
        }

        private MultiplexerData GetMultiplexerData(int threadId)
        {
            MultiplexerData multiplexerData;
            _clientMultiplexerLock.EnterReadLock();
            try
            {
                // Get from multiplexer by thread ID
                if (!_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    throw new Exception("FATAL: multiplexer was missing entry for Thread ID " + threadId);
                }

                return multiplexerData;
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }
        }

        private void EnrollMultiplexer(int threadId)
        {
            _clientMultiplexerLock.EnterWriteLock();
            try
            {
                // Add manual reset event for current thread
                _clientMultiplexer.Add(threadId, new MultiplexerData { ManualResetEvent = _manualResetEventPool.Pop() });
            }
            catch
            {
                throw new Exception("FATAL: multiplexer tried to add duplicate entry for Thread ID " + threadId);
            }
            finally
            {
                _clientMultiplexerLock.ExitWriteLock();
            }
        }

        private void UnenrollMultiplexer(int threadId)
        {
            MultiplexerData multiplexerData;
            _clientMultiplexerLock.EnterUpgradeableReadLock();
            try
            {
                // Get from multiplexer by thread ID
                if (!_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    throw new Exception("FATAL: multiplexer was missing entry for Thread ID " + threadId);
                }

                _clientMultiplexerLock.EnterWriteLock();
                try
                {
                    // Remove entry
                    _clientMultiplexer.Remove(threadId);
                }
                finally
                {
                    _clientMultiplexerLock.ExitWriteLock();
                }
            }
            finally
            {
                _clientMultiplexerLock.ExitUpgradeableReadLock();
            }

            // Now return objects to pools
            _manualResetEventPool.Push(multiplexerData.ManualResetEvent);
        }

        private void SignalMultiplexer(int threadId)
        {
            MultiplexerData multiplexerData;
            _clientMultiplexerLock.EnterReadLock();
            try
            {
                // Get from multiplexer by thread ID
                if (!_clientMultiplexer.TryGetValue(threadId, out multiplexerData))
                {
                    throw new Exception("FATAL: multiplexer was missing entry for Thread ID " + threadId);
                }

                multiplexerData.ManualResetEvent.Set();
            }
            finally
            {
                _clientMultiplexerLock.ExitReadLock();
            }
        }
    }
}
