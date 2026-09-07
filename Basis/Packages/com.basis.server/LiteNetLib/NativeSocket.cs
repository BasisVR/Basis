using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteNetLib
{
    internal static class NativeSocket
    {
        static unsafe class WinSock
        {
            private const string LibName = "ws2_32.dll";

            [DllImport(LibName, SetLastError = true)]
            public static extern int recvfrom(
                IntPtr socketHandle,
                [In, Out] byte[] pinnedBuffer,
                [In] int len,
                [In] SocketFlags socketFlags,
                [Out] byte[] socketAddress,
                [In, Out] ref int socketAddressSize);

            [DllImport(LibName, SetLastError = true)]
            internal static extern int sendto(
                IntPtr socketHandle,
                byte* pinnedBuffer,
                [In] int len,
                [In] SocketFlags socketFlags,
                [In] byte[] socketAddress,
                [In] int socketAddressSize);

            /// <summary>
            /// Same call, but taking the address as a raw pointer so the batch path can send
            /// straight out of its arena instead of marshalling a managed array per datagram.
            /// </summary>
            [DllImport(LibName, EntryPoint = "sendto", SetLastError = true)]
            internal static extern int sendto_ptr(
                IntPtr socketHandle,
                byte* pinnedBuffer,
                [In] int len,
                [In] SocketFlags socketFlags,
                byte* socketAddress,
                [In] int socketAddressSize);
        }

        static unsafe class UnixSock
        {
            private const string LibName = "libc";

            [DllImport(LibName, SetLastError = true)]
            public static extern int recvfrom(
                IntPtr socketHandle,
                [In, Out] byte[] pinnedBuffer,
                [In] int len,
                [In] SocketFlags socketFlags,
                [Out] byte[] socketAddress,
                [In, Out] ref int socketAddressSize);

            [DllImport(LibName, SetLastError = true)]
            internal static extern int sendto(
                IntPtr socketHandle,
                byte* pinnedBuffer,
                [In] int len,
                [In] SocketFlags socketFlags,
                [In] byte[] socketAddress,
                [In] int socketAddressSize);

            /// <summary>
            /// Same call, but taking the address as a raw pointer so the batch path can send
            /// straight out of its arena instead of marshalling a managed array per datagram.
            /// </summary>
            [DllImport(LibName, EntryPoint = "sendto", SetLastError = true)]
            internal static extern int sendto_ptr(
                IntPtr socketHandle,
                byte* pinnedBuffer,
                [In] int len,
                [In] SocketFlags socketFlags,
                byte* socketAddress,
                [In] int socketAddressSize);

            /// <summary>
            /// Raw setsockopt. Needed because .NET's Unix socket layer validates option names
            /// against its own mapping table instead of passing the value through, so an option it
            /// does not know — SO_REUSEPORT among them — is rejected with OperationNotSupported
            /// however it is cast. Going straight to libc is the only way to set it.
            /// </summary>
            [DllImport(LibName, SetLastError = true)]
            internal static extern int setsockopt(
                IntPtr socketHandle,
                int level,
                int optname,
                ref int optval,
                uint optlen);

            /// <summary>
            /// Sends up to <paramref name="vlen"/> datagrams — each to its own destination — in a
            /// single syscall. This is the whole point of the batch path: a broadcast server's
            /// cost is dominated by syscall count, not by bytes.
            /// Returns the number of messages sent, or -1.
            /// </summary>
            [DllImport(LibName, SetLastError = true)]
            internal static extern int sendmmsg(
                IntPtr socketHandle,
                MmsgHdr* msgvec,
                uint vlen,
                int flags);

            /// <summary>
            /// The receive counterpart: fills up to <c>vlen</c> message headers in one call. A
            /// receive thread is one core's worth of syscalls, so this is the axis inbound
            /// throughput scales on for a thread that is already busy.
            /// Returns the number of messages received, or -1.
            /// </summary>
            [DllImport(LibName, SetLastError = true)]
            internal static extern int recvmmsg(
                IntPtr socketHandle,
                MmsgHdr* msgvec,
                uint vlen,
                int flags,
                void* timeout);
        }

        /// <summary>Linux <c>struct iovec</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct IoVec
        {
            public byte* Base;
            public IntPtr Length;   // size_t
        }

        /// <summary>
        /// Linux <c>struct msghdr</c>. Field order and widths must match the platform ABI exactly;
        /// this is the 64-bit layout (socklen_t is 32-bit and followed by 4 bytes of padding,
        /// which <see cref="LayoutKind.Sequential"/> inserts because the next field is pointer-aligned).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct MsgHdr
        {
            public byte* Name;          // sockaddr*
            public uint NameLen;        // socklen_t
            private uint _pad;
            public IoVec* Iov;
            public IntPtr IovLen;       // size_t
            public byte* Control;
            public IntPtr ControlLen;   // size_t
            public int Flags;
        }

        /// <summary>Linux <c>struct mmsghdr</c>: a msghdr plus the per-message sent length.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct MmsgHdr
        {
            public MsgHdr Hdr;
            public uint Len;
            private uint _pad;
        }

        public static readonly bool IsSupported = false;
        public static readonly bool UnixMode = false;

        /// <summary>
        /// True when <see cref="SendBatch"/> can hand the whole batch to the kernel in one call
        /// (Linux <c>sendmmsg</c>). Elsewhere the batch still works — it just costs one syscall per
        /// datagram, so only the managed-side per-send overhead is saved.
        /// Probed once at startup rather than assumed from the OS, because sendmmsg is absent on
        /// old kernels and blocked by some seccomp sandboxes.
        /// </summary>
        public static readonly bool SupportsBatchSend = false;

        /// <summary>
        /// True when <see cref="RecvBatch"/> can take a vector of datagrams from the kernel in one
        /// call (Linux <c>recvmmsg</c>). Probed for the same reasons as
        /// <see cref="SupportsBatchSend"/>: it is absent on old kernels and blocked by some
        /// seccomp sandboxes, and finding that out under load is worse than finding it out at
        /// startup.
        /// </summary>
        public static readonly bool SupportsBatchRecv = false;

        /// <summary>
        /// Linux <c>MSG_WAITFORONE</c>: block for the first datagram, then take whatever else is
        /// already queued and return. Without it recvmmsg waits for the whole vector to fill, which
        /// on a quiet socket means holding the first datagram until 63 more arrive.
        /// </summary>
        private const int MSG_WAITFORONE = 0x10000;

        public const int IPv4AddrSize = 16;
        public const int IPv6AddrSize = 28;
        public const int AF_INET = 2;
        public const int AF_INET6 = 10;

        private static readonly Dictionary<int, SocketError> LinuxErrorToSocketError = new Dictionary<int, SocketError>
        {
            { 13, SocketError.AccessDenied },               //EACCES
            { 98, SocketError.AddressAlreadyInUse },        //EADDRINUSE
            { 99, SocketError.AddressNotAvailable },        //EADDRNOTAVAIL
            { 97, SocketError.AddressFamilyNotSupported },  //EAFNOSUPPORT
            { 11, SocketError.WouldBlock },                 //EAGAIN
            { 114, SocketError.AlreadyInProgress },         //EALREADY
            { 9, SocketError.OperationAborted },            //EBADF
            { 125, SocketError.OperationAborted },          //ECANCELED
            { 103, SocketError.ConnectionAborted },         //ECONNABORTED
            { 111, SocketError.ConnectionRefused },         //ECONNREFUSED
            { 104, SocketError.ConnectionReset },           //ECONNRESET
            { 89, SocketError.DestinationAddressRequired }, //EDESTADDRREQ
            { 14, SocketError.Fault },                      //EFAULT
            { 112, SocketError.HostDown },                  //EHOSTDOWN
            { 6, SocketError.HostNotFound },                //ENXIO
            { 113, SocketError.HostUnreachable },           //EHOSTUNREACH
            { 115, SocketError.InProgress },                //EINPROGRESS
            { 4, SocketError.Interrupted },                 //EINTR
            { 22, SocketError.InvalidArgument },            //EINVAL
            { 106, SocketError.IsConnected },               //EISCONN
            { 24, SocketError.TooManyOpenSockets },         //EMFILE
            { 90, SocketError.MessageSize },                //EMSGSIZE
            { 100, SocketError.NetworkDown },               //ENETDOWN
            { 102, SocketError.NetworkReset },              //ENETRESET
            { 101, SocketError.NetworkUnreachable },        //ENETUNREACH
            { 23, SocketError.TooManyOpenSockets },         //ENFILE
            { 105, SocketError.NoBufferSpaceAvailable },    //ENOBUFS
            { 61, SocketError.NoData },                     //ENODATA
            { 2, SocketError.AddressNotAvailable },         //ENOENT
            { 92, SocketError.ProtocolOption },             //ENOPROTOOPT
            { 107, SocketError.NotConnected },              //ENOTCONN
            { 88, SocketError.NotSocket },                  //ENOTSOCK
            { 3440, SocketError.OperationNotSupported },    //ENOTSUP
            { 1, SocketError.AccessDenied },                //EPERM
            { 32, SocketError.Shutdown },                   //EPIPE
            { 96, SocketError.ProtocolFamilyNotSupported }, //EPFNOSUPPORT
            { 93, SocketError.ProtocolNotSupported },       //EPROTONOSUPPORT
            { 91, SocketError.ProtocolType },               //EPROTOTYPE
            { 94, SocketError.SocketNotSupported },         //ESOCKTNOSUPPORT
            { 108, SocketError.Disconnecting },             //ESHUTDOWN
            { 110, SocketError.TimedOut },                  //ETIMEDOUT
            { 0, SocketError.Success }
        };

        private static readonly Dictionary<int, SocketError> OsxErrorToSocketError = new Dictionary<int, SocketError>
        {
            { 1, SocketError.AccessDenied },                //EPERM
            { 2, SocketError.AddressNotAvailable },         //ENOENT
            { 4, SocketError.Interrupted },                 //EINTR
            { 6, SocketError.HostNotFound },                //ENXIO
            { 9, SocketError.OperationAborted },            //EBADF
            { 13, SocketError.AccessDenied },               //EACCES
            { 14, SocketError.Fault },                      //EFAULT
            { 22, SocketError.InvalidArgument },            //EINVAL
            { 23, SocketError.TooManyOpenSockets },         //ENFILE
            { 24, SocketError.TooManyOpenSockets },         //EMFILE
            { 32, SocketError.Shutdown },                   //EPIPE
            { 35, SocketError.WouldBlock },                 //EAGAIN / EWOULDBLOCK
            { 36, SocketError.InProgress },                 //EINPROGRESS
            { 37, SocketError.AlreadyInProgress },          //EALREADY
            { 38, SocketError.NotSocket },                  //ENOTSOCK
            { 39, SocketError.DestinationAddressRequired }, //EDESTADDRREQ
            { 40, SocketError.MessageSize },                //EMSGSIZE
            { 41, SocketError.ProtocolType },               //EPROTOTYPE
            { 42, SocketError.ProtocolOption },             //ENOPROTOOPT
            { 43, SocketError.ProtocolNotSupported },       //EPROTONOSUPPORT
            { 44, SocketError.SocketNotSupported },         //ESOCKTNOSUPPORT
            { 45, SocketError.OperationNotSupported },      //ENOTSUP
            { 46, SocketError.ProtocolFamilyNotSupported }, //EPFNOSUPPORT
            { 47, SocketError.AddressFamilyNotSupported },  //EAFNOSUPPORT
            { 48, SocketError.AddressAlreadyInUse },        //EADDRINUSE
            { 49, SocketError.AddressNotAvailable },        //EADDRNOTAVAIL
            { 50, SocketError.NetworkDown },                //ENETDOWN
            { 51, SocketError.NetworkUnreachable },         //ENETUNREACH
            { 52, SocketError.NetworkReset },               //ENETRESET
            { 53, SocketError.ConnectionAborted },          //ECONNABORTED
            { 54, SocketError.ConnectionReset },            //ECONNRESET
            { 55, SocketError.NoBufferSpaceAvailable },     //ENOBUFS
            { 56, SocketError.IsConnected },                //EISCONN
            { 57, SocketError.NotConnected },               //ENOTCONN
            { 58, SocketError.Disconnecting },              //ESHUTDOWN
            { 60, SocketError.TimedOut },                   //ETIMEDOUT
            { 61, SocketError.ConnectionRefused },          //ECONNREFUSED
            { 64, SocketError.HostDown },                   //EHOSTDOWN
            { 65, SocketError.HostUnreachable },            //EHOSTUNREACH
            { 89, SocketError.OperationAborted },           //ECANCELED
            { 96, SocketError.NoData },                     //ENODATA
            { 102, SocketError.OperationNotSupported },     //EOPNOTSUPP
            { 0, SocketError.Success }
        };

        private static readonly Dictionary<int, SocketError> NativeErrorToSocketError;

        static NativeSocket()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                IsSupported = true;
                UnixMode = true;
                NativeErrorToSocketError = LinuxErrorToSocketError;
                SupportsBatchSend = ProbeSendmmsg();
                SupportsBatchRecv = ProbeRecvmmsg();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                IsSupported = true;
                UnixMode = true;
                NativeErrorToSocketError = OsxErrorToSocketError;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                IsSupported = true;
                NativeErrorToSocketError = LinuxErrorToSocketError;
            }
            else
            {
                NativeErrorToSocketError = LinuxErrorToSocketError;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RecvFrom(
            IntPtr socketHandle,
            byte[] pinnedBuffer,
            int len,
            byte[] socketAddress,
            ref int socketAddressSize)
        {
            return UnixMode
                ? UnixSock.recvfrom(socketHandle, pinnedBuffer, len, 0, socketAddress, ref socketAddressSize)
                : WinSock.recvfrom(socketHandle, pinnedBuffer, len, 0, socketAddress, ref socketAddressSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int SendTo(
            IntPtr socketHandle,
            byte* pinnedBuffer,
            int len,
            byte[] socketAddress,
            int socketAddressSize)
        {
            return UnixMode
                ? UnixSock.sendto(socketHandle, pinnedBuffer, len, 0, socketAddress, socketAddressSize)
                : WinSock.sendto(socketHandle, pinnedBuffer, len, 0, socketAddress, socketAddressSize);
        }

        /// <summary>
        /// Calls sendmmsg with a zero-length vector on a throwaway UDP socket. A kernel that has
        /// the syscall returns 0; one that does not fails with ENOSYS, and a sandbox that blocks
        /// it fails with EPERM. Doing this once at startup keeps the send path free of fallback
        /// checks and avoids discovering the problem under load.
        /// </summary>
        private static unsafe bool ProbeSendmmsg()
        {
            Socket probe = null;
            try
            {
                probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                int result = UnixSock.sendmmsg(probe.Handle, null, 0, 0);
                if (result >= 0) return true;

                int err = Marshal.GetLastWin32Error();
                NetDebug.WriteError($"[NS] sendmmsg unavailable (errno {err}); batch send falls back to per-datagram sendto.");
                return false;
            }
            catch (Exception e)
            {
                // EntryPointNotFoundException on a libc without the symbol, or anything else the
                // platform throws — either way the fallback path is correct, so never fail startup.
                NetDebug.WriteError($"[NS] sendmmsg probe failed ({e.GetType().Name}); batch send falls back to per-datagram sendto.");
                return false;
            }
            finally
            {
                probe?.Dispose();
            }
        }

        /// <summary>
        /// Calls recvmmsg with a zero-length vector on a throwaway UDP socket, for the same reason
        /// and with the same outcomes as <see cref="ProbeSendmmsg"/>.
        /// </summary>
        private static unsafe bool ProbeRecvmmsg()
        {
            Socket probe = null;
            try
            {
                probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                int result = UnixSock.recvmmsg(probe.Handle, null, 0, 0, null);
                if (result >= 0) return true;

                int err = Marshal.GetLastWin32Error();
                NetDebug.WriteError($"[NS] recvmmsg unavailable (errno {err}); receive falls back to one recvfrom per datagram.");
                return false;
            }
            catch (Exception e)
            {
                NetDebug.WriteError($"[NS] recvmmsg probe failed ({e.GetType().Name}); receive falls back to one recvfrom per datagram.");
                return false;
            }
            finally
            {
                probe?.Dispose();
            }
        }

        /// <summary>
        /// Takes up to <paramref name="count"/> datagrams in one syscall, blocking for the first
        /// and then draining whatever else is already queued. The headers must already point at the
        /// caller's buffers. Returns how many arrived, or -1.
        /// </summary>
        public static unsafe int RecvBatch(IntPtr socketHandle, MmsgHdr* headers, uint count)
        {
            return UnixSock.recvmmsg(socketHandle, headers, count, MSG_WAITFORONE, null);
        }

        /// <summary>
        /// One entry in a batch: a slice of the caller's pinned arena plus the destination address.
        /// Pointers, not arrays, so the batch can be built once and handed straight to the kernel.
        /// </summary>
        internal unsafe struct BatchEntry
        {
            public byte* Data;
            public int Length;
            public byte* Address;
            public int AddressLength;
        }

        /// <summary>
        /// Sends every entry in <paramref name="entries"/>.
        ///
        /// On Linux this is one <c>sendmmsg</c> syscall for the whole batch (looping only if the
        /// kernel accepts a partial vector). Everywhere else it is a tight <c>sendto</c> loop over
        /// the same already-pinned memory — the syscall count is unchanged there, but the managed
        /// per-send cost (pinning, exception frames, endpoint dispatch) is paid once for the batch
        /// instead of once per datagram.
        ///
        /// Returns the number of datagrams accepted. A short return means the caller should treat
        /// the remainder as dropped — this is unreliable UDP, and the channel layer already
        /// handles loss.
        /// </summary>
        public static unsafe int SendBatch(
            IntPtr socketHandle,
            BatchEntry* entries,
            int count,
            MmsgHdr* headers,
            IoVec* iovecs)
        {
            if (count <= 0) return 0;

            if (SupportsBatchSend)
            {
                for (int i = 0; i < count; i++)
                {
                    iovecs[i].Base = entries[i].Data;
                    iovecs[i].Length = (IntPtr)entries[i].Length;

                    headers[i] = default;
                    headers[i].Hdr.Name = entries[i].Address;
                    headers[i].Hdr.NameLen = (uint)entries[i].AddressLength;
                    headers[i].Hdr.Iov = &iovecs[i];
                    headers[i].Hdr.IovLen = (IntPtr)1;
                }

                int sentTotal = 0;
                while (sentTotal < count)
                {
                    int sent = UnixSock.sendmmsg(socketHandle, headers + sentTotal, (uint)(count - sentTotal), 0);
                    if (sent <= 0)
                    {
                        // EWOULDBLOCK/ENOBUFS on a saturated socket: the rest of this batch is
                        // dropped, exactly as an unbatched send would have been.
                        break;
                    }
                    sentTotal += sent;
                }
                return sentTotal;
            }

            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                int result = UnixMode
                    ? UnixSock.sendto_ptr(socketHandle, entries[i].Data, entries[i].Length, 0, entries[i].Address, entries[i].AddressLength)
                    : WinSock.sendto_ptr(socketHandle, entries[i].Data, entries[i].Length, 0, entries[i].Address, entries[i].AddressLength);
                if (result < 0) break;
                ok++;
            }
            return ok;
        }

        // Linux values. SOL_SOCKET is 1 and SO_REUSEPORT is 15 there; both differ on macOS/BSD
        // (0xffff / 0x0200), which is why this is gated to Linux by the caller rather than guessed.
        private const int SOL_SOCKET_LINUX = 1;
        private const int SO_REUSEPORT_LINUX = 15;

        /// <summary>
        /// Enables SO_REUSEPORT on <paramref name="socketHandle"/>, so several sockets can share one
        /// UDP port and the kernel hashes inbound 4-tuples across them. Must be set before bind.
        /// Returns false with <paramref name="errno"/> set when the kernel refuses.
        ///
        /// Linux only — the caller checks the platform. It exists as a P/Invoke because the managed
        /// SetSocketOption cannot express this option at all; see <see cref="UnixSock.setsockopt"/>.
        /// </summary>
        public static bool TryEnableReusePort(IntPtr socketHandle, out int errno)
        {
            errno = 0;
            if (!UnixMode) return false;

            try
            {
                int enable = 1;
                int result = UnixSock.setsockopt(
                    socketHandle, SOL_SOCKET_LINUX, SO_REUSEPORT_LINUX, ref enable, sizeof(int));
                if (result == 0) return true;

                errno = Marshal.GetLastWin32Error();
                return false;
            }
            catch (Exception)
            {
                // No libc symbol, or a platform that does not have it — treat as unsupported.
                return false;
            }
        }

        public static SocketError GetSocketError()
        {
            int error = Marshal.GetLastWin32Error();
            if (UnixMode)
                return NativeErrorToSocketError.TryGetValue(error, out var err)
                    ? err
                    : SocketError.SocketError;
            return (SocketError)error;
        }

        public static SocketException GetSocketException()
        {
            int error = Marshal.GetLastWin32Error();
            if (UnixMode)
                return NativeErrorToSocketError.TryGetValue(error, out var err)
                    ? new SocketException((int)err)
                    : new SocketException((int)SocketError.SocketError);
            return new SocketException(error);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short GetNativeAddressFamily(IPEndPoint remoteEndPoint)
        {
            return UnixMode
                ? (short)(remoteEndPoint.AddressFamily == AddressFamily.InterNetwork ? AF_INET : AF_INET6)
                : (short)remoteEndPoint.AddressFamily;
        }
    }
}
