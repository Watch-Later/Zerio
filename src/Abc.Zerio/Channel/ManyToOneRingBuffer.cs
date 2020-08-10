using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Abc.Zerio.Interop;
using Adaptive.Agrona;
using Adaptive.Agrona.Concurrent.RingBuffer;

namespace Abc.Zerio.Channel
{
    public unsafe class ManyToOneRingBuffer : IDisposable
    {
        private const int _paddingMessageTypeId = -1;
        private const int _insufficientCapacity = -2;
        private const int _userMessageTypeId = 1;

        private readonly int _capacity;
        private readonly int _maxMsgLength;
        private readonly int _tailPositionIndex;
        private readonly int _headCachePositionIndex;
        private readonly int _headPositionIndex;

        private readonly int _bufferLength;
        private readonly IntPtr _bufferHandle;
        private readonly IntPtr _bufferId;
        private readonly byte* _bufferStart;

        public ManyToOneRingBuffer(int minimumSize)
        {
            _bufferLength = BitUtil.FindNextPositivePowerOfTwo(minimumSize) + RingBufferDescriptor.TrailerLength;

            const int allocationType = Kernel32.Consts.MEM_COMMIT | Kernel32.Consts.MEM_RESERVE;
            _bufferHandle = Kernel32.VirtualAlloc(IntPtr.Zero, (uint)_bufferLength, allocationType, Kernel32.Consts.PAGE_READWRITE);
            if (_bufferHandle == IntPtr.Zero)
                WinSock.ThrowLastWsaError();

            WinSock.EnsureIsInitialized();

            _bufferId = WinSock.Extensions.RegisterBuffer(_bufferHandle, (uint)_bufferLength);
            if (_bufferId == WinSock.Consts.RIO_INVALID_BUFFERID)
                WinSock.ThrowLastWsaError();

            _bufferStart = (byte*)_bufferHandle.ToPointer();

            _capacity = _bufferLength - RingBufferDescriptor.TrailerLength;

            _maxMsgLength = _capacity / 8;
            _tailPositionIndex = _capacity + RingBufferDescriptor.TailPositionOffset;
            _headCachePositionIndex = _capacity + RingBufferDescriptor.HeadCachePositionOffset;
            _headPositionIndex = _capacity + RingBufferDescriptor.HeadPositionOffset;
        }

        public void Dispose()
        {
            try
            {
                WinSock.Extensions.DeregisterBuffer(_bufferId);
            }
            finally
            {
                Kernel32.VirtualFree(_bufferHandle, 0, Kernel32.Consts.MEM_RELEASE);
            }
        }

        public bool Write(ReadOnlySpan<byte> messageBytes)
        {
            CheckMsgLength(messageBytes.Length);

            var isSuccessful = false;

            var buffer = _bufferStart;
            var recordLength = messageBytes.Length + RecordDescriptor.HeaderLength;
            var requiredCapacity = BitUtil.Align(recordLength, RecordDescriptor.Alignment);
            var recordIndex = ClaimCapacity(buffer, requiredCapacity);

            if (_insufficientCapacity != recordIndex)
            {
                PutLongOrdered(buffer, recordIndex, RecordDescriptor.MakeHeader(-recordLength, _userMessageTypeId));
                // TODO JPW original: UnsafeAccess.UNSAFE.storeFence();
                Thread.MemoryBarrier();
                PutBytes(buffer, RecordDescriptor.EncodedMsgOffset(recordIndex), messageBytes);
                PutIntOrdered(buffer, RecordDescriptor.LengthOffset(recordIndex), recordLength);

                isSuccessful = true;
            }

            return isSuccessful;
        }
        
        public int Read(MessageHandler handler)
        {
            return Read(handler, int.MaxValue);
        }
        
        public int Read(MessageHandler handler, int messageCountLimit)
        {
            var messagesRead = 0;
            var buffer = _bufferStart;
            var head = GetLong(buffer, _headPositionIndex);

            var capacity = _capacity;
            var headIndex = (int)head & (capacity - 1);
            var maxBlockLength = capacity - headIndex;
            var bytesRead = 0;

            try
            {
                while ((bytesRead < maxBlockLength) && (messagesRead < messageCountLimit))
                {
                    var recordIndex = headIndex + bytesRead;
                    var header = GetLongVolatile(buffer, recordIndex);

                    var recordLength = RecordDescriptor.RecordLength(header);
                    if (recordLength <= 0)
                    {
                        break;
                    }

                    bytesRead += BitUtil.Align(recordLength, RecordDescriptor.Alignment);

                    var messageTypeId = RecordDescriptor.MessageTypeId(header);
                    if (_paddingMessageTypeId == messageTypeId)
                    {
                        continue;
                    }

                    ++messagesRead;
                    handler(messageTypeId, buffer, recordIndex + RecordDescriptor.HeaderLength, recordLength - RecordDescriptor.HeaderLength);
                }
            }
            finally
            {
                if (bytesRead != 0)
                {
                    SetMemory(buffer, headIndex, bytesRead, 0);
                    PutLongOrdered(buffer, _headPositionIndex, head + bytesRead);
                }
            }

            return messagesRead;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetMemory(byte* buffer, int index, int length, byte value)
        {
            CheckBounds(index, length);
            Unsafe.InitBlock(buffer + index, value, (uint) length);            
        }
    
        private void CheckMsgLength(int length)
        {
            if (length > _maxMsgLength)
            {
                var msg = $"encoded message exceeds maxMsgLength of {_maxMsgLength:D}, length={length:D}";

                throw new ArgumentException(msg);
            }
        }

        public static long GetLongVolatile(byte* buffer, int index)
        {
            return Volatile.Read(ref *(long*)(buffer + index));
        }

        public static long GetLong(byte* buffer, int index)
        {
            return *(long*)(buffer + index);
        }

        public static void PutLongOrdered(byte* buffer, long index, long value)
        {
            Volatile.Write(ref *(long*)(buffer + index), value);
        }

        public static void PutIntOrdered(byte* buffer, long index, int value)
        {
            Volatile.Write(ref *(int*)(buffer + index), value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutBytes(byte* buffer, int index, ReadOnlySpan<byte> messageBytes)
        {
            if (messageBytes.Length == 0)
            {
                return;
            }

            CheckBounds(index, messageBytes.Length);

            var destination = new Span<byte>(buffer + index, messageBytes.Length);
            messageBytes.CopyTo(destination);
        }
        
        private int ClaimCapacity(byte* buffer, int requiredCapacity)
        {
            var capacity = _capacity;
            var tailPositionIndex = _tailPositionIndex;
            var headCachePositionIndex = _headCachePositionIndex;
            var mask = capacity - 1;

            var head = GetLongVolatile(buffer, headCachePositionIndex);

            long tail;
            int tailIndex;
            int padding;
            do
            {
                tail = GetLongVolatile(buffer, tailPositionIndex);
                var availableCapacity = capacity - (int)(tail - head);

                if (requiredCapacity > availableCapacity)
                {
                    head = GetLongVolatile(buffer, _headPositionIndex);

                    if (requiredCapacity > (capacity - (int)(tail - head)))
                    {
                        return _insufficientCapacity;
                    }

                    PutLongOrdered(buffer, headCachePositionIndex, head);
                }

                padding = 0;
                tailIndex = (int)tail & mask;
                var toBufferEndLength = capacity - tailIndex;

                if (requiredCapacity > toBufferEndLength)
                {
                    var headIndex = (int)head & mask;

                    if (requiredCapacity > headIndex)
                    {
                        head = GetLongVolatile(buffer, _headPositionIndex);
                        headIndex = (int)head & mask;
                        if (requiredCapacity > headIndex)
                        {
                            return _insufficientCapacity;
                        }

                        PutLongOrdered(buffer, headCachePositionIndex, head);
                    }

                    padding = toBufferEndLength;
                }
            } while (!CompareAndSetLong(buffer, tailPositionIndex, tail, tail + requiredCapacity + padding));

            if (0 != padding)
            {
                PutLongOrdered(buffer, tailIndex, RecordDescriptor.MakeHeader(padding, _paddingMessageTypeId));
                tailIndex = 0;
            }

            return tailIndex;
        }

        public bool CompareAndSetLong(byte* buffer, int index, long expectedValue, long updateValue)
        {
            CheckBounds(index, sizeof(long));

            var original = Interlocked.CompareExchange(ref *(long*)(buffer + index), updateValue, expectedValue);

            return original == expectedValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckBounds(int index, int length)
        {
            var resultingPosition = index + (long)length;
            if (index < 0 || resultingPosition > _bufferLength)
                throw new IndexOutOfRangeException($"index={index}, length={length}, capacity={_capacity}");
        }
        
    }
    
    public unsafe delegate void MessageHandler(
        int msgTypeId,
        byte* buffer,
        int index,
        int length);
}