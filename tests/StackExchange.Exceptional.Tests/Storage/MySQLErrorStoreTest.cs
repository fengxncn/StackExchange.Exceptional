﻿using System.Runtime.CompilerServices;
using Dapper;
using MySqlConnector;
using StackExchange.Exceptional.Stores;
using Xunit;
using Xunit.Abstractions;

namespace StackExchange.Exceptional.Tests.Storage;

public class MySQLErrorStoreTest : StoreBaseTest, IClassFixture<MySqlFixture>
{
    public static string ConnectionString => TestConfig.Current.MySQLConnectionString;
    private MySqlFixture Fixture { get; }

    public MySQLErrorStoreTest(MySqlFixture fixture, ITestOutputHelper output) : base(output)
    {
        Fixture = fixture;
        if (Fixture.ShouldSkip)
        {
            Skip.Inconclusive("Couldn't test against: " + ConnectionString + "\n" + fixture.SkipReason);
        }
    }

    protected override ErrorStore GetStore([CallerMemberName]string appName = null) =>
        new MySQLErrorStore(new ErrorStoreSettings
        {
            ConnectionString = ConnectionString,
            ApplicationName = appName,
            TableName = Fixture.TableName
        });
}

public class MySqlFixture : IDisposable
{
    public bool ShouldSkip { get; }
    public string SkipReason { get; }
    public string TableName { get; }
    public string TableScript { get; }
    private string ConnectionString { get; }

    public MySqlFixture()
    {
        Skip.IfNoConfig(nameof(TestConfig.Current.MySQLConnectionString), TestConfig.Current.MySQLConnectionString);
        try
        {
            var script = Resource.Get("Scripts.MySQL.sql");
            var csb = new MySqlConnectionStringBuilder(TestConfig.Current.MySQLConnectionString)
            {
                ConnectionTimeout = 2000
            };
            ConnectionString = csb.ConnectionString;
            using var conn = new MySqlConnection(ConnectionString);
            TableName = string.Concat("Test", Guid.NewGuid().ToString("N").AsSpan(24));
            TableScript = script.Replace("Exceptions", TableName);
            conn.Execute(TableScript);
        }
        catch (Exception e)
        {
            e.MaybeLog(TestConfig.Current.MySQLConnectionString);
            ShouldSkip = true;
            SkipReason = e.Message;
        }
    }

    public void Dispose()
    {
        try
        {
            using (var conn = new MySqlConnection(ConnectionString))
            {
                conn.Execute("Drop Table " + TableName);
            }
            GC.SuppressFinalize(this);
        }
        catch when (ShouldSkip)
        {
            // if we didn't error initially then we'll throw
        }
    }
}
