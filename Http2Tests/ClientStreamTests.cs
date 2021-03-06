using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;
using static Http2Tests.TestHeaders;

namespace Http2Tests
{
    public class ClientStreamTests
    {
        private ILoggerProvider loggerProvider;

        public ClientStreamTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        public static class StreamCreator
        {
            public struct Result
            {
                public Encoder hEncoder;
                public Connection conn;
                public IStream stream;
            }

            public static async Task<Result> CreateConnectionAndStream(
                StreamState state,
                ILoggerProvider loggerProvider,
                IBufferedPipe iPipe, IBufferedPipe oPipe,
                Settings? localSettings = null,
                Settings? remoteSettings = null,
                HuffmanStrategy huffmanStrategy = HuffmanStrategy.Never)
            {
                if (state == StreamState.Idle)
                {
                    throw new Exception("Not supported");
                }

                var hEncoder = new Encoder();
                var conn = await ConnectionUtils.BuildEstablishedConnection(
                    false, iPipe, oPipe, loggerProvider, null,
                    localSettings: localSettings,
                    remoteSettings: remoteSettings,
                    huffmanStrategy: huffmanStrategy);

                var endOfStream = false;
                if (state == StreamState.HalfClosedLocal ||
                    state == StreamState.Closed)
                    endOfStream = true;
                var stream = await conn.CreateStreamAsync(
                    DefaultGetHeaders, endOfStream: endOfStream);
                await oPipe.ReadAndDiscardHeaders(1u, endOfStream);

                if (state == StreamState.HalfClosedRemote ||
                    state == StreamState.Closed)
                {
                    var outBuf = new byte[Settings.Default.MaxFrameSize];
                    var result = hEncoder.EncodeInto(
                        new ArraySegment<byte>(outBuf),
                        DefaultStatusHeaders);
                    await iPipe.WriteFrameHeaderWithTimeout(
                        new FrameHeader
                        {
                            Type = FrameType.Headers,
                            Flags = (byte)(HeadersFrameFlags.EndOfHeaders |
                                           HeadersFrameFlags.EndOfStream),
                            StreamId = 1u,
                            Length = result.UsedBytes,
                        });
                    await iPipe.WriteAsync(new ArraySegment<byte>(outBuf, 0, result.UsedBytes));
                    var readHeadersTask = stream.ReadHeadersAsync();
                    var combined = await Task.WhenAny(readHeadersTask, Task.Delay(
                        ReadableStreamTestExtensions.ReadTimeout));
                    Assert.True(readHeadersTask == combined, "Expected to receive headers");
                    var headers = await readHeadersTask;
                    Assert.True(headers.SequenceEqual(DefaultStatusHeaders));
                    // Consume the data - which should be empty
                    var data = await stream.ReadAllToArrayWithTimeout();
                    Assert.Equal(0, data.Length);
                }
                else if (state == StreamState.Reset)
                {
                    await iPipe.WriteResetStream(1u, ErrorCode.Cancel);
                    await Assert.ThrowsAsync<StreamResetException>(
                        () => stream.ReadHeadersAsync());
                }

                return new Result
                {
                    hEncoder = hEncoder,
                    conn = conn,
                    stream = stream,
                };
            }
        }

        [Theory]
        [InlineData(StreamState.Open)]
        [InlineData(StreamState.HalfClosedLocal)]
        [InlineData(StreamState.HalfClosedRemote)]
        [InlineData(StreamState.Closed)]
        [InlineData(StreamState.Reset)]
        public async Task StreamCreatorShouldCreateStreamInCorrectState(
            StreamState state)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await StreamCreator.CreateConnectionAndStream(
                state, loggerProvider, inPipe, outPipe);
            Assert.NotNull(res.stream);
            Assert.Equal(state, res.stream.State);
        }

        [Fact]
        public async Task CreatingStreamShouldEmitHeaders()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var conn = await ConnectionUtils.BuildEstablishedConnection(
                false, inPipe, outPipe, loggerProvider);

            var stream1Task = conn.CreateStreamAsync(DefaultGetHeaders, false);
            var stream1 = await stream1Task;
            Assert.Equal(StreamState.Open, stream1.State);
            Assert.Equal(1u, stream1.Id);

            var fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(1u, fh.StreamId);
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal((byte)HeadersFrameFlags.EndOfHeaders, fh.Flags);
            Assert.Equal(EncodedDefaultGetHeaders.Length, fh.Length);
            var hdrData = new byte[fh.Length];
            await outPipe.ReadWithTimeout(new ArraySegment<byte>(hdrData));
            Assert.Equal(EncodedDefaultGetHeaders, hdrData);

            var stream3Task = conn.CreateStreamAsync(DefaultGetHeaders, true);
            var stream3 = await stream3Task;
            Assert.Equal(StreamState.HalfClosedLocal, stream3.State);
            Assert.Equal(3u, stream3.Id);

            fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(3u, fh.StreamId);
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal(
                (byte)(HeadersFrameFlags.EndOfHeaders | HeadersFrameFlags.EndOfStream),
                fh.Flags);
            Assert.Equal(EncodedIndexedDefaultGetHeaders.Length, fh.Length);
            var hdrData3 = new byte[fh.Length];
            await outPipe.ReadWithTimeout(new ArraySegment<byte>(hdrData3));
            Assert.Equal(EncodedIndexedDefaultGetHeaders, hdrData3);
        }

        [Theory]
        [InlineData(100)]
        public async Task CreatedStreamsShouldAlwaysUseIncreasedStreamIds(
            int nrStreams)
        {
            // This test checks if there are race conditions in the stream
            // establish code
            var inPipe = new BufferedPipe(10*1024);
            var outPipe = new BufferedPipe(10*1024);
            var conn = await ConnectionUtils.BuildEstablishedConnection(
                false, inPipe, outPipe, loggerProvider);

            var createStreamTasks = new Task<IStream>[nrStreams];
            for (var i = 0; i < nrStreams; i++)
            {
                // Create the task in the threadpool
                var t = Task.Run(
                    () => conn.CreateStreamAsync(DefaultGetHeaders, false));
                createStreamTasks[i] = t;
            }

            // Wait until all streams are open
            await Task.WhenAll(createStreamTasks);
            var streams = createStreamTasks.Select(t => t.Result).ToList();

            // Check output data
            // Sequence IDs must be always increasing
            var buffer = new byte[Settings.Default.MaxFrameSize];
            for (var i = 0; i < nrStreams; i++)
            {
                var expectedId = 1u + 2*i;
                var fh = await outPipe.ReadFrameHeaderWithTimeout();
                Assert.Equal(expectedId, fh.StreamId);
                Assert.Equal(FrameType.Headers, fh.Type);
                Assert.Equal((byte)HeadersFrameFlags.EndOfHeaders, fh.Flags);
                // Discard header data
                await outPipe.ReadWithTimeout(
                    new ArraySegment<byte>(buffer, 0, fh.Length));
            }
        }

        [Fact]
        public async Task ReceivingDataBeforeHeadersShouldYieldAResetException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider,
                inPipe, outPipe);

            await inPipe.WriteData(1u, 1);
            await outPipe.AssertResetStreamReception(1u, ErrorCode.ProtocolError);
            var ex = await Assert.ThrowsAsync<AggregateException>(
                () => res.stream.ReadWithTimeout(new ArraySegment<byte>(
                    new byte[1])));
            Assert.IsType<StreamResetException>(ex.InnerException);
            Assert.Equal(StreamState.Reset, res.stream.State);
        }

        [Fact]
        public async Task ReceivingResetShouldYieldAResetException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var conn = await ConnectionUtils.BuildEstablishedConnection(
                false, inPipe, outPipe, loggerProvider);
            IStream stream = await conn.CreateStreamAsync(DefaultGetHeaders);
            await outPipe.ReadAndDiscardHeaders(1u, false);

            var readTask = stream.ReadWithTimeout(new ArraySegment<byte>(new byte[1]));
            await inPipe.WriteResetStream(1u, ErrorCode.Cancel);
            var ex = await Assert.ThrowsAsync<AggregateException>(
                () => readTask);
            Assert.IsType<StreamResetException>(ex.InnerException);
            Assert.Equal(StreamState.Reset, stream.State);
        }

        [Fact]
        public async Task CreatingStreamsOnServerConnectionShouldYieldException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var conn = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider);
            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => conn.CreateStreamAsync(DefaultGetHeaders));
            Assert.Equal("Streams can only be created for clients", ex.Message);
        }

        [Fact]
        public async Task ClientsShouldBeAbleToReceiveInformationalHeaders()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Send and receive first set of informational headers
            var readInfoHeaders1Task = res.stream.ReadHeadersAsync();
            Assert.False(readInfoHeaders1Task.IsCompleted);

            var infoHeaders1 = new HeaderField[]
            {
                new HeaderField { Name = ":status", Value = "100" },
                new HeaderField { Name = "extension-field", Value = "bar" },
            };
            await inPipe.WriteHeaders(res.hEncoder, 1u, false, infoHeaders1);

            var recvdInfoHeaders1 = await readInfoHeaders1Task;
            Assert.True(infoHeaders1.SequenceEqual(recvdInfoHeaders1));

            // Send and receive second set of informational headers
            var readInfoHeaders2Task = res.stream.ReadHeadersAsync();
            Assert.False(readInfoHeaders2Task.IsCompleted);

            var infoHeaders2 = new HeaderField[]
            {
                new HeaderField { Name = ":status", Value = "108" },
                new HeaderField { Name = "extension-field-b", Value = "bar2" },
            };
            await inPipe.WriteHeaders(res.hEncoder, 1u, false, infoHeaders2);

            var recvdInfoHeaders2 = await readInfoHeaders2Task;
            Assert.True(infoHeaders2.SequenceEqual(recvdInfoHeaders2));

            // Send and receive final headers
            var recvHeadersTask = res.stream.ReadHeadersAsync();
            Assert.False(recvHeadersTask.IsCompleted);
            await inPipe.WriteHeaders(res.hEncoder, 1u, true, DefaultStatusHeaders);
            var recvdHeaders = await recvHeadersTask;
            Assert.True(DefaultStatusHeaders.SequenceEqual(recvdHeaders));
        }

        [Fact]
        public async Task ReceivingAnInformationalHeaderAfterANormalHeaderShouldBeAnError()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            await inPipe.WriteHeaders(res.hEncoder, 1u, false, DefaultStatusHeaders);
            // Expect to receive the status headers
            var recvdHeaders = await res.stream.ReadHeadersAsync();
            Assert.True(DefaultStatusHeaders.SequenceEqual(recvdHeaders));

            // Send informational headers to the client
            var infoHeaders = new HeaderField[]
            {
                new HeaderField { Name = ":status", Value = "100" },
                new HeaderField { Name = "extension-field", Value = "bar" },
            };
            await inPipe.WriteHeaders(res.hEncoder, 1u, false, infoHeaders);

            // Expect to receive an error
            await outPipe.AssertResetStreamReception(1u, ErrorCode.ProtocolError);
            Assert.Equal(StreamState.Reset, res.stream.State);

            await Assert.ThrowsAsync<StreamResetException>(
                () => res.stream.ReadHeadersAsync());
        }

        [Fact]
        public async Task ReceivingDataDirectlyAfterInformationalHeadersShouldBeAnError()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Send informational headers to the client
            var infoHeaders = new HeaderField[]
            {
                new HeaderField { Name = ":status", Value = "100" },
                new HeaderField { Name = "extension-field", Value = "bar" },
            };
            await inPipe.WriteHeaders(res.hEncoder, 1u, false, infoHeaders);
            var recvdHeaders = await res.stream.ReadHeadersAsync();
            Assert.True(infoHeaders.SequenceEqual(recvdHeaders));

            // Try to send data
            await inPipe.WriteData(1u, 100, null, true);
            // Expect to receive an error
            await outPipe.AssertResetStreamReception(1u, ErrorCode.ProtocolError);
            Assert.Equal(StreamState.Reset, res.stream.State);

            await Assert.ThrowsAsync<StreamResetException>(
                () => res.stream.ReadHeadersAsync());
        }
    }
}