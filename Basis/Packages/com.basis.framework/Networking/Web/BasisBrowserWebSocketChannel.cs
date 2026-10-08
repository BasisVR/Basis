using Basis.Network.Core;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    public sealed class BasisBrowserWebSocketChannel : IBasisWebSocketClientChannel
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int BasisWS_Open(string url, string protocol);
        [DllImport("__Internal")]
        private static extern int BasisWS_PeekType(int id);
        [DllImport("__Internal")]
        private static extern int BasisWS_PeekSize(int id);
        [DllImport("__Internal")]
        private static extern int BasisWS_Take(int id, byte[] buffer, int capacity);
        [DllImport("__Internal")]
        private static extern int BasisWS_Send(int id, byte[] buffer, int offset, int length);
        [DllImport("__Internal")]
        private static extern int BasisWS_CloseCode(int id);
        [DllImport("__Internal")]
        private static extern void BasisWS_Close(int id, int code);
        [DllImport("__Internal")]
        private static extern void BasisWS_Free(int id);
#endif
        private const int TypeNone = 0;
        private const int TypeOpen = 1;
        private const int TypeMessage = 2;
        private const int TypeClose = 3;
        private const int TypeError = 4;
        private const int RetainedBufferBytes = 4 * 1024 * 1024;
        private IBasisWebSocketChannelHandler _handler;
        private byte[] _buffer = new byte[64 * 1024];
        private int _id;
        private bool _closedRaised;

        public static bool IsSupported => Application.platform == RuntimePlatform.WebGLPlayer;

        public bool RequiresPolling => true;

        public bool IsOpen => _id > 0 && !_closedRaised;

        public void Open(string url, string subProtocol, IBasisWebSocketChannelHandler handler)
        {
            _handler = handler;
#if UNITY_WEBGL && !UNITY_EDITOR
            _id = BasisWS_Open(url, subProtocol ?? string.Empty);
#else
            throw new PlatformNotSupportedException("The browser WebSocket channel only runs in web builds.");
#endif
        }

        public Task SendAsync(byte[] buffer, int offset, int count)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_id > 0 && BasisWS_Send(_id, buffer, offset, count) >= 0)
            {
                return Task.CompletedTask;
            }
#endif
            return Task.FromException(new InvalidOperationException("The WebSocket is not open."));
        }

        public Task CloseAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_id > 0)
            {
                BasisWS_Close(_id, 1000);
            }
#endif
            return Task.CompletedTask;
        }

        public void Abort()
        {
            CloseAsync();
            Release();
            RaiseClosed(1006);
        }

        public void Poll()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            while (_id > 0)
            {
                int type = BasisWS_PeekType(_id);
                switch (type)
                {
                    case TypeNone:
                        return;
                    case TypeMessage:
                        int size = BasisWS_PeekSize(_id);
                        if (size > _buffer.Length)
                        {
                            _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
                        }
                        int taken = BasisWS_Take(_id, _buffer, _buffer.Length);
                        if (taken > 0)
                        {
                            _handler?.OnMessage(_buffer, 0, taken);
                        }
                        if (_buffer.Length > RetainedBufferBytes)
                        {
                            _buffer = new byte[64 * 1024];
                        }
                        break;
                    case TypeOpen:
                        BasisWS_Take(_id, _buffer, 0);
                        _handler?.OnOpen();
                        break;
                    case TypeError:
                        BasisWS_Take(_id, _buffer, 0);
                        _handler?.OnError("WebSocket error");
                        break;
                    default:
                        int code = BasisWS_CloseCode(_id);
                        Release();
                        RaiseClosed(code);
                        return;
                }
            }
#endif
        }

        private void Release()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_id > 0)
            {
                BasisWS_Free(_id);
            }
#endif
            _id = 0;
        }

        private void RaiseClosed(int code)
        {
            if (_closedRaised) return;
            _closedRaised = true;
            _handler?.OnClosed(code, string.Empty, code == 1000);
        }
    }

    public static class BasisBrowserWebSocketBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install()
        {
            if (!BasisBrowserWebSocketChannel.IsSupported) return;
            BasisWebSocketNetManager.ClientChannelFactory = () => new BasisBrowserWebSocketChannel();
            BasisNetworkStackRegistry.ReplaceFactory(BasisNetworkStackRegistry.LiteNetLibId, (listener, configuration) => new BasisWebSocketNetManager(listener, configuration));
        }
    }
}
