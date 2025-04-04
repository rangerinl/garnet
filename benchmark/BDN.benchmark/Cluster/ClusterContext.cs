﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using BDN.benchmark.CustomProcs;
using Embedded.server;
using Garnet.common;
using Garnet.server;

namespace BDN.benchmark.Cluster
{
    unsafe class ClusterContext
    {
        EmbeddedRespServer server;
        RespServerSession session;
        readonly BenchUtils benchUtils = new();
        readonly int port = 7000;

        public static ReadOnlySpan<byte> keyTag => "{0}"u8;
        public Request[] singleGetSet;
        public Request[] singleMGetMSet;
        public Request singleCTXNSET;

        public void Dispose()
        {
            session.Dispose();
            server.Dispose();
        }

        public void SetupSingleInstance(bool disableSlotVerification = false)
        {
            var opt = new GarnetServerOptions
            {
                QuietMode = true,
                EnableCluster = !disableSlotVerification,
                EndPoints = [new IPEndPoint(IPAddress.Loopback, port)],
                CleanClusterConfig = true,
                ClusterAnnounceEndpoint = new IPEndPoint(IPAddress.Loopback, port)
            };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                opt.CheckpointDir = "/tmp";
            server = new EmbeddedRespServer(opt);
            session = server.GetRespSession();
            _ = server.Register.NewTransactionProc(CustomTxnSet.CommandName, () => new CustomTxnSet(), new RespCommandsInfo { Arity = CustomTxnSet.Arity });
        }

        public void AddSlotRange(List<(int, int)> slotRanges)
        {
            foreach (var slotRange in slotRanges)
            {
                var clusterAddSlotsRange = Encoding.ASCII.GetBytes($"*4\r\n$7\r\nCLUSTER\r\n$13\r\nADDSLOTSRANGE\r\n" +
                    $"${NumUtils.CountDigits(slotRange.Item1)}\r\n{slotRange.Item1}\r\n" +
                    $"${NumUtils.CountDigits(slotRange.Item2)}\r\n{slotRange.Item2}\r\n");
                fixed (byte* req = clusterAddSlotsRange)
                    _ = session.TryConsumeMessages(req, clusterAddSlotsRange.Length);
            }
        }

        public void CreateGetSet(int keySize = 8, int valueSize = 32, int batchSize = 100)
        {
            var pairs = new (byte[], byte[])[batchSize];
            for (var i = 0; i < batchSize; i++)
            {
                pairs[i] = (new byte[keySize], new byte[valueSize]);

                keyTag.CopyTo(pairs[i].Item1.AsSpan());
                benchUtils.RandomBytes(ref pairs[i].Item1, startOffset: keyTag.Length);
                benchUtils.RandomBytes(ref pairs[i].Item2);
            }

            var setByteCount = batchSize * ("*3\r\n$3\r\nSET\r\n"u8.Length + 1 + NumUtils.CountDigits(keySize) + 2 + keySize + 2 + 1 + NumUtils.CountDigits(valueSize) + 2 + valueSize + 2);
            var setReq = new Request(setByteCount);
            var curr = setReq.ptr;
            var end = curr + setReq.buffer.Length;
            for (var i = 0; i < batchSize; i++)
            {
                _ = RespWriteUtils.TryWriteArrayLength(3, ref curr, end);
                _ = RespWriteUtils.TryWriteBulkString("SET"u8, ref curr, end);
                _ = RespWriteUtils.TryWriteBulkString(pairs[i].Item1, ref curr, end);
                _ = RespWriteUtils.TryWriteBulkString(pairs[i].Item2, ref curr, end);
            }

            var getByteCount = batchSize * ("*2\r\n$3\r\nGET\r\n"u8.Length + 1 + NumUtils.CountDigits(keySize) + 2 + keySize + 2);
            var getReq = new Request(getByteCount);
            curr = getReq.ptr;
            end = curr + getReq.buffer.Length;
            for (var i = 0; i < batchSize; i++)
            {
                _ = RespWriteUtils.TryWriteArrayLength(2, ref curr, end);
                _ = RespWriteUtils.TryWriteBulkString("GET"u8, ref curr, end);
                _ = RespWriteUtils.TryWriteBulkString(pairs[i].Item1, ref curr, end);
            }
            singleGetSet = [getReq, setReq];
        }

        public void CreateMGetMSet(int keySize = 8, int valueSize = 32, int batchSize = 100)
        {
            var pairs = new (byte[], byte[])[batchSize];
            for (var i = 0; i < batchSize; i++)
            {
                pairs[i] = (new byte[keySize], new byte[valueSize]);

                keyTag.CopyTo(pairs[i].Item1.AsSpan());
                benchUtils.RandomBytes(ref pairs[i].Item1, startOffset: keyTag.Length);
                benchUtils.RandomBytes(ref pairs[i].Item2);
            }

            var mGetHeaderSize = 1 + NumUtils.CountDigits(1 + batchSize) + 2 + "$4\r\nMGET\r\n"u8.Length;
            var getRespSize = 1 + NumUtils.CountDigits(keySize) + 2 + keySize + 2;
            var mGetByteCount = mGetHeaderSize + (batchSize * getRespSize);
            var mGetReq = new Request(mGetByteCount);

            var curr = mGetReq.ptr;
            var end = curr + mGetReq.buffer.Length;
            _ = RespWriteUtils.TryWriteArrayLength(1 + batchSize, ref curr, end);
            _ = RespWriteUtils.TryWriteBulkString("MGET"u8, ref curr, end);
            for (var i = 0; i < batchSize; i++)
                _ = RespWriteUtils.TryWriteBulkString(pairs[i].Item1, ref curr, end);

            var mSetHeaderSize = 1 + NumUtils.CountDigits(1 + (batchSize * 2)) + 2 + "$4\r\nMSET\r\n"u8.Length;
            var setRespSize = 1 + NumUtils.CountDigits(keySize) + 2 + keySize + 2 + 1 + NumUtils.CountDigits(valueSize) + 2 + valueSize + 2;
            var mSetByteCount = mSetHeaderSize + (batchSize * setRespSize);
            var mSetReq = new Request(mSetByteCount);

            curr = mSetReq.ptr;
            end = curr + mSetReq.buffer.Length;
            _ = RespWriteUtils.TryWriteArrayLength(1 + (batchSize * 2), ref curr, end);
            _ = RespWriteUtils.TryWriteBulkString("MSET"u8, ref curr, end);
            for (var i = 0; i < batchSize; i++)
            {
                _ = RespWriteUtils.TryWriteBulkString(pairs[i].Item1, ref curr, end);
                _ = RespWriteUtils.TryWriteBulkString(pairs[i].Item2, ref curr, end);
            }
            singleMGetMSet = [mGetReq, mSetReq];
        }

        public void CreateCTXNSET(int keySize = 8, int batchSize = 100)
        {
            var keys = new byte[8][];
            for (var i = 0; i < 8; i++)
            {
                keys[i] = new byte[keySize];

                keyTag.CopyTo(keys[i].AsSpan());
                benchUtils.RandomBytes(ref keys[i], startOffset: keyTag.Length);
            }

            var ctxnsetByteCount = "*9\r\n$7\r\nCTXNSET\r\n"u8.Length + (8 * (1 + NumUtils.CountDigits(keySize) + 2 + keySize + 2));
            var ctxnsetReq = new Request(batchSize * ctxnsetByteCount);
            var curr = ctxnsetReq.ptr;
            var end = curr + ctxnsetReq.buffer.Length;

            for (var i = 0; i < batchSize; i++)
            {
                _ = RespWriteUtils.TryWriteArrayLength(9, ref curr, end);
                _ = RespWriteUtils.TryWriteBulkString("CTXNSET"u8, ref curr, end);
                for (var j = 0; j < 8; j++)
                {
                    _ = RespWriteUtils.TryWriteBulkString(keys[j], ref curr, end);
                }
            }

            singleCTXNSET = ctxnsetReq;
        }

        public void Consume(byte* ptr, int length)
            => session.TryConsumeMessages(ptr, length);
    }

}