using AlmostServiceBus.Core.Broker;

namespace AlmostServiceBus.Tests.Broker;

public class TopicEntityTests
{
    private static BrokeredMessage CreateMessage(string? body = null)
    {
        return new BrokeredMessage
        {
            Body = System.Text.Encoding.UTF8.GetBytes(body ?? "hello")
        };
    }

    [Fact]
    public void Properties_HaveDefaults()
    {
        var topic = new TopicEntity("my-topic");

        Assert.Equal("my-topic", topic.Name);
        Assert.Equal(1024L, topic.MaxSizeInMegabytes);
        Assert.Equal(TimeSpan.MaxValue, topic.DefaultMessageTimeToLive);
        Assert.True(topic.EnableBatchedOperations);
        Assert.Null(topic.UserMetadata);
    }

    [Fact]
    public void AddSubscription_ReturnsExistingIfAlreadyExists()
    {
        var topic = new TopicEntity("my-topic");

        var sub1 = topic.AddSubscription("sub1");
        var sub2 = topic.AddSubscription("sub1");

        Assert.Same(sub1, sub2);
    }

    [Fact]
    public void GetSubscription_ReturnsNullIfNotFound()
    {
        var topic = new TopicEntity("my-topic");

        var result = topic.GetSubscription("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void RemoveSubscription_RemovesIt()
    {
        var topic = new TopicEntity("my-topic");
        topic.AddSubscription("sub1");

        topic.RemoveSubscription("sub1");

        Assert.Null(topic.GetSubscription("sub1"));
    }

    [Fact]
    public void Publish_FansOutToAllSubscriptions()
    {
        var topic = new TopicEntity("my-topic");
        var subA = topic.AddSubscription("subA");
        var subB = topic.AddSubscription("subB");

        topic.Publish(CreateMessage("fan-out"));

        var msgA = subA.Queue.TryDequeueImmediate();
        var msgB = subB.Queue.TryDequeueImmediate();

        Assert.NotNull(msgA);
        Assert.NotNull(msgB);
    }

    [Fact]
    public void Publish_ClonesMessagePerSubscription()
    {
        var topic = new TopicEntity("my-topic");
        var subA = topic.AddSubscription("subA");
        var subB = topic.AddSubscription("subB");

        topic.Publish(CreateMessage("clone-test"));

        var msgA = subA.Queue.TryDequeueImmediate();
        var msgB = subB.Queue.TryDequeueImmediate();

        Assert.NotNull(msgA);
        Assert.NotNull(msgB);
        Assert.NotSame(msgA, msgB);
    }

    [Fact]
    public void Publish_WithForwardTo_RoutesToTargetQueue()
    {
        var topic = new TopicEntity("my-topic");
        var targetQueue = new QueueEntity("target-queue");
        var sub = topic.AddSubscription("sub1");
        sub.ForwardTo = "target-queue";
        sub.ResolvedForwardToQueue = targetQueue;

        topic.Publish(CreateMessage("forwarded"));

        var msgInTarget = targetQueue.TryDequeueImmediate();
        var msgInOwn = sub.Queue.TryDequeueImmediate();

        Assert.NotNull(msgInTarget);
        Assert.Null(msgInOwn);
    }

    // ── Sessionless fan-out ──────────────────────────────────────────────────

    [Fact]
    public void Publish_SessionlessMessage_DeadLettersAtSessionSubscription_AndDeliversElsewhere()
    {
        var topic = new TopicEntity("mixed-topic");
        var session = topic.AddSubscription("session-sub");
        session.RequiresSession = true;
        var plain = topic.AddSubscription("plain-sub");

        // Azure accepts the publish and dead-letters at the subscription that cannot hold the
        // message. Throwing here would fail the whole transfer and starve `plain-sub` too.
        topic.Publish(CreateMessage("no-session"));

        var dead = session.Queue.DeadLetterQueue.PeekMessages();
        var deadLettered = Assert.Single(dead);
        // The wording is Azure's own, measured on a Standard namespace; a caller that matches on
        // the reason string has to get the same answer from both brokers.
        Assert.Equal("Session id is null.", SubscriptionEntity.SessionIdIsNullReason);
        Assert.Equal(SubscriptionEntity.SessionIdIsNullReason, deadLettered.DeadLetterReason);
        Assert.Equal("Message has no session id and the entity requires a session.",
            deadLettered.DeadLetterErrorDescription);
        Assert.Equal(session.Queue.Name, deadLettered.DeadLetterSource);

        // Nothing active on the session subscription.
        Assert.Empty(session.Queue.PeekMessages());

        // The other subscription got its copy.
        var delivered = Assert.Single(plain.Queue.PeekMessages());
        Assert.Equal("no-session", System.Text.Encoding.UTF8.GetString(delivered.Body));
    }

    [Fact]
    public void Publish_MessageWithSession_ReachesSessionSubscription()
    {
        var topic = new TopicEntity("mixed-topic");
        var session = topic.AddSubscription("session-sub");
        session.RequiresSession = true;

        var message = CreateMessage("with-session");
        message.SessionId = "s1";
        topic.Publish(message);

        Assert.Empty(session.Queue.DeadLetterQueue.PeekMessages());
        Assert.Single(session.Queue.PeekMessages());
    }

    [Fact]
    public void Publish_SessionlessMessage_FilteredOut_IsNotDeadLettered()
    {
        var topic = new TopicEntity("mixed-topic");
        var session = topic.AddSubscription("session-sub");
        session.RequiresSession = true;
        session.RemoveRule("$Default");
        session.AddOrUpdateRule(new RuleEntity
        {
            Name = "r",
            FilterType = FilterType.SqlFilter,
            SqlExpression = "user.keep = 'yes'"
        });

        topic.Publish(CreateMessage("no-session"));

        // The filter decides first: a message this subscription never wanted is not its
        // dead letter.
        Assert.Empty(session.Queue.DeadLetterQueue.PeekMessages());
        Assert.Empty(session.Queue.PeekMessages());
    }

    [Fact]
    public void Publish_SessionlessMessage_ForwardedToSessionQueue_IsDeadLetteredThere()
    {
        var topic = new TopicEntity("mixed-topic");
        var target = new QueueEntity("session-queue") { RequiresSession = true };
        var subscription = topic.AddSubscription("forwarding-sub");
        subscription.ForwardTo = "session-queue";
        subscription.ResolvedForwardToQueue = target;

        topic.Publish(CreateMessage("no-session"));

        var deadLettered = Assert.Single(target.DeadLetterQueue.PeekMessages());
        Assert.Equal(SubscriptionEntity.SessionIdIsNullReason, deadLettered.DeadLetterReason);
    }

    [Fact]
    public void SessionRequiredQueue_StillRejectsADirectSend()
    {
        // Unchanged, and deliberately so: Azure rejects at the sender for a queue.
        var queue = new QueueEntity("session-queue") { RequiresSession = true };

        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(CreateMessage("no-session")));
    }
}
