using System.Xml;
using System.Xml.Linq;
using AlmostServiceBus.Core.Broker;

namespace AlmostServiceBus.Core.Management;

/// <summary>
/// Serializes Service Bus entities into Atom XML format compatible with the Azure SDK.
/// </summary>
public static class AtomXmlWriter
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Sb = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Counts = "http://schemas.microsoft.com/netservices/2011/06/servicebus";

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> in ISO 8601 duration format.
    /// For <see cref="TimeSpan.MaxValue"/>, returns the Azure-specific representation.
    /// </summary>
    public static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts == TimeSpan.MaxValue)
            return "P10675199DT2H48M5.4775807S";
        return XmlConvert.ToString(ts);
    }

    /// <summary>Parses a TimeSpan from an ISO 8601 duration string.</summary>
    private static TimeSpan ParseTimeSpan(string value)
    {
        if (value == "P10675199DT2H48M5.4775807S")
            return TimeSpan.MaxValue;
        return XmlConvert.ToTimeSpan(value);
    }

    // ── Queue ────────────────────────────────────────────────────────────────

    // A queue's or topic's scheduled messages are held by the scheduled message processor, not by
    // the entity, so the endpoints pass them in: entity name → messages scheduled and not yet due.

    public static string WriteQueueEntry(QueueEntity queue, string baseUrl = "", Func<string, int>? scheduled = null) =>
        SerializeToString(BuildQueueEntry(queue, baseUrl, scheduled));

    public static string WriteQueueFeed(IEnumerable<QueueEntity> queues, string baseUrl = "", Func<string, int>? scheduled = null) =>
        SerializeToString(BuildFeed(queues.Select(queue => BuildQueueEntry(queue, baseUrl, scheduled))));

    private static XElement BuildQueueEntry(QueueEntity queue, string baseUrl, Func<string, int>? scheduled)
    {
        var active = queue.ActiveMessageCount;
        var deadLetter = queue.DeadLetterMessageCount;
        var scheduledCount = scheduled?.Invoke(queue.Name) ?? 0;

        var desc = new XElement(Sb + "QueueDescription",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            Elem("LockDuration", FormatTimeSpan(queue.LockDuration)),
            Elem("MaxSizeInMegabytes", queue.MaxSizeInMegabytes),
            Elem("RequiresSession", queue.RequiresSession),
            Elem("DefaultMessageTimeToLive", FormatTimeSpan(queue.DefaultMessageTimeToLive)),
            Elem("DeadLetteringOnMessageExpiration", queue.DeadLetteringOnMessageExpiration),
            Elem("MaxDeliveryCount", queue.MaxDeliveryCount),
            Elem("EnablePartitioning", queue.EnablePartitioning),
            Elem("EnableExpress", queue.EnableExpress),
            Elem("EnableBatchedOperations", queue.EnableBatchedOperations),
            OptElem("ForwardTo", queue.ForwardTo),
            OptElem("UserMetadata", queue.UserMetadata),
            queue.AutoDeleteOnIdle.HasValue ? Elem("AutoDeleteOnIdle", FormatTimeSpan(queue.AutoDeleteOnIdle.Value)) : null,
            Elem("RequiresDuplicateDetection", queue.RequiresDuplicateDetection),
            Elem("DuplicateDetectionHistoryTimeWindow", FormatTimeSpan(queue.DuplicateDetectionHistoryTimeWindow)),
            // Azure's total counts the scheduled and dead-lettered messages as well as the active ones.
            Elem("MessageCount", active + deadLetter + scheduledCount),
            CountDetails(active, deadLetter, scheduledCount));

        return BuildEntry(queue.Name, queue.Name, desc, baseUrl);
    }

    // ── Topic ────────────────────────────────────────────────────────────────

    public static string WriteTopicEntry(TopicEntity topic, string baseUrl = "", Func<string, int>? scheduled = null) =>
        SerializeToString(BuildTopicEntry(topic, baseUrl, scheduled));

    public static string WriteTopicFeed(IEnumerable<TopicEntity> topics, string baseUrl = "", Func<string, int>? scheduled = null) =>
        SerializeToString(BuildFeed(topics.Select(t => BuildTopicEntry(t, baseUrl, scheduled))));

    private static XElement BuildTopicEntry(TopicEntity topic, string baseUrl, Func<string, int>? scheduled)
    {
        var desc = new XElement(Sb + "TopicDescription",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            Elem("DefaultMessageTimeToLive", FormatTimeSpan(topic.DefaultMessageTimeToLive)),
            Elem("MaxSizeInMegabytes", topic.MaxSizeInMegabytes),
            Elem("EnablePartitioning", topic.EnablePartitioning),
            Elem("EnableExpress", topic.EnableExpress),
            Elem("EnableBatchedOperations", topic.EnableBatchedOperations),
            Elem("EnableSubscriptionPartitioning", topic.EnableSubscriptionPartitioning),
            Elem("SupportOrdering", topic.SupportOrdering),
            OptElem("UserMetadata", topic.UserMetadata),
            topic.AutoDeleteOnIdle.HasValue ? Elem("AutoDeleteOnIdle", FormatTimeSpan(topic.AutoDeleteOnIdle.Value)) : null,
            Elem("RequiresDuplicateDetection", topic.RequiresDuplicateDetection),
            Elem("DuplicateDetectionHistoryTimeWindow", FormatTimeSpan(topic.DuplicateDetectionHistoryTimeWindow)),
            Elem("SubscriptionCount", topic.GetSubscriptions().Count),
            // A topic holds nothing but its scheduled messages: Azure counts them here, and a
            // subscription sees a scheduled message only once it is delivered.
            CountDetails(0, 0, scheduled?.Invoke(topic.Name) ?? 0));

        return BuildEntry(topic.Name, topic.Name, desc, baseUrl);
    }

    // ── Subscription ─────────────────────────────────────────────────────────

    public static string WriteSubscriptionEntry(SubscriptionEntity sub, string baseUrl = "") =>
        SerializeToString(BuildSubscriptionEntry(sub, baseUrl));

    public static string WriteSubscriptionFeed(IEnumerable<SubscriptionEntity> subs, string baseUrl = "") =>
        SerializeToString(BuildFeed(subs.Select(s => BuildSubscriptionEntry(s, baseUrl))));

    private static XElement BuildSubscriptionEntry(SubscriptionEntity sub, string baseUrl)
    {
        var active = sub.Queue.ActiveMessageCount;
        var deadLetter = sub.Queue.DeadLetterMessageCount;

        var desc = new XElement(Sb + "SubscriptionDescription",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            Elem("LockDuration", FormatTimeSpan(sub.LockDuration)),
            Elem("RequiresSession", sub.RequiresSession),
            Elem("DefaultMessageTimeToLive", FormatTimeSpan(sub.DefaultMessageTimeToLive)),
            Elem("DeadLetteringOnMessageExpiration", sub.DeadLetteringOnMessageExpiration),
            Elem("DeadLetteringOnFilterEvaluationExceptions", sub.DeadLetteringOnFilterEvaluationExceptions),
            Elem("MaxDeliveryCount", sub.MaxDeliveryCount),
            Elem("EnableBatchedOperations", sub.EnableBatchedOperations),
            Elem("Status", "Active"),
            OptElem("ForwardTo", sub.ForwardTo),
            OptElem("UserMetadata", sub.UserMetadata),
            // Emitted with a max-value default when unset so the SDK admin clients, which require
            // these fields to be present, can deserialize the entry.
            Elem("AutoDeleteOnIdle", FormatTimeSpan(sub.AutoDeleteOnIdle ?? TimeSpan.MaxValue)),
            Elem("EntityAvailabilityStatus", "Available"),
            Elem("MessageCount", active + deadLetter),
            CountDetails(active, deadLetter, 0));

        // The SDK admin clients derive the topic and subscription names from the entry id path,
        // so it must be the full "{topic}/Subscriptions/{sub}" resource path, not just the leaf name.
        return BuildEntry($"{sub.TopicName}/Subscriptions/{sub.Name}", sub.Name, desc, baseUrl);
    }

    // ── Rule ─────────────────────────────────────────────────────────────────

    public static string WriteRuleEntry(RuleEntity rule, string topicName, string subscriptionName, string baseUrl = "") =>
        SerializeToString(BuildRuleEntry(rule, topicName, subscriptionName, baseUrl));

    public static string WriteRuleFeed(IEnumerable<RuleEntity> rules, string topicName, string subscriptionName, string baseUrl = "") =>
        SerializeToString(BuildFeed(rules.Select(r => BuildRuleEntry(r, topicName, subscriptionName, baseUrl))));

    private static XElement BuildRuleEntry(RuleEntity rule, string topicName, string subscriptionName, string baseUrl)
    {
        var filterType = rule.FilterType switch
        {
            FilterType.TrueFilter => "TrueFilter",
            FilterType.FalseFilter => "FalseFilter",
            FilterType.SqlFilter => "SqlFilter",
            FilterType.CorrelationFilter => "CorrelationFilter",
            _ => "TrueFilter"
        };

        // For TrueFilter/FalseFilter the SDK uses a SqlExpression of "1=1"/"1=0" internally,
        // but the type attribute signals the filter kind.
        var filterElement = new XElement(Sb + "Filter",
            new XAttribute(Xsi + "type", filterType));

        if (rule.FilterType is FilterType.SqlFilter or FilterType.TrueFilter)
        {
            var sqlExpr = rule.FilterType == FilterType.TrueFilter ? "1=1" : rule.SqlExpression;
            if (sqlExpr is not null)
                filterElement.Add(new XElement(Sb + "SqlExpression", sqlExpr));
        }
        else if (rule.FilterType == FilterType.CorrelationFilter)
        {
            if (rule.CorrelationId is not null)
                filterElement.Add(new XElement(Sb + "CorrelationId", rule.CorrelationId));
            if (rule.Subject is not null)
                filterElement.Add(new XElement(Sb + "Label", rule.Subject));
            if (rule.To is not null)
                filterElement.Add(new XElement(Sb + "To", rule.To));
            if (rule.ReplyTo is not null)
                filterElement.Add(new XElement(Sb + "ReplyTo", rule.ReplyTo));
            if (rule.SessionId is not null)
                filterElement.Add(new XElement(Sb + "SessionId", rule.SessionId));
            if (rule.ContentType is not null)
                filterElement.Add(new XElement(Sb + "ContentType", rule.ContentType));

            if (rule.CorrelationFilterProperties is { Count: > 0 })
            {
                var propsEl = new XElement(Sb + "Properties");
                foreach (var (key, value) in rule.CorrelationFilterProperties)
                {
                    propsEl.Add(new XElement(Sb + "KeyValueOfstringanyType",
                        new XElement(Sb + "Key", key),
                        new XElement(Sb + "Value", value)));
                }
                filterElement.Add(propsEl);
            }
        }

        var actionType = rule.ActionExpression is null ? "EmptyRuleAction" : "SqlRuleAction";
        var actionElement = new XElement(Sb + "Action",
            new XAttribute(Xsi + "type", actionType));

        if (rule.ActionExpression is not null)
            actionElement.Add(new XElement(Sb + "SqlExpression", rule.ActionExpression));

        var desc = new XElement(Sb + "RuleDescription",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            filterElement,
            actionElement,
            Elem("Name", rule.Name));

        // Full "{topic}/Subscriptions/{sub}/Rules/{rule}" path so the SDK can parse the entity names.
        return BuildEntry($"{topicName}/Subscriptions/{subscriptionName}/Rules/{rule.Name}", rule.Name, desc, baseUrl);
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static XElement BuildEntry(string resourcePath, string title, XElement description, string baseUrl) 
    {
        // Strip trailing slashes to prevent "http://hostname.com//topicName"
        baseUrl = baseUrl.TrimEnd('/');
        var resourceUrl = $"{baseUrl}/{resourcePath}?api-version=2021-05";

        return new XElement(Atom + "entry",
            new XElement(Atom + "id", resourceUrl),
            new XElement(Atom + "title", new XAttribute("type", "text"), title),
            new XElement(Atom + "author",
                new XElement(Atom + "name", "almost-service-bus")
            ),
            new XElement(Atom + "link", 
                new XAttribute("rel", "self"), 
                new XAttribute("href", resourceUrl)
            ),
            new XElement(Atom + "content", new XAttribute("type", "application/xml"), description)
        );
    }

    private static XElement BuildFeed(IEnumerable<XElement> entries) =>
        new(Atom + "feed",
            new XElement(Atom + "title", "Entities"),
            entries);

    private static XElement Elem(string localName, object value) =>
        new(Sb + localName, value is bool b ? b.ToString().ToLowerInvariant() : value);

    /// <summary>
    /// The runtime counts the SDK reads into <c>QueueRuntimeProperties</c>,
    /// <c>TopicRuntimeProperties</c> and <c>SubscriptionRuntimeProperties</c>, in Azure's shape.
    /// Nothing is ever in transfer here, so the transfer counts are zero.
    /// </summary>
    private static XElement CountDetails(int active, int deadLetter, int scheduled) =>
        new(Sb + "CountDetails",
            new XAttribute(XNamespace.Xmlns + "d2p1", Counts.NamespaceName),
            new XElement(Counts + "ActiveMessageCount", active),
            new XElement(Counts + "DeadLetterMessageCount", deadLetter),
            new XElement(Counts + "ScheduledMessageCount", scheduled),
            new XElement(Counts + "TransferMessageCount", 0),
            new XElement(Counts + "TransferDeadLetterMessageCount", 0));

    private static XElement? OptElem(string localName, string? value) =>
        value is null ? null : new XElement(Sb + localName, value);

    private static string SerializeToString(XElement element)
    {
        var sw = new StringWriter();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
        };
        using (var writer = XmlWriter.Create(sw, settings))
        {
            element.WriteTo(writer);
        }
        return sw.ToString();
    }
}
