using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace QuantConnect.Algorithm.CSharp.Core.IO
{
    /// <summary>
    /// Live configuration deltas over RabbitMQ.
    ///
    /// Publish a JSON object of key/value pairs to the exchange <c>calc_engine</c> with a routing
    /// key starting <c>config.</c>:
    ///
    ///     {"MaxDesiredOrders": 8, "SomeFlag": true, "Nested.Thing": 12.5}
    ///
    /// Semantics:
    ///  - DELTAS, never a full config: only the keys present are touched, everything else stays.
    ///  - One message may carry many pairs, and the batch is applied ATOMICALLY: if any key fails
    ///    to resolve or convert, nothing in that message is applied and the reason is logged.
    ///  - Primitives only (number, string, bool, arrays of those, enums by name or number). JSON
    ///    null is rejected: the config has no nullable values, and "unset" has no meaning here —
    ///    send the value you want.
    ///  - Keys resolve case-insensitively against the algorithm's config object; a dotted path
    ///    (<c>Nested.Key</c>) reaches into a nested config object.
    ///  - Application happens on the ALGORITHM thread, at the next <c>OnData</c>, never from the
    ///    AMQP consumer thread: a settings object read mid-cycle never changes under the cycle.
    ///  - If the message carries an AMQP <c>reply_to</c>, the outcome is published there as
    ///    <c>{"ok":bool,"applied":[...],"rejected":[...]}</c>.
    ///
    /// This is a separate connection from <see cref="RabbitMQClient"/> on purpose: that one owns an
    /// exclusive reply queue bound to every response, and a second consumer on it would steal or
    /// duplicate its traffic.
    /// </summary>
    public sealed class ConfigUpdateChannel
    {
        private const string ExchangeIn = "calc_engine";
        private const string ExchangeOut = "calc_engine.responses";
        private const string QueueName = "lean.config.update";
        private const string BindingKey = "config.#";
        private const int MaxQueuedMessages = 256;

        private static readonly JsonSerializerOptions ChannelJsonOptions = new()
        {
            IncludeFields = true,                       // schedule config classes carry fields, not properties
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,   // "1" as well as 1 for byte/decimal
            ReadCommentHandling = JsonCommentHandling.Skip,               // the config files carry comments
            AllowTrailingCommas = true,
        };

        private readonly Foundations _algo;
        private readonly ConcurrentQueue<PendingUpdate> _pending = new();
        private IConnection _connection;
        private IChannel _channel;

        private readonly struct PendingUpdate
        {
            public PendingUpdate(JsonDocument document, string replyTo, string routingKey)
            {
                Document = document;
                ReplyTo = replyTo;
                RoutingKey = routingKey;
            }

            public JsonDocument Document { get; }
            public string ReplyTo { get; }
            public string RoutingKey { get; }
        }

        public ConfigUpdateChannel(Foundations algo)
        {
            _algo = algo ?? throw new ArgumentNullException(nameof(algo));
        }

        /// <summary>Connects in the background; a broker that is down never blocks Initialize.</summary>
        public void Start(string host, int port, string user, string pass, string virtualHost = "/")
        {
            _ = Task.Run(() => ConnectAsync(host, port, user, pass, virtualHost));
        }

        private async Task ConnectAsync(string host, int port, string user, string pass, string virtualHost)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = host,
                    Port = port,
                    UserName = user,
                    Password = pass,
                    VirtualHost = virtualHost,
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                    RequestedHeartbeat = TimeSpan.FromSeconds(60),
                    ClientProvidedName = "lean-config-update"
                };

                _connection = await factory.CreateConnectionAsync("lean-config-update");
                _channel = await _connection.CreateChannelAsync();

                await _channel.ExchangeDeclareAsync(ExchangeIn, ExchangeType.Topic, durable: true);
                await _channel.ExchangeDeclareAsync(ExchangeOut, ExchangeType.Topic, durable: true);
                await _channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false);
                await _channel.QueueBindAsync(QueueName, ExchangeIn, BindingKey);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += OnReceivedAsync;
                await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer);

                _algo.Log($"{_algo.Time} ConfigUpdateChannel: listening on {ExchangeIn}/{BindingKey} " +
                          $"(queue {QueueName}, vhost {virtualHost}).");
            }
            catch (Exception error)
            {
                _algo.Error($"{_algo.Time} ConfigUpdateChannel: connect failed — live config updates are " +
                            $"disabled this run: {error.Message}");
            }
        }

        private Task OnReceivedAsync(object sender, BasicDeliverEventArgs args)
        {
            var body = Encoding.UTF8.GetString(args.Body.Span);
            JsonDocument document = null;
            try
            {
                document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("payload must be a JSON object of key/value pairs");
                }

                if (_pending.Count >= MaxQueuedMessages)
                {
                    _algo.Error($"{_algo.Time} ConfigUpdateChannel: queue full ({MaxQueuedMessages}), " +
                                $"dropping message on {args.RoutingKey}: {Truncate(body)}");
                    document.Dispose();
                }
                else
                {
                    _pending.Enqueue(new PendingUpdate(document, args.BasicProperties?.ReplyTo, args.RoutingKey));
                    document = null; // ownership passed to the algorithm thread
                }
            }
            catch (Exception error)
            {
                _algo.Error($"{_algo.Time} ConfigUpdateChannel: rejected message on {args.RoutingKey}: " +
                            $"{error.Message}; body={Truncate(body)}");
                document?.Dispose();
                // A malformed message will never parse: dead-letter it rather than redelivering.
                _ = _channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false);
                return Task.CompletedTask;
            }

            _ = _channel.BasicAckAsync(args.DeliveryTag, multiple: false);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Applies queued deltas. Call from the algorithm thread (OnData); returns how many messages
        /// were applied so the caller can log a count if it wants.
        /// </summary>
        public int ApplyPending()
        {
            var applied = 0;
            while (_pending.TryDequeue(out var update))
            {
                using (update.Document)
                {
                    applied += Apply(update);
                }
            }
            return applied;
        }

        private int Apply(PendingUpdate update)
        {
            var config = _algo.Cfg;
            var changes = new List<(PropertyInfo Property, object Owner, object Value, string Key, object Old)>();
            var errors = new List<string>();

            foreach (var entry in update.Document.RootElement.EnumerateObject())
            {
                var resolved = Resolve(config, entry.Name);
                if (resolved == null)
                {
                    errors.Add($"{entry.Name}: no such setting");
                    continue;
                }

                var (owner, property) = resolved.Value;
                if (!property.CanWrite)
                {
                    errors.Add($"{entry.Name}: read-only");
                    continue;
                }
                if (entry.Value.ValueKind == JsonValueKind.Null)
                {
                    errors.Add($"{entry.Name}: null is not supported — send the value you want");
                    continue;
                }
                object value;
                string conversionError;
                if (IsStringKeyedDictionary(property.PropertyType) &&
                    entry.Value.ValueKind == JsonValueKind.Object)
                {
                    // Dictionaries are MERGED, not replaced: this channel carries deltas, never a
                    // full config, and a full replacement would silently drop per-ticker overrides
                    // that the sender did not mention (e.g. only "\"_\": 5" in the payload).
                    if (!TryMergeDictionary(property.PropertyType, property.GetValue(owner), entry.Value,
                                             out value, out conversionError))
                    {
                        errors.Add($"{entry.Name}: {conversionError}");
                        continue;
                    }
                }
                else if (!TryConvert(entry.Value, property.PropertyType, out value, out conversionError))
                {
                    errors.Add($"{entry.Name}: {conversionError}");
                    continue;
                }

                changes.Add((property, owner, value, entry.Name, property.GetValue(owner)));
            }

            if (errors.Count > 0)
            {
                _algo.Error($"{_algo.Time} ConfigUpdateChannel: rejected batch, nothing applied — " +
                            $"{string.Join("; ", errors)}");
                PublishOutcome(update.ReplyTo, false, Array.Empty<string>(), errors);
                return 0;
            }

            var failed = new List<string>();
            foreach (var change in changes)
            {
                try
                {
                    change.Property.SetValue(change.Owner, change.Value);
                }
                catch (Exception error)
                {
                    failed.Add($"{change.Key}: {error.Message}");
                }
            }

            var appliedKeys = changes.Where(c => failed.All(f => !f.StartsWith(c.Key + ":", StringComparison.Ordinal)))
                                     .Select(c => c.Key)
                                     .ToList();
            var summary = string.Join(", ", changes.Select(c => $"{c.Key}={Format(c.Value)} (was {Format(c.Old)})"));

            if (failed.Count == 0)
            {
                _algo.Log($"{_algo.Time} ConfigUpdateChannel: applied {changes.Count} key(s) from {update.RoutingKey}: {summary}");
            }
            else
            {
                _algo.Error($"{_algo.Time} ConfigUpdateChannel: applied with errors from {update.RoutingKey}: " +
                            $"{summary}; errors: {string.Join("; ", failed)}");
            }

            PublishOutcome(update.ReplyTo, failed.Count == 0, appliedKeys, failed);
            return appliedKeys.Count;
        }

        private void PublishOutcome(string replyTo, bool ok, IReadOnlyCollection<string> applied, IReadOnlyCollection<string> rejected)
        {
            if (string.IsNullOrEmpty(replyTo) || _channel == null)
            {
                return;
            }

            try
            {
                var payload = JsonSerializer.Serialize(new { ok, applied, rejected });
                _ = _channel.BasicPublishAsync(ExchangeOut, replyTo, Encoding.UTF8.GetBytes(payload));
            }
            catch (Exception error)
            {
                _algo.Error($"{_algo.Time} ConfigUpdateChannel: could not publish outcome to {replyTo}: {error.Message}");
            }
        }

        // ── Resolution and conversion ────────────────────────────────────────────

        private static (object Owner, PropertyInfo Property)? Resolve(object root, string path)
        {
            var parts = path.Split('.');
            object current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var step = FindProperty(current.GetType(), parts[i]);
                if (step == null)
                {
                    return null;
                }
                current = step.GetValue(current);
                if (current == null)
                {
                    return null;
                }
            }

            var property = FindProperty(current.GetType(), parts[^1]);
            return property == null ? null : (current, property);
        }

        private static bool IsStringKeyedDictionary(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            type.GetGenericArguments()[0] == typeof(string);

        /// Merge the JSON object's entries into a copy of `current`, converting each value to the
        /// dictionary's value type. Keys absent from the payload are preserved.
        private static bool TryMergeDictionary(Type type, object current, JsonElement payload,
                                               out object value, out string error)
        {
            value = null;
            error = null;
            var valueType = type.GetGenericArguments()[1];
            var merged = (IDictionary)Activator.CreateInstance(type);
            if (current is IDictionary existing)
            {
                foreach (DictionaryEntry entry in existing)
                {
                    merged[entry.Key] = entry.Value;
                }
            }
            foreach (var item in payload.EnumerateObject())
            {
                if (!TryConvert(item.Value, valueType, out var converted, out var conversionError))
                {
                    error = $"{item.Name}: {conversionError}";
                    return false;
                }
                merged[item.Name] = converted;
            }
            value = merged;
            return true;
        }

        private static PropertyInfo FindProperty(Type type, string name) =>
            type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        private static bool TryConvert(JsonElement element, Type target, out object value, out string error)
        {
            value = null;
            error = null;

            try
            {
                var underlying = Nullable.GetUnderlyingType(target);
                if (underlying != null)
                {
                    target = underlying;
                }

                if (target == typeof(string))
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        error = "expected a string";
                        return false;
                    }
                    value = element.GetString();
                    return true;
                }

                if (target == typeof(bool))
                {
                    if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
                    {
                        value = element.GetBoolean();
                        return true;
                    }
                    if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsedBool))
                    {
                        value = parsedBool;
                        return true;
                    }
                    error = "expected a bool";
                    return false;
                }

                if (target.IsEnum)
                {
                    if (element.ValueKind == JsonValueKind.String &&
                        Enum.TryParse(target, element.GetString(), ignoreCase: true, out var parsedEnum))
                    {
                        value = parsedEnum;
                        return true;
                    }
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var enumNumber))
                    {
                        value = Enum.ToObject(target, enumNumber);
                        return true;
                    }
                    error = $"expected one of {string.Join("/", Enum.GetNames(target))}";
                    return false;
                }

                if (target == typeof(DateTime))
                {
                    if (element.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(element.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var parsedDate))
                    {
                        value = parsedDate;
                        return true;
                    }
                    error = "expected an ISO-8601 date";
                    return false;
                }

                if (target == typeof(TimeSpan))
                {
                    if (element.ValueKind == JsonValueKind.String &&
                        TimeSpan.TryParse(element.GetString(), CultureInfo.InvariantCulture, out var parsedSpan))
                    {
                        value = parsedSpan;
                        return true;
                    }
                    error = "expected a time span (hh:mm:ss)";
                    return false;
                }

                if (element.ValueKind == JsonValueKind.Object)
                {
                    // Config classes such as HedgeScheduleCfg / SweepScheduleCfg expose public FIELDS
                    // (the file is read by Newtonsoft, which binds fields by default) — System.Text.Json
                    // needs IncludeFields to do the same. This is also what lets a whole dictionary of
                    // schedules be published over the channel, e.g.
                    // {"HedgeSchedules": {"_": [{MarketRegime, Start, End, Direction, EquityHedgeMode}]}}.
                    try
                    {
                        value = JsonSerializer.Deserialize(element.GetRawText(), target, ChannelJsonOptions);
                        if (value == null)
                        {
                            error = "object payload deserialized to null";
                            return false;
                        }
                        return true;
                    }
                    catch (JsonException exception)
                    {
                        error = exception.Message;
                        return false;
                    }
                }

                if (target.IsArray || (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>)))
                {
                    if (element.ValueKind != JsonValueKind.Array)
                    {
                        error = "expected an array";
                        return false;
                    }
                    var elementType = target.IsArray ? target.GetElementType() : target.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!TryConvert(item, elementType, out var converted, out error))
                        {
                            return false;
                        }
                        list.Add(converted);
                    }
                    if (target.IsArray)
                    {
                        var array = Array.CreateInstance(elementType, list.Count);
                        list.CopyTo(array, 0);
                        value = array;
                    }
                    else
                    {
                        value = list;
                    }
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Number || element.ValueKind == JsonValueKind.String)
                {
                    var raw = element.ValueKind == JsonValueKind.Number ? element.GetRawText() : element.GetString();
                    value = Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"unsupported JSON {element.ValueKind} for {target.Name}";
                return false;
            }
            catch (Exception exception)
            {
                value = null;
                error = exception.Message;
                return false;
            }
        }

        private static string Format(object value) => value switch
        {
            null => "null",
            string text => text,
            // Dictionaries default to their type name; show the entries so the applied/old pair in
            // the log is actually readable for settings like MaxOptionOrderQuantity.
            IDictionary dictionary => "{" + string.Join(", ",
                dictionary.Cast<DictionaryEntry>().Select(e => $"{e.Key}={Format(e.Value)}")) + "}",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        private static string Truncate(string text) =>
            text.Length <= 200 ? text : text.Substring(0, 200) + "…";
    }
}
