using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using AlmostServiceBus.TestHost;

namespace AlmostServiceBus.SdkIntegration.Tests;

/// <summary>
/// The counts the SDK's runtime properties report, in the shape a real namespace reports them
/// (measured on a Standard namespace, 23 September 2026): a scheduled message counts on its queue
/// or on its topic until it is delivered, and a subscription sees it only then. The management
/// API used to send no counts at all, so every one read zero.
/// </summary>
public class RuntimePropertiesTests : IAsyncLifetime
{
    private readonly ServiceBusEmulatorFixture _fixture = new();
    private readonly HttpClient _http = new();
    private ServiceBusAdministrationClient _admin = null!;

    public async Task InitializeAsync()
    {
        await _fixture.StartAsync();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        _admin = new ServiceBusAdministrationClient(_fixture.ConnectionString,
            new ServiceBusAdministrationClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _fixture.DisposeAsync();
    }

    private ServiceBusClient CreateClient() => new(
        _fixture.ConnectionString,
        new ServiceBusClientOptions
        {
            TransportType = ServiceBusTransportType.AmqpTcp,
            RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0, TryTimeout = TimeSpan.FromSeconds(10) },
        });

    [Fact]
    public async Task Queue_CountsActiveScheduledAndDeadLetteredMessages()
    {
        await _admin.CreateQueueAsync("counts-queue");
        await using var client = CreateClient();
        var sender = client.CreateSender("counts-queue");
        await sender.SendMessageAsync(new ServiceBusMessage("active"));
        await sender.SendMessageAsync(new ServiceBusMessage("dead"));
        var seq = await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddHours(1));

        var receiver = client.CreateReceiver("counts-queue");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(received);
        await receiver.DeadLetterMessageAsync(received);

        var props = (await _admin.GetQueueRuntimePropertiesAsync("counts-queue")).Value;
        Assert.Equal(1, props.ActiveMessageCount);
        Assert.Equal(1, props.ScheduledMessageCount);
        Assert.Equal(1, props.DeadLetterMessageCount);
        Assert.Equal(3, props.TotalMessageCount);

        await sender.CancelScheduledMessageAsync(seq);
        props = (await _admin.GetQueueRuntimePropertiesAsync("counts-queue")).Value;
        Assert.Equal(0, props.ScheduledMessageCount);
        Assert.Equal(2, props.TotalMessageCount);
    }

    [Fact]
    public async Task Topic_CountsScheduledMessagesUntilTheSubscriptionHasThem()
    {
        await _admin.CreateTopicAsync("counts-topic");
        await _admin.CreateSubscriptionAsync("counts-topic", "all");
        await using var client = CreateClient();
        var sender = client.CreateSender("counts-topic");
        await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddHours(1));
        await sender.ScheduleMessageAsync(new ServiceBusMessage("soon"), DateTimeOffset.UtcNow.AddSeconds(1));

        var topic = (await _admin.GetTopicRuntimePropertiesAsync("counts-topic")).Value;
        Assert.Equal(2, topic.ScheduledMessageCount);
        Assert.Equal(1, topic.SubscriptionCount);
        Assert.Equal(0, (await _admin.GetSubscriptionRuntimePropertiesAsync("counts-topic", "all")).Value.ActiveMessageCount);

        var receiver = client.CreateReceiver("counts-topic", "all", new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
        var delivered = await receiver.PeekMessageAsync();
        for (var i = 0; delivered is null && i < 50; i++)
        {
            await Task.Delay(100);
            delivered = await receiver.PeekMessageAsync();
        }
        Assert.Equal("soon", delivered?.Body.ToString());

        topic = (await _admin.GetTopicRuntimePropertiesAsync("counts-topic")).Value;
        Assert.Equal(1, topic.ScheduledMessageCount);
        var sub = (await _admin.GetSubscriptionRuntimePropertiesAsync("counts-topic", "all")).Value;
        Assert.Equal(1, sub.ActiveMessageCount);
        Assert.Equal(1, sub.TotalMessageCount);
    }

    [Fact]
    public async Task Dashboard_CountsScheduledMessages()
    {
        await _admin.CreateQueueAsync("dash-queue");
        await _admin.CreateTopicAsync("dash-topic");
        await using var client = CreateClient();
        await client.CreateSender("dash-queue").ScheduleMessageAsync(new ServiceBusMessage("q"), DateTimeOffset.UtcNow.AddHours(1));
        await client.CreateSender("dash-topic").ScheduleMessageAsync(new ServiceBusMessage("t"), DateTimeOffset.UtcNow.AddHours(1));

        var overview = await _http.GetFromJsonAsync<JsonElement>(
            $"http://localhost:{_fixture.PublicPort}/api/dashboard/namespaces/{_fixture.Namespace}/entities");
        var queue = overview.GetProperty("queues").EnumerateArray().Single(q => q.GetProperty("name").GetString() == "dash-queue");
        var topic = overview.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "dash-topic");
        Assert.Equal(1, queue.GetProperty("scheduledCount").GetInt32());
        Assert.Equal(0, queue.GetProperty("messageCount").GetInt32());
        Assert.Equal(1, topic.GetProperty("scheduledCount").GetInt32());
    }
}
