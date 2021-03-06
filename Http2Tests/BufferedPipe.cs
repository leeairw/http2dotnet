﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Http2;
using Http2.Internal;
using Xunit;

namespace Http2Tests
{
    /// <summary>
    /// A pipe, which connects two tasks.
    /// On task uses the reading side an can read data from the pipe,
    /// the other task uses the writing side and can write data or close the pipe
    /// </summary>
    public interface IBufferedPipe
        : IReadableByteStream, IWriteAndCloseableByteStream
    {
    }

    public class BufferedPipe
        : IWriteableByteStream, IReadableByteStream,
          ICloseableByteStream, IWriteAndCloseableByteStream,
          IBufferedPipe
    {
        public byte[] Buffer;
        public int Written = 0;
        public bool IsClosed = false;
        AsyncManualResetEvent canRead = new AsyncManualResetEvent(false);
        AsyncManualResetEvent canWrite = new AsyncManualResetEvent(true);
        object mutex = new object();
        SemaphoreSlim writeLock = new SemaphoreSlim(1);

        public BufferedPipe(int bufferSize)
        {
            if (bufferSize < 1) throw new ArgumentException(nameof(bufferSize));
            Buffer = new byte[bufferSize];
        }

        public async ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
        {
            // Wait until read is possible
            await canRead;

            var wakeupWriter = false;
            var toCopy = 0;

            lock (mutex)
            {
                var available = Written;
                if (available == 0)
                {
                    return new StreamReadResult
                    {
                        BytesRead = 0,
                        EndOfStream = true,
                    };
                }

                toCopy = Math.Min(available, buffer.Count);
                Array.Copy(Buffer, 0, buffer.Array, buffer.Offset, toCopy);

                if (toCopy == Written)
                {
                    // Everything was consumed
                    Written = 0;
                    if (!IsClosed)
                    {
                        // Block the read only if the pipe is not closed
                        // If closed the reading part must be able to read
                        // the EndOfStream info in the next iteration.
                        canRead.Reset();
                    }
                }
                else
                {
                    // Move unconsumed data to start of array
                    var remaining = Written - toCopy;
                    Array.Copy(Buffer, toCopy, Buffer, 0, remaining);
                    Written -= toCopy;
                }

                // Determine whether to wakeup the writer
                wakeupWriter = Written != Buffer.Length;
            }

            // Wakeup the writer if he is waiting and there is free space
            if (wakeupWriter)
            {
                canWrite.Set();
            }

            return new StreamReadResult
            {
                BytesRead = toCopy,
                EndOfStream = false,
            };
        }

        public async Task WriteAsync(ArraySegment<byte> buffer)
        {
            if (buffer.Array == null) throw new ArgumentNullException(nameof(buffer));

            var offset = buffer.Offset;
            var count = buffer.Count;

            await writeLock.WaitAsync();
            try
            {

                while (count > 0)
                {
                    var segment = new ArraySegment<byte>(buffer.Array, offset, count);
                    var written = await WriteOnce(segment);
                    offset += written;
                    count -= written;
                }
            }
            finally
            {
                writeLock.Release();
            }
        }

        private async ValueTask<int> WriteOnce(ArraySegment<byte> buffer)
        {
            await canWrite;
            var writeAmount = 0;
            lock (mutex)
            {
                if (IsClosed)
                {
                    throw new Exception("Write on closed stream");
                }
                var free = Buffer.Length - Written;
                writeAmount = Math.Min(free, buffer.Count);
                Array.Copy(buffer.Array, buffer.Offset, Buffer, Written, writeAmount);
                Written += writeAmount;

                if (Written == Buffer.Length)
                {
                    // Buffer is full. Need to wait for reader next time
                    canWrite.Reset();
                }
            }

            if (writeAmount > 0)
            {
                // Allow the reader to proceed
                canRead.Set();
            }

            return writeAmount;
        }

        public Task CloseAsync()
        {
            lock (mutex)
            {
                IsClosed = true;
            }

            canRead.Set();
            canWrite.Set();
            return Task.CompletedTask;
        }
    }

    public class BufferedPipeTests
    {
        [Fact]
        public async Task WritingOnOneEndShallYieldInDataOnTheOtherEnd()
        {
            var p = new BufferedPipe(128);
            var input = new byte[70];
            var output = new byte[50];
            for (var i = 0; i < 70; i++) input[i] = (byte)i;

            await p.WriteAsync(new ArraySegment<byte>(input));

            var res = await p.ReadAsync(new ArraySegment<byte>(output));
            Assert.Equal(false, res.EndOfStream);
            Assert.Equal(50, res.BytesRead);
            for (var i = 0; i < 50; i++)
            {
                Assert.Equal(i, output[i]);
            }

            await p.CloseAsync();

            res = await p.ReadAsync(new ArraySegment<byte>(output));
            Assert.Equal(false, res.EndOfStream);
            Assert.Equal(20, res.BytesRead);
            for (var i = 0; i < 20; i++)
            {
                Assert.Equal(50+i, output[i]);
            }

            await p.CloseAsync();
            res = await p.ReadAsync(new ArraySegment<byte>(output));
            Assert.Equal(true, res.EndOfStream);
            Assert.Equal(0, res.BytesRead);
        }

        [Fact]
        public async Task ReadsShouldBlockIfNoDataIsAvailable()
        {
            var p = new BufferedPipe(128);
            var input = new byte[30];
            var output = new byte[50];
            // Fill some data first - first try should succeed
            await p.WriteAsync(new ArraySegment<byte>(input));
            var res = await p.ReadAsync(new ArraySegment<byte>(output));
            Assert.Equal(false, res.EndOfStream);
            Assert.Equal(30, res.BytesRead);

            // Try to read more data from the pipe which wil never get available
            var readTask = Task.Run(async () => await p.ReadAsync(new ArraySegment<byte>(output)));
            // Start a timer in parallel
            var timerTask = Task.Delay(20);

            var finishedTask = await Task.WhenAny(readTask, timerTask);
            Assert.Equal(timerTask, finishedTask);
            Assert.Equal(false, readTask.IsCompleted);
        }

        [Fact]
        public async Task WritesShouldUnblockReads()
        {
            var p = new BufferedPipe(128);
            var input = new byte[70];
            var output = new byte[50];
            for (var i = 0; i < 70; i++) input[i] = (byte)i;
            // Start read first
            var task = p.ReadAsync(new ArraySegment<byte>(output)).AsTask();
            // Schedule write
            var writeTask = Task.Run(async () =>
            {
                await Task.Delay(10);
                await p.WriteAsync(new ArraySegment<byte>(input));
            });
            // And wait for read to unblock
            var res = await task;
            Assert.Equal(false, res.EndOfStream);
            Assert.Equal(50, res.BytesRead);
            for (var i = 0; i < 50; i++)
            {
                Assert.Equal(i, output[i]);
            }
            // The writeTask should also finish
            await writeTask;
        }

        [Fact]
        public async Task CloseShouldUnblockReads()
        {
            var p = new BufferedPipe(128);
            var input = new byte[70];
            var output = new byte[50];
            for (var i = 0; i < 70; i++) input[i] = (byte)i;
            // Start read first
            var task = p.ReadAsync(new ArraySegment<byte>(output)).AsTask();
            // Schedule close
            var writeTask = Task.Run(async () =>
            {
                await Task.Delay(10);
                await p.CloseAsync();
            });
            // And wait for read to unblock
            var res = await task;
            Assert.Equal(true, res.EndOfStream);
            Assert.Equal(0, res.BytesRead);
            // The writeTask should also finish
            await writeTask;
        }

        [Fact]
        public async Task CloseShouldAllowRemainingDataToBeReadBeforeSignalClose()
        {
            var p = new BufferedPipe(128);
            var input = new byte[20];
            var output = new byte[10];
            for (var i = 0; i < 20; i++) input[i] = (byte)i;
            await p.WriteAsync(new ArraySegment<byte>(input));
            await p.CloseAsync();

            var res = await p.ReadAsync(new ArraySegment<byte>(output));
            Assert.Equal(false, res.EndOfStream);
            Assert.Equal(10, res.BytesRead);
            for (var i = 0; i < 10; i++) Assert.Equal(i, output[i]);

            res = await p.ReadAsync(new ArraySegment<byte>(output));
            Assert.Equal(false, res.EndOfStream);
            Assert.Equal(10, res.BytesRead);
            for (var i = 0; i < 10; i++) Assert.Equal(i+10, output[i]);

            res = await p.ReadAsync(new ArraySegment<byte>(output));
            Assert.Equal(true, res.EndOfStream);
            Assert.Equal(0, res.BytesRead);
        }

        [Fact]
        public async Task WritesShouldBlockIfBufferIsFull()
        {
            var p = new BufferedPipe(128);
            var input = new byte[50];
            var output = new byte[50];
            // Fill pipe with two writes
            await p.WriteAsync(new ArraySegment<byte>(input));
            await p.WriteAsync(new ArraySegment<byte>(input));

            // Try to write more data from the pipe which is full
            var writeTask = Task.Run(async () => await p.WriteAsync(new ArraySegment<byte>(input)));
            // Start a timer in parallel
            var timerTask = Task.Delay(20);

            var finishedTask = await Task.WhenAny(writeTask, timerTask);
            Assert.Equal(timerTask, finishedTask);
            Assert.Equal(false, writeTask.IsCompleted);
        }

        [Fact]
        public async Task ReadsShouldUnblockWrites()
        {
            var p = new BufferedPipe(70);
            var input = new byte[70];
            var input2 = new byte[50];
            var output = new byte[60];
            for (var i = 0; i < 70; i++) input[i] = (byte)i;

            // Fill the complete buffer
            await p.WriteAsync(new ArraySegment<byte>(input));
            var writeMoreTask = p.WriteAsync(new ArraySegment<byte>(input2));
            // Read in the background to unblock
            var readTask = Task.Run(async () =>
            {
                await Task.Delay(10);
                return await p.ReadAsync(new ArraySegment<byte>(output)).AsTask();
            });
            // The next line would deadlock without the reader in the background
            await writeMoreTask;
            var res = await readTask;
            Assert.Equal(false, res.EndOfStream);
            Assert.Equal(60, res.BytesRead);
            for (var i = 0; i < 60; i++)
            {
                Assert.Equal(i, output[i]);
            }
        }
    }
}
