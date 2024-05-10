﻿using System.Runtime.CompilerServices;
using MongoDB.Driver;
using StackExchange.Exceptional.Stores;
using Xunit;
using Xunit.Abstractions;

namespace StackExchange.Exceptional.Tests.Storage;

public class MongoDBErrorStoreTest : StoreBaseTest, IClassFixture<MongoDBFixture>
{
    public static string ConnectionString => TestConfig.Current.MongoDBConnectionString;
    private MongoDBFixture Fixture { get; }

    public MongoDBErrorStoreTest(MongoDBFixture fixture, ITestOutputHelper output) : base(output)
    {
        Fixture = fixture;
        if (Fixture.ShouldSkip)
        {
            Skip.Inconclusive("Couldn't test against: " + ConnectionString + "\n" + fixture.SkipReason);
        }
    }

    protected override ErrorStore GetStore([CallerMemberName]string appName = null) =>
        new MongoDBErrorStore(new ErrorStoreSettings
        {
            ConnectionString = Fixture.ConnectionString,
            ApplicationName = appName,
            TableName = Fixture.TableName
        });
}

public class MongoDBFixture : IDisposable
{
    public bool ShouldSkip { get; }
    public string SkipReason { get; }
    public string ConnectionString { get; }
    public string TableName { get; }
    public IMongoDatabase MongoDatabase { get; }

    public MongoDBFixture()
    {
        Skip.IfNoConfig(nameof(TestConfig.Current.MongoDBConnectionString), TestConfig.Current.MongoDBConnectionString);
        ConnectionString = TestConfig.Current.MongoDBConnectionString;
        TableName = string.Concat("Test", Guid.NewGuid().ToString("N").AsSpan(24));
        try
        {
            var databaseName = new MongoUrl(ConnectionString).DatabaseName;
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ConnectTimeout = settings.SocketTimeout = settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            var database = new MongoClient(settings).GetDatabase(databaseName);
            MongoDatabase = database;
            database.ListCollections();
        }
        catch (Exception e)
        {
            e.MaybeLog(TestConfig.Current.MongoDBConnectionString);
            ShouldSkip = true;
            SkipReason = e.Message;
        }
    }

    public void Dispose()
    {
        try
        {
            MongoDatabase.DropCollection(TableName);
            GC.SuppressFinalize(this);
        }
        catch when (ShouldSkip)
        {
            // if we didn't error initially then we'll throw
        }
    }
}
