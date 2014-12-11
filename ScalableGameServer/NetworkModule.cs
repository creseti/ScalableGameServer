using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;  
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;



namespace ScalableGameServer
{
    class IOCPServer
    {
        const int BUF_SIZE = 8192;
        const int WORKER_THREAD_COUNT = 4;

       
        [StructLayout(LayoutKind.Sequential)]
        internal class PerHandleData
        {
            public IntPtr mSocket;
            public GCHandle mThis;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal class PerIoData
        {            
            public GCHandle mGCHOverlapped;                        
            public GCHandle mGCHBuffer;

            public WSABuffer mWSABuffer;
            public OperationType mType;
        }

        [StructLayout(LayoutKind.Sequential)]  
        internal class Overlapped
        {
            public IntPtr mInternalLow;
            public IntPtr mInternalHigh;
            public int mOffsetLow;
            public int mOffsetHigh;
            public IntPtr mEventHandle;

            public GCHandle mGCHPerIoData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WSABuffer
        {
            public uint mLength;
            public IntPtr mPointer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WSAData
        {
            internal short wVersion;
            internal short wHighVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 0x101)]
            internal string szDescription;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 0x81)]
            internal string szSystemStatus;
            internal short iMaxSockets;
            internal short iMaxUdpDg;
            internal IntPtr lpVendorInfo;
        }

        [Flags]
        internal enum SocketConstructorFlags
        {
            WSA_FLAG_OVERLAPPED = 0x01,
            WSA_FLAG_MULTIPOINT_C_ROOT = 0x02,
            WSA_FLAG_MULTIPOINT_C_LEAF = 0x04,
            WSA_FLAG_MULTIPOINT_D_ROOT = 0x08,
            WSA_FLAG_MULTIPOINT_D_LEAF = 0x10,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeFileHandle CreateIoCompletionPort(IntPtr fileHandle, IntPtr existingCompletionPort, IntPtr completionKey, uint numberOfConcurrentThreads);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetQueuedCompletionStatus(SafeFileHandle completionPort, out uint lpNumberOfBytesTransferred, out IntPtr lpCompletionKey, out IntPtr lpOverlapped, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool PostQueuedCompletionStatus(SafeFileHandle completionPort, uint dwNumberOfBytesTransferred, IntPtr dwCompletionKey, IntPtr lpOverlapped);

        [DllImport("ws2_32.dll", SetLastError = true)]
		internal static extern SocketError WSAStartup(short wVersionRequested, out WSAData lpWSAData);

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern IntPtr WSASocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, IntPtr protocolInfo, uint group, SocketConstructorFlags flags);

        [DllImport("ws2_32.dll", SetLastError = true)]
		public static extern int WSAGetLastError();

		[DllImport("ws2_32.dll", SetLastError = true)]
        private static extern SocketError bind(IntPtr socketHandle, byte[] socketAddress, int socketAddressSize);

		[DllImport("ws2_32.dll", SetLastError = true)]
        private static extern SocketError listen(IntPtr socketHandle, int backlog);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern IntPtr accept(IntPtr socketHandle, byte[] socketAddress, ref int socketAddressSize);

		[DllImport("ws2_32.dll", SetLastError = true)]
        public static extern SocketError WSARecv(IntPtr socketHandle, ref WSABuffer buffer, int bufferCount, out int bytesTransferred, ref SocketFlags socketFlags, IntPtr overlapped, IntPtr completionRoutine);

        [DllImport("ws2_32.dll", SetLastError = true)]
		private static extern SocketError closesocket(IntPtr socketHandle);

		[DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern SocketError WSASend(IntPtr socketHandle, ref WSABuffer buffer, int bufferCount, out int bytesTransferred, SocketFlags socketFlags, IntPtr overlapped, IntPtr completionRoutine);

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern Int32 setsockopt(IntPtr s, SocketOptionLevel level, SocketOptionName optname, ref Object optval, Int32 optlen);

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern Int32 getsockopt(IntPtr s, SocketOptionLevel level, SocketOptionName optname, ref Object optval, ref Int32 optlen);

        public enum OperationType
        {
            OT_SEND,
            OT_RECEIVE,
            OT_DISCONNECT,
        };


        SafeFileHandle mCompletionPort;
        IntPtr mListenSocket;
        byte[] mMyAddress;
        int mMyAddressSize;
        Int64 mTotalRecvBytes = 0;


        public void Start()
        {
            // Initialize WS
            WSAData wsaData;            
            if (WSAStartup(0x0202, out wsaData) != SocketError.Success)            
            {
                Console.WriteLine("WSAStartup Error! : " + WSAGetLastError());
                return;
            }
                        
            // Create completion port
            mCompletionPort = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, IntPtr.Zero, 0);
            if (mCompletionPort.IsInvalid)
            {
                Console.WriteLine("CreateIoCompletionPort Error! : " + WSAGetLastError());
                return;
            }

            // Create listen socket
            mListenSocket = WSASocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, IntPtr.Zero, 0, SocketConstructorFlags.WSA_FLAG_OVERLAPPED);
            if (mListenSocket.ToInt32() == 0)
            {
                Console.WriteLine("WSASocket Error! : " + WSAGetLastError());
                return;
            }

            // Bind address
            IPEndPoint address = new IPEndPoint(IPAddress.Any, 9001);
            FieldInfo bufferFieldInfo = typeof(SocketAddress).GetField("m_Buffer", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo sizeFieldInfo = typeof(SocketAddress).GetField("m_Size", BindingFlags.Instance | BindingFlags.NonPublic);
            mMyAddress = (byte[])bufferFieldInfo.GetValue(address.Serialize());
            mMyAddressSize = (int)sizeFieldInfo.GetValue(address.Serialize());

            if (bind(mListenSocket, mMyAddress, mMyAddressSize) != SocketError.Success)
            {
                Console.WriteLine("bind Error! : " + WSAGetLastError());
                return;
            }

            // Listen
            if (listen(mListenSocket, 128) != SocketError.Success)
            {
                Console.WriteLine("listen Error! : " + WSAGetLastError());
                return;
            }

            // Create worker thread and run
            CreateWorkerThread(WORKER_THREAD_COUNT);

            // Accept loop
            while (true)
            {
                // Accept (Wait...)
                IntPtr acceptedSocket;
                acceptedSocket = accept(mListenSocket, mMyAddress, ref mMyAddressSize);
                    
                // Welcome!          
                PerHandleData perHandleData = new PerHandleData();
                GCHandle gchPerHandleData = GCHandle.Alloc(perHandleData, GCHandleType.Pinned);
                perHandleData.mSocket = acceptedSocket;
                perHandleData.mThis = gchPerHandleData;

                // Assign socket to completion port
                CreateIoCompletionPort(acceptedSocket, mCompletionPort.DangerousGetHandle(), GCHandle.ToIntPtr(gchPerHandleData), 0);

                // Change socket options
                /*
                Object socketRecvBufferSize = 0;
                int oldSocketRecvBufferSize = 0;
                getsockopt(acceptedSocket, SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, ref socketRecvBufferSize, ref oldSocketRecvBufferSize);
                ... 
                */                

                // Receive
                ReceiveData(acceptedSocket);
            }            
        }

        private void ReceiveData(IntPtr socket)
        {
            PerIoData perIoData = new PerIoData();
            GCHandle gchPerIoData = GCHandle.Alloc(perIoData, GCHandleType.Pinned);
            
            Overlapped overlapped = new Overlapped();
            overlapped.mGCHPerIoData = gchPerIoData;
            perIoData.mGCHOverlapped = GCHandle.Alloc(overlapped, GCHandleType.Pinned);

            byte[] buffer = new byte[BUF_SIZE];
            perIoData.mGCHBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            perIoData.mWSABuffer.mLength = BUF_SIZE;
            perIoData.mWSABuffer.mPointer = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
            perIoData.mType = OperationType.OT_RECEIVE;

            int recvByte = 0;
            SocketFlags socketFlags = SocketFlags.None;
            SocketError result = WSARecv(socket, ref perIoData.mWSABuffer, 1, out recvByte, ref socketFlags, perIoData.mGCHOverlapped.AddrOfPinnedObject(), IntPtr.Zero);
            if (result != SocketError.SocketError && result != SocketError.Success && result != SocketError.IOPending)
            {
                Console.WriteLine("WSARecv Error! : " + result.ToString() + WSAGetLastError());
            }
        }

        private void CreateWorkerThread(int numberOfWorkerThread)
        {
            for (int i = 0; i < numberOfWorkerThread; i++)
            {
                Thread workerThread = new Thread(WorkerThread);
                workerThread.Start();
            }
        }    
   
        public void WorkerThread()
		{
			while (true)
			{                                
                IntPtr addressOfPerHandleData;
                IntPtr addressOfOverlapped;
                uint transferredBytes;
             
                // Get!
                bool result = GetQueuedCompletionStatus(mCompletionPort, out transferredBytes, out addressOfPerHandleData, out addressOfOverlapped, uint.MaxValue);
                
                if (result == false)
                {
                    Console.WriteLine("GetQueuedCompletionStatus Error! : " + WSAGetLastError());
                    Thread.Sleep(1000);
                    continue;
                }

                // Retrive data
                GCHandle gchPerHandleData = GCHandle.FromIntPtr(addressOfPerHandleData);
                PerHandleData perHandleData = (PerHandleData)gchPerHandleData.Target;

                Overlapped overlapped = new Overlapped();
                Marshal.PtrToStructure(addressOfOverlapped, overlapped);
                PerIoData perIoData = (PerIoData)overlapped.mGCHPerIoData.Target;
                                
                switch (perIoData.mType)
                {
                    case OperationType.OT_DISCONNECT:                        
                        break;

                    case OperationType.OT_RECEIVE:
                        Interlocked.Add(ref mTotalRecvBytes, transferredBytes);
                        Console.WriteLine("ReceiveData Success! (Total bytes : " + mTotalRecvBytes + ")");
                        ReceiveData(perHandleData.mSocket);
                        break;

                    case OperationType.OT_SEND:
                        break;
                }

                // Release perIoData
                perIoData.mGCHBuffer.Free();
                perIoData.mGCHOverlapped.Free();
                overlapped.mGCHPerIoData.Free();

                // Disconnect (Release perHandleData)
                if (transferredBytes == 0)
                {
                    perHandleData.mThis.Free();
                }
			}
		}       
    }

    class RIOServer
    {
    }

    class NetAsyncIOServer
    {
    }
}
