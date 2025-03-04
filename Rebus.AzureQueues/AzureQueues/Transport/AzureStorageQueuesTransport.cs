﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Queue;
using Microsoft.Azure.Storage.RetryPolicies;
using Newtonsoft.Json;
using Rebus.AzureQueues.Internals;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Exceptions;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Threading;
using Rebus.Time;
using Rebus.Transport;
// ReSharper disable MethodSupportsCancellation
// ReSharper disable EmptyGeneralCatchClause
// ReSharper disable UnusedMember.Global
#pragma warning disable 1998

namespace Rebus.AzureQueues.Transport;

/// <summary>
/// Implementation of <see cref="ITransport"/> that uses Azure Storage Queues to do its thing
/// </summary>
public class AzureStorageQueuesTransport : ITransport, IInitializable, IDisposable
{
    const string QueueNameValidationRegex = "^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$";
    static readonly QueueRequestOptions ExponentialRetryRequestOptions = new() { RetryPolicy = new ExponentialRetry() };
    static readonly QueueRequestOptions DefaultQueueRequestOptions = new();
    readonly ConcurrentDictionary<string, MessageLockRenewer> _messageLockRenewers = new();
    readonly AzureStorageQueuesTransportOptions _options;
    readonly ConcurrentQueue<CloudQueueMessage> _prefetchedMessages = new();
    readonly IAsyncTask _messageLockRenewalTask;
    readonly TimeSpan _initialVisibilityDelay;
    readonly ILog _log;
    readonly ICloudQueueFactory _queueFactory;
    readonly IRebusTime _rebusTime;


    /// <summary>
    /// Constructs the transport using a <see cref="CloudStorageAccount"/>
    /// </summary>
    public AzureStorageQueuesTransport(CloudStorageAccount storageAccount, string inputQueueName, IRebusLoggerFactory rebusLoggerFactory, AzureStorageQueuesTransportOptions options, IRebusTime rebusTime, IAsyncTaskFactory asyncTaskFactory)
        : this(new CloudQueueClientQueueFactory(storageAccount.CreateCloudQueueClient()), inputQueueName, rebusLoggerFactory, options, rebusTime, asyncTaskFactory)
    {
        if (storageAccount == null) throw new ArgumentNullException(nameof(storageAccount));

    }

    /// <summary>
    /// Constructs the transport using a <see cref="ICloudQueueFactory"/>
    /// </summary>
    public AzureStorageQueuesTransport(ICloudQueueFactory queueFactory, string inputQueueName, IRebusLoggerFactory rebusLoggerFactory, AzureStorageQueuesTransportOptions options, IRebusTime rebusTime, IAsyncTaskFactory asyncTaskFactory)
    {
        if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _rebusTime = rebusTime;
        _log = rebusLoggerFactory.GetLogger<AzureStorageQueuesTransport>();
        _initialVisibilityDelay = options.InitialVisibilityDelay;
        _queueFactory = queueFactory ?? throw new ArgumentNullException(nameof(queueFactory));

        if (inputQueueName != null)
        {
            if (!Regex.IsMatch(inputQueueName, QueueNameValidationRegex))
            {
                throw new ArgumentException($"The inputQueueName {inputQueueName} is not valid - it can contain only alphanumeric characters and hyphens, and must not have 2 consecutive hyphens.", nameof(inputQueueName));
            }
            Address = inputQueueName.ToLowerInvariant();
        }
        _messageLockRenewalTask = asyncTaskFactory.Create("Peek Lock Renewal", RenewPeekLocks, prettyInsignificant: true, intervalSeconds: 10);
    }

    /// <summary>
    /// Creates a new queue with the specified address
    /// </summary>
    public void CreateQueue(string address)
    {
        if (!_options.AutomaticallyCreateQueues)
        {
            _log.Info("Transport configured to not create queue - skipping existence check and potential creation for {queueName}", address);
            return;
        }

        AsyncHelpers.RunSync(async () =>
        {
            var queue = await _queueFactory.GetQueue(address);
            await queue.CreateIfNotExistsAsync();
        });
    }

    /// <summary>
    /// Sends the given <see cref="TransportMessage"/> to the queue with the specified globally addressable name
    /// </summary>
    public async Task Send(string destinationAddress, TransportMessage transportMessage, ITransactionContext context)
    {
        var outgoingMessages = context.GetOrAdd("outgoing-messages", () =>
        {
            var messagesToSend = new ConcurrentQueue<MessageToSend>();

            context.OnCommitted(_ =>
            {
                var messagesByQueue = messagesToSend
                    .GroupBy(m => m.DestinationAddress)
                    .ToList();

                return Task.WhenAll(messagesByQueue.Select(async batch =>
                {
                    var queueName = batch.Key;
                    var queue = await _queueFactory.GetQueue(queueName);

                    await Task.WhenAll(batch.Select(async message =>
                    {
                        var headers = message.Headers.Clone();
                        var timeToLiveOrNull = GetTimeToBeReceivedOrNull(headers);
                        var visibilityDelayOrNull = GetQueueVisibilityDelayOrNull(headers);
                        var cloudQueueMessage = Serialize(headers, message.Body);

                        try
                        {
                            await queue.AddMessageAsync(
                                message: cloudQueueMessage,
                                timeToLive: timeToLiveOrNull,
                                initialVisibilityDelay: visibilityDelayOrNull,
                                options: ExponentialRetryRequestOptions,
                                operationContext: new OperationContext()
                            );
                        }
                        catch (Exception exception)
                        {
                            var errorText = $"Could not send message with ID {cloudQueueMessage.Id} to '{message.DestinationAddress}'";

                            throw new RebusApplicationException(exception, errorText);
                        }
                    }));
                }));
            });

            return messagesToSend;
        });

        var messageToSend = new MessageToSend(destinationAddress, transportMessage.Headers, transportMessage.Body);

        outgoingMessages.Enqueue(messageToSend);
    }

    class MessageToSend
    {
        public string DestinationAddress { get; }
        public Dictionary<string, string> Headers { get; }
        public byte[] Body { get; }

        public MessageToSend(string destinationAddress, Dictionary<string, string> headers, byte[] body)
        {
            DestinationAddress = destinationAddress;
            Headers = headers;
            Body = body;
        }
    }

    /// <summary>
    /// Receives the next message (if any) from the transport's input queue <see cref="ITransport.Address"/>
    /// </summary>
    public async Task<TransportMessage> Receive(ITransactionContext context, CancellationToken cancellationToken)
    {
        if (Address == null)
        {
            throw new InvalidOperationException("This Azure Storage Queues transport does not have an input queue, which means that it is configured to be a one-way client. Therefore, it is not possible to receive anything.");
        }

        var inputQueue = await _queueFactory.GetQueue(Address);

        try
        {
            return await InnerReceive(context, cancellationToken, inputQueue);
        }
        catch (StorageException exception) when (exception.InnerException is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // it's OK, we're exiting... TaskCancelledException wrapped in StorageException is just how the driver rolls
            return null;
        }
    }

    async Task<TransportMessage> InnerReceive(ITransactionContext context, CancellationToken cancellationToken, CloudQueue inputQueue)
    {
        if (!_options.Prefetch.HasValue)
        {
            // fetch single message
            var cloudQueueMessage = await inputQueue.GetMessageAsync(
                visibilityTimeout: _initialVisibilityDelay,
                options: DefaultQueueRequestOptions,
                operationContext: new OperationContext(),
                cancellationToken: cancellationToken
            );

            if (cloudQueueMessage == null) return null;

            if (_options.AutomaticPeekLockRenewalEnabled)
            {
                _messageLockRenewers.TryAdd(cloudQueueMessage.Id, new MessageLockRenewer(cloudQueueMessage, inputQueue));
            }

            SetUpCompletion(context, cloudQueueMessage, inputQueue);

            return Deserialize(cloudQueueMessage);
        }

        if (_prefetchedMessages.TryDequeue(out var dequeuedMessage))
        {
            SetUpCompletion(context, dequeuedMessage, inputQueue);
            return Deserialize(dequeuedMessage);
        }

        var cloudQueueMessages = await inputQueue.GetMessagesAsync(
            messageCount: _options.Prefetch.Value,
            visibilityTimeout: _initialVisibilityDelay,
            options: DefaultQueueRequestOptions,
            operationContext: new OperationContext(),
            cancellationToken: cancellationToken
        );

        foreach (var message in cloudQueueMessages)
        {
            _prefetchedMessages.Enqueue(message);
        }

        if (_prefetchedMessages.TryDequeue(out var newlyPrefetchedMessage))
        {
            SetUpCompletion(context, newlyPrefetchedMessage, inputQueue);
            return Deserialize(newlyPrefetchedMessage);
        }

        return null;
    }

    void SetUpCompletion(ITransactionContext context, CloudQueueMessage cloudQueueMessage, CloudQueue inputQueue)
    {
        var messageId = cloudQueueMessage.Id;

        context.OnCompleted(async _ =>
        {
            //if the message has been Automatic renewed - the popreceipt has changed since setup
            var popReceipt = _options.AutomaticPeekLockRenewalEnabled && _messageLockRenewers.TryRemove(messageId, out var updatedMessage)
                ? updatedMessage.PopReceipt
                : cloudQueueMessage.PopReceipt;

            try
            {
                // if we get this far, don't pass on the cancellation token
                // ReSharper disable once MethodSupportsCancellation
                await inputQueue.DeleteMessageAsync(
                    messageId: messageId,
                    popReceipt: popReceipt,
                    options: ExponentialRetryRequestOptions,
                    operationContext: new OperationContext()
                );
            }
            catch (Exception exception)
            {
                throw new RebusApplicationException(exception, $"Could not delete message with ID {messageId} and pop receipt {popReceipt} from the input queue");
            }
        });

        context.OnAborted(ctx =>
        {
            const MessageUpdateFields fields = MessageUpdateFields.Visibility;
            var visibilityTimeout = TimeSpan.FromSeconds(0);
            _messageLockRenewers.TryRemove(messageId, out _);
            AsyncHelpers.RunSync(async () =>
            {
                // ignore if this fails
                try
                {
                    await inputQueue.UpdateMessageAsync(cloudQueueMessage, visibilityTimeout, fields);
                }
                catch { }
            });
        });

        context.OnDisposed(ctx => _messageLockRenewers.TryRemove(messageId, out _));
    }

    static TimeSpan? GetTimeToBeReceivedOrNull(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue(Headers.TimeToBeReceived, out var timeToBeReceivedStr))
        {
            return null;
        }

        try
        {
            return TimeSpan.Parse(timeToBeReceivedStr);
        }
        catch (Exception exception)
        {
            throw new FormatException($"Could not parse the string '{timeToBeReceivedStr}' into a TimeSpan!", exception);
        }
    }

    TimeSpan? GetQueueVisibilityDelayOrNull(IDictionary<string, string> headers)
    {
        if (!_options.UseNativeDeferredMessages)
        {
            return null;
        }

        if (!headers.TryGetValue(Headers.DeferredUntil, out var deferredUntilDateTimeOffsetString))
        {
            return null;
        }

        headers.Remove(Headers.DeferredUntil);

        var enqueueTime = deferredUntilDateTimeOffsetString.ToDateTimeOffset();

        var difference = enqueueTime - _rebusTime.Now;
        if (difference <= TimeSpan.Zero) return null;
        return difference;
    }

    static CloudQueueMessage Serialize(Dictionary<string, string> headers, byte[] body)
    {
        var cloudStorageQueueTransportMessage = new CloudStorageQueueTransportMessage
        {
            Headers = headers,
            Body = body
        };

        return new CloudQueueMessage(JsonConvert.SerializeObject(cloudStorageQueueTransportMessage));
    }

    static TransportMessage Deserialize(CloudQueueMessage cloudQueueMessage)
    {
        var cloudStorageQueueTransportMessage = JsonConvert.DeserializeObject<CloudStorageQueueTransportMessage>(cloudQueueMessage.AsString);

        return new TransportMessage(cloudStorageQueueTransportMessage.Headers, cloudStorageQueueTransportMessage.Body);
    }

    class CloudStorageQueueTransportMessage
    {
        public Dictionary<string, string> Headers { get; set; }
        public byte[] Body { get; set; }
    }

    /// <inheritdoc />
    public string Address { get; }

    /// <summary>
    /// Initializes the transport by creating the input queue if necessary
    /// </summary>
    public void Initialize()
    {
        if (Address != null)
        {
            _log.Info("Initializing Azure Storage Queues transport with queue {queueName}", Address);
            CreateQueue(Address);
            if (_options.AutomaticPeekLockRenewalEnabled)
            {
                _messageLockRenewalTask.Start();
            }
            return;
        }
        _log.Info("Initializing one-way Azure Storage Queues transport");
    }

    /// <summary>
    /// Purges the input queue
    /// </summary>
    public void PurgeInputQueue()
    {
        AsyncHelpers.RunSync(async () =>
        {
            var queue = await _queueFactory.GetQueue(Address);

            if (!await queue.ExistsAsync()) return;

            _log.Info("Purging storage queue {queueName} (purging by deleting all messages)", Address);

            try
            {
                await queue.ClearAsync(ExponentialRetryRequestOptions, new OperationContext());
            }
            catch (Exception exception)
            {
                throw new RebusApplicationException(exception, "Could not purge queue");
            }
        });
    }

    async Task RenewPeekLocks()
    {
        var mustBeRenewed = _messageLockRenewers.Values
            .Where(r => r.IsDue)
            .ToList();

        if (!mustBeRenewed.Any()) return;

        _log.Debug("Found {count} peek locks to be renewed", mustBeRenewed.Count);

        await Task.WhenAll(mustBeRenewed.Select(async r =>
        {
            try
            {
                await r.Renew().ConfigureAwait(false);

                _log.Debug("Successfully renewed peek lock for message with ID {messageId}", r.MessageId);
            }
            catch (Exception exception)
            {
                _log.Warn(exception, "Error when renewing peek lock for message with ID {messageId}", r.MessageId);
            }
        }));
    }

    /// <summary>
    /// Disposes background running tasks
    /// </summary>
    public void Dispose()
    {
        _messageLockRenewalTask?.Dispose();
    }
}