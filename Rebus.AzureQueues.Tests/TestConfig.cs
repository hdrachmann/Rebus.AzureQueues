﻿using System;
using System.IO;
using Microsoft.Azure.Storage;
using Rebus.AzureQueues.Transport;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Threading.SystemThreadingTimer;
using Rebus.Time;

namespace Rebus.AzureQueues.Tests;

public static class AzureConfig
{
    public static CloudStorageAccount StorageAccount => CloudStorageAccount.Parse(ConnectionString);

    public static string ConnectionString => ConnectionStringFromFileOrNull(Path.Combine(GetBaseDirectory(), "azure_storage_connection_string.txt"))
                                             ?? ConnectionStringFromEnvironmentVariable("rebus2_storage_connection_string")
                                             ?? "UseDevelopmentStorage=true";

    static string GetBaseDirectory() => AppContext.BaseDirectory;

    static string ConnectionStringFromFileOrNull(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Could not find file {filePath}");
            return null;
        }

        Console.WriteLine($"Using Azure Storage connection string from file {filePath}");
        return File.ReadAllText(filePath);
    }

    static string ConnectionStringFromEnvironmentVariable(string environmentVariableName)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariableName);

        if (value == null)
        {
            Console.WriteLine($"Could not find env variable {environmentVariableName}");
            return null;
        }

        Console.WriteLine($"Using Azure Storage connection string from env variable {environmentVariableName}");

        return value;
    }

    public static void PurgeQueue(string queueName) => new AzureStorageQueuesTransport(
            StorageAccount,
            queueName,
            new NullLoggerFactory(),
            new AzureStorageQueuesTransportOptions(),
            new DefaultRebusTime(),
            new SystemThreadingTimerAsyncTaskFactory(new NullLoggerFactory())
        )
        .PurgeInputQueue();
}