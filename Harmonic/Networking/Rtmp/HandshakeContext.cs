﻿using Harmonic.Networking.Utils;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Harmonic.Networking.Rtmp
{
    sealed class HandshakeContext : IDisposable
    {
        private uint _readerTimestampEpoch = 0;
        private uint _writerTimestampEpoch = 0;
        private ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        private Random _random = new Random();
        private byte[] _s1Data = null;
        private byte[] _c1Data = null;
        private IOPipeLine _ioPipeline = null;

        public HandshakeContext(IOPipeLine ioPipeline)
        {
            _ioPipeline = ioPipeline;
            _ioPipeline._bufferProcessors.Add(ProcessState.HandshakeC0C1, ProcessHandshakeC0C1);
            _ioPipeline._bufferProcessors.Add(ProcessState.HandshakeC2, ProcessHandshakeC2);
        }

        public void Dispose()
        {
            if (_s1Data != null)
            {
                _arrayPool.Return(_s1Data);
            }
            if (_c1Data != null)
            {
                _arrayPool.Return(_c1Data);
            }
        }

        private bool ProcessHandshakeC0C1(ReadOnlySequence<byte> buffer, ref int consumed)
        {
            if (buffer.Length - consumed < 1537)
            {
                return false;
            }
            var arr = _arrayPool.Rent(1537);

            buffer.Slice(consumed, 1537).CopyTo(arr);
            consumed += 1537;
            var version = arr[0];

            if (version < 3)
            {
                throw new NotSupportedException();
            }
            if (version > 31)
            {
                throw new ProtocolViolationException();
            }

            _readerTimestampEpoch = NetworkBitConverter.ToUInt32(arr.AsSpan(1, 4));
            _writerTimestampEpoch = 0;
            var allZero = arr.AsSpan(5, 4);
            if (allZero[0] != 0 || allZero[1] != 0 || allZero[2] != 0 || allZero[3] != 0)
            {
                throw new ProtocolViolationException();
            }
            _c1Data = _arrayPool.Rent(1528);

            arr.AsSpan(9).CopyTo(_c1Data);
            _s1Data = _arrayPool.Rent(1528);
            _random.NextBytes(_s1Data.AsSpan(0, 1528));

            arr.AsSpan().Clear();
            arr[0] = 3;
            NetworkBitConverter.TryGetBytes(_writerTimestampEpoch, arr.AsSpan(1, 4));
            _s1Data.AsSpan(0, 1528).CopyTo(arr.AsSpan(9));
            _ioPipeline.SendRawData(arr, 1537);

            _ioPipeline._nextProcessState = ProcessState.HandshakeC2;
            return true;
        }

        private bool ProcessHandshakeC2(ReadOnlySequence<byte> buffer, ref int consumed)
        {
            if (buffer.Length - consumed < 1536)
            {
                return false;
            }
            byte[] arr = _arrayPool.Rent(1536);
            try
            {
                buffer.Slice(consumed, 1536).CopyTo(arr);
                consumed += 1536;
                var s1Timestamp = NetworkBitConverter.ToUInt32(arr.AsSpan(0, 4));
                if (s1Timestamp != _writerTimestampEpoch)
                {
                    throw new ProtocolViolationException();
                }
                if (!arr.AsSpan(8, 1528).SequenceEqual(_s1Data.AsSpan(0, 1528)))
                {
                    throw new ProtocolViolationException();
                }

                NetworkBitConverter.TryGetBytes(_readerTimestampEpoch, arr.AsSpan(0, 4));
                NetworkBitConverter.TryGetBytes((uint)0, arr.AsSpan(4, 4));
                _c1Data.AsSpan(0, 1528).CopyTo(arr.AsSpan(8));
                _ioPipeline.SendRawData(arr, 1536);
                _ioPipeline.OnHandshakeSuccessful();
                return true;
            }
            finally
            {
                _arrayPool.Return(_c1Data);
                _arrayPool.Return(_s1Data);
                _s1Data = null;
                _c1Data = null;
            }

        }
    }
}