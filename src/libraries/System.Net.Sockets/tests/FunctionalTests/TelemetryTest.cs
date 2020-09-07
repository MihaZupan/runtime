// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    public class TelemetryTest
    {
        public readonly ITestOutputHelper _output;

        public TelemetryTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public static void EventSource_ExistsWithCorrectId()
        {
            Type esType = typeof(Socket).Assembly.GetType("System.Net.Sockets.SocketsTelemetry", throwOnError: true, ignoreCase: false);
            Assert.NotNull(esType);

            Assert.Equal("System.Net.Sockets", EventSource.GetName(esType));
            Assert.Equal(Guid.Parse("d5b2e7d4-b6ec-50ae-7cde-af89427ad21f"), EventSource.GetGuid(esType));

            Assert.NotEmpty(EventSource.GenerateManifest(esType, esType.Assembly.Location));
        }

        public static IEnumerable<object[]> SocketMethods_MemberData()
        {
            yield return new object[] { "Sync" };
            yield return new object[] { "Task" };
            yield return new object[] { "Apm" };
            yield return new object[] { "Eap" };
        }

        //[OuterLoop]
        [ConditionalTheory(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        [MemberData(nameof(SocketMethods_MemberData))]
        public void EventSource_SocketConnects_LogsConnectAcceptStartStop(string socketMethod)
        {
            RemoteExecutor.Invoke(async socketMethod =>
            {
                using (var listener = new TestEventListener("System.Net.Sockets", EventLevel.Verbose, 0.1))
                {
                    var events = new ConcurrentQueue<EventWrittenEventArgs>();
                    await listener.RunWithCallbackAsync(events.Enqueue, async () =>
                    {
                        switch (socketMethod)
                        {
                            case "Sync":
                                await new ConnectSync(null).Connect_Success(IPAddress.Loopback);
                                break;

                            case "Task":
                                await new ConnectTask(null).Connect_Success(IPAddress.Loopback);
                                break;

                            case "Apm":
                                await new ConnectApm(null).Connect_Success(IPAddress.Loopback);
                                break;

                            case "Eap":
                                await new ConnectEap(null).Connect_Success(IPAddress.Loopback);
                                break;
                        }

                        await Task.Delay(300);
                    });
                    Assert.DoesNotContain(events, ev => ev.EventId == 0); // errors from the EventSource itself

                    VerifyEvents(events, "ConnectStart", 1);

                    VerifyEvents(events, "ConnectStop", 1);

                    VerifyEventCounters(events, connectCount: 1);
                }
            }, socketMethod).Dispose();
        }

        [OuterLoop]
        [ConditionalFact(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        public void EventSource_EventsRaisedAsExpected()
        {
            RemoteExecutor.Invoke(() =>
            {
                using (var listener = new TestEventListener("System.Net.Sockets", EventLevel.Verbose, 0.1))
                {
                    var events = new ConcurrentQueue<EventWrittenEventArgs>();
                    listener.RunWithCallbackAsync(events.Enqueue, async () =>
                    {
                        // Invoke several tests to execute code paths while tracing is enabled

                        await new SendReceiveSync(null).SendRecv_Stream_TCP(IPAddress.Loopback, false).ConfigureAwait(false);
                        await new SendReceiveSync(null).SendRecv_Stream_TCP(IPAddress.Loopback, true).ConfigureAwait(false);

                        await new SendReceiveTask(null).SendRecv_Stream_TCP(IPAddress.Loopback, false).ConfigureAwait(false);
                        await new SendReceiveTask(null).SendRecv_Stream_TCP(IPAddress.Loopback, true).ConfigureAwait(false);

                        await new SendReceiveEap(null).SendRecv_Stream_TCP(IPAddress.Loopback, false).ConfigureAwait(false);
                        await new SendReceiveEap(null).SendRecv_Stream_TCP(IPAddress.Loopback, true).ConfigureAwait(false);

                        await new SendReceiveApm(null).SendRecv_Stream_TCP(IPAddress.Loopback, false).ConfigureAwait(false);
                        await new SendReceiveApm(null).SendRecv_Stream_TCP(IPAddress.Loopback, true).ConfigureAwait(false);

                        await new SendReceiveUdpClient().SendToRecvFromAsync_Datagram_UDP_UdpClient(IPAddress.Loopback).ConfigureAwait(false);
                        await new SendReceiveUdpClient().SendToRecvFromAsync_Datagram_UDP_UdpClient(IPAddress.Loopback).ConfigureAwait(false);

                        await new NetworkStreamTest().CopyToAsync_AllDataCopied(4096, true).ConfigureAwait(false);
                        await new NetworkStreamTest().Timeout_ValidData_Roundtrips().ConfigureAwait(false);
                        await Task.Delay(300).ConfigureAwait(false);
                    }).Wait();
                    Assert.DoesNotContain(events, ev => ev.EventId == 0); // errors from the EventSource itself
                    VerifyEvents(events, "ConnectStart", 10);
                    VerifyEvents(events, "ConnectStop", 10);

                    Dictionary<string, double> eventCounters = events.Where(e => e.EventName == "EventCounters").Select(e => (IDictionary<string, object>) e.Payload.Single())
                        .GroupBy(d => (string)d["Name"], d => (double)d["Mean"], (k, v) => new { Name = k, Value = v.Sum() })
                        .ToDictionary(p => p.Name, p => p.Value);

                    VerifyEventCounter("incoming-connections-established", eventCounters);
                    VerifyEventCounter("outgoing-connections-established", eventCounters);
                    VerifyEventCounter("bytes-received", eventCounters);
                    VerifyEventCounter("bytes-sent", eventCounters);
                    VerifyEventCounter("datagrams-received", eventCounters);
                    VerifyEventCounter("datagrams-sent", eventCounters);
                }
            }).Dispose();
        }

        private static void VerifyEvents(IEnumerable<EventWrittenEventArgs> events, string eventName, int expectedCount)
        {
            EventWrittenEventArgs[] starts = events.Where(e => e.EventName == eventName).ToArray();
            Assert.Equal(expectedCount, starts.Length);
        }

        private static void VerifyEventCounter(string name, Dictionary<string, double> eventCounters)
        {
            Assert.True(eventCounters.ContainsKey(name));
            Assert.True(eventCounters[name] > 0);
        }

        private static void VerifyEventCounters(ConcurrentQueue<EventWrittenEventArgs> events, int connectCount)
        {
            Dictionary<string, double[]> eventCounters = events
                .Where(e => e.EventName == "EventCounters")
                .Select(e => (IDictionary<string, object>)e.Payload.Single())
                .GroupBy(d => (string)d["Name"], d => (double)(d.ContainsKey("Mean") ? d["Mean"] : d["Increment"]))
                .ToDictionary(p => p.Key, p => p.ToArray());

            Assert.True(eventCounters.TryGetValue("outgoing-connections-established", out double[] outgoingConnections));
            Assert.Equal(connectCount, outgoingConnections[^1]);

            Assert.True(eventCounters.TryGetValue("incoming-connections-established", out double[] incomingConnections));
            Assert.Equal(connectCount, incomingConnections[^1]);
        }
    }
}
