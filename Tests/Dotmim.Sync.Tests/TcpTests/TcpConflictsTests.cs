﻿using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.MariaDB;
using Dotmim.Sync.MySql;
using Dotmim.Sync.PostgreSql;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.SqlServer;
using Dotmim.Sync.SqlServer.Manager;
using Dotmim.Sync.Tests.Core;
using Dotmim.Sync.Tests.Fixtures;
using Dotmim.Sync.Tests.Misc;
using Dotmim.Sync.Tests.Models;
using Dotmim.Sync.Web.Client;
using Dotmim.Sync.Web.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
#if NET5_0 || NET6_0 || NET7_0 || NETCOREAPP3_1
using MySqlConnector;
#elif NETCOREAPP2_1
using MySql.Data.MySqlClient;
#endif

using Newtonsoft.Json;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Dotmim.Sync.Tests.IntegrationTests
{

    public abstract class TcpConflictsTests : DatabaseTest, IClassFixture<DatabaseServerFixture>, IDisposable
    {
        private CoreProvider serverProvider;
        private IEnumerable<CoreProvider> clientsProvider;
        private SyncSetup setup;

        public TcpConflictsTests(ITestOutputHelper output, DatabaseServerFixture fixture) : base(output, fixture)
        {
            serverProvider = GetServerProvider();
            clientsProvider = GetClientProviders();
            setup = GetSetup();
        }

        private async Task CheckProductCategoryRows(CoreProvider clientProvider, string nameShouldStartWith = null)
        {
            // check rows count on server and on each client
            using var ctx = new AdventureWorksContext(serverProvider);
            // get all product categories
            var serverPC = await ctx.ProductCategory.AsNoTracking().ToListAsync();

            using var cliCtx = new AdventureWorksContext(clientProvider);
            // get all product categories
            var clientPC = await cliCtx.ProductCategory.AsNoTracking().ToListAsync();

            // check row count
            Assert.Equal(serverPC.Count, clientPC.Count);

            foreach (var cpc in clientPC)
            {
                var spc = serverPC.First(pc => pc.ProductCategoryId == cpc.ProductCategoryId);

                // check column value
                Assert.Equal(spc.ProductCategoryId, cpc.ProductCategoryId);
                Assert.Equal(spc.Name, cpc.Name);

                if (!string.IsNullOrEmpty(nameShouldStartWith))
                    Assert.StartsWith(nameShouldStartWith, cpc.Name);

            }
        }


        private async Task Resolve_Client_UniqueKeyError_WithDelete(SqlSyncProvider sqlSyncProvider)
        {
            var sqlConnection = new SqlConnection(sqlSyncProvider.ConnectionString);
            var (serverProviderType, _) = HelperDatabase.GetDatabaseType(sqlSyncProvider);

            var subcatrandom = Path.GetRandomFileName();
            var categoryName = string.Concat("A_", string.Concat(subcatrandom.Where(c => char.IsLetter(c))).ToUpperInvariant());

            var pcName = serverProviderType == ProviderType.Sql ? "[SalesLT].[ProductCategory]" : "[ProductCategory]";

            var commandText = $"DELETE FROM {pcName} WHERE [ProductCategoryID]='Z_02';";

            var command = sqlConnection.CreateCommand();
            command.CommandText = commandText;
            sqlConnection.Open();
            await command.ExecuteNonQueryAsync();
            sqlConnection.Close();

        }

        private async Task Update_Client_UniqueKeyError(SqlSyncProvider sqlSyncProvider)
        {
            var sqlConnection = new SqlConnection(sqlSyncProvider.ConnectionString);
            var (serverProviderType, _) = HelperDatabase.GetDatabaseType(sqlSyncProvider);

            var subcatrandom = Path.GetRandomFileName();
            var categoryName = string.Concat("A_", string.Concat(subcatrandom.Where(c => char.IsLetter(c))).ToUpperInvariant());

            var pcName = serverProviderType == ProviderType.Sql ? "[SalesLT].[ProductCategory]" : "[ProductCategory]";

            var commandText = $"UPDATE {pcName} SET [Name] = [Name];";

            var command = sqlConnection.CreateCommand();
            command.CommandText = commandText;
            sqlConnection.Open();
            await command.ExecuteNonQueryAsync();
            sqlConnection.Close();

        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableRaiseAnError()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                var (clientProviderType, clientDatabaseName) = HelperDatabase.GetDatabaseType(clientProvider);

                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z1{str}")
                                row["Name"] = $"Z2{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });


                var exc = await Assert.ThrowsAsync<SyncException>(() => agent.SynchronizeAsync(setup));
                Assert.NotNull(exc);

                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();
                Assert.Empty(batchInfos);
            }
        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableContinueOnError()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // make a first sync to init the two databases
            foreach (var clientProvider in clientsProvider)
                await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(setup);

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z1{str}")
                                row["Name"] = $"Z2{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });

                agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
                {
                    // Continue On Error
                    args.Resolution = ErrorResolution.ContinueOnError;
                    Assert.NotNull(args.Exception);
                    Assert.NotNull(args.ErrorRow);
                    Assert.NotNull(args.SchemaTable);
                    Assert.Equal(SyncRowState.Modified, args.ApplyType);
                });

                var s = await agent.SynchronizeAsync(setup);

                Assert.Equal(2, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                var batchInfo = batchInfos[0];

                var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
                }

                s = await agent.SynchronizeAsync(setup);

                Assert.Equal(0, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnClient);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                batchInfo = batchInfos[0];

                syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
                }
            }
        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableRetryOneMoreTimeAndThrowOnError()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // make a first sync to init the two databases
            foreach (var clientProvider in clientsProvider)
                await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(setup);

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z1{str}")
                                row["Name"] = $"Z2{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });

                agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
                {
                    // Continue On Error
                    args.Resolution = ErrorResolution.RetryOneMoreTimeAndThrowOnError;
                    Assert.NotNull(args.Exception);
                    Assert.NotNull(args.ErrorRow);
                    Assert.NotNull(args.SchemaTable);
                    Assert.Equal(SyncRowState.Modified, args.ApplyType);
                });

                var exc = await Assert.ThrowsAsync<SyncException>(() => agent.SynchronizeAsync(setup));
                Assert.NotNull(exc);
                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();
                Assert.Empty(batchInfos);

                exc = await Assert.ThrowsAsync<SyncException>(() => agent.SynchronizeAsync(setup));
                Assert.NotNull(exc);
                batchInfos = agent.LocalOrchestrator.LoadBatchInfos();
                Assert.Empty(batchInfos);
            }
        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableRetryOneMoreTimeAndContinueOnError()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // make a first sync to init the two databases
            foreach (var clientProvider in clientsProvider)
                await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(setup);

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z1{str}")
                                row["Name"] = $"Z2{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });


                agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
                {
                    // Continue On Error
                    args.Resolution = ErrorResolution.RetryOneMoreTimeAndContinueOnError;
                    Assert.NotNull(args.Exception);
                    Assert.NotNull(args.ErrorRow);
                    Assert.NotNull(args.SchemaTable);
                    Assert.Equal(SyncRowState.Modified, args.ApplyType);
                });

                var s = await agent.SynchronizeAsync(setup);

                Assert.Equal(2, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                var batchInfo = batchInfos[0];

                var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
                }
            }
        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableRetryOnNextSync()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // make a first sync to init the two databases
            foreach (var clientProvider in clientsProvider)
                await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(setup);

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z1{str}")
                                row["Name"] = $"Z2{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });

                agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
                {
                    // Continue On Error
                    args.Resolution = ErrorResolution.RetryOnNextSync;
                    Assert.NotNull(args.Exception);
                    Assert.NotNull(args.ErrorRow);
                    Assert.NotNull(args.SchemaTable);
                    Assert.Equal(SyncRowState.Modified, args.ApplyType);
                });

                var s = await agent.SynchronizeAsync(setup);

                Assert.Equal(2, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERROR", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                var batchInfo = batchInfos[0];

                var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
                }

                s = await agent.SynchronizeAsync(setup);

                Assert.Equal(0, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(0, s.TotalChangesAppliedOnClient);
                Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERROR", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                batchInfo = batchInfos[0];

                syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
                }
            }
        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableRetryOnNextSyncWithResolve()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // make a first sync to init the two databases
            foreach (var clientProvider in clientsProvider)
                await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(setup);

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                var interceptorId = agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z2{str}")
                                row["Name"] = $"Z1{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });

                agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
                {
                    // Continue On Error
                    args.Resolution = ErrorResolution.RetryOnNextSync;
                    Assert.NotNull(args.Exception);
                    Assert.NotNull(args.ErrorRow);
                    Assert.NotNull(args.SchemaTable);
                    Assert.Equal(SyncRowState.Modified, args.ApplyType);
                });

                var s = await agent.SynchronizeAsync(setup);

                Assert.Equal(2, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERROR", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                var batchInfo = batchInfos[0];

                var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
                }

                // To resolve the issue, just clear the interceptor
                agent.LocalOrchestrator.ClearInterceptors(interceptorId);

                // And then intercept again the error batch re applied to change the value
                agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z2{str}")
                                row["Name"] = $"Z2{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });

                s = await agent.SynchronizeAsync(setup);

                Assert.Equal(0, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.Empty(batchInfos);
            }
        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableRetryOnNextSyncThenResolveClientByDelete()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // make a first sync to init the two databases
            foreach (var clientProvider in clientsProvider)
                await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(setup);

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                var interceptorId = agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z2{str}")
                                row["Name"] = $"Z1{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });

                agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
                {
                    // Continue On Error
                    args.Resolution = ErrorResolution.RetryOnNextSync;
                    Assert.NotNull(args.Exception);
                    Assert.NotNull(args.ErrorRow);
                    Assert.NotNull(args.SchemaTable);
                    Assert.Equal(SyncRowState.Modified, args.ApplyType);
                });

                var s = await agent.SynchronizeAsync(setup);

                Assert.Equal(2, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERROR", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                var batchInfo = batchInfos[0];

                var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
                }

                // To resolve the issue, just clear the interceptor
                agent.LocalOrchestrator.ClearInterceptors(interceptorId);

                // And then delete the values on server side
                await serverProvider.DeleteProductCategoryAsync($"Z2{str}");

                s = await agent.SynchronizeAsync(setup);

                Assert.Equal(1, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(0, s.TotalChangesAppliedOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(1, s.TotalResolvedConflicts);

                batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.Empty(batchInfos);
            }
        }

        [Fact]
        public virtual async Task ErrorUniqueKeyOnSameTableRetryOnNextSyncThenResolveClientByUpdate()
        {
            var options = new SyncOptions { DisableConstraintsOnApplyChanges = true };

            // make a first sync to init the two databases
            foreach (var clientProvider in clientsProvider)
                await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(setup);

            // Adding two rows on server side, that are correct
            var str = HelperDatabase.GetRandomName().ToUpper()[..9];
            await serverProvider.AddProductCategoryAsync($"Z1{str}", name: $"Z1{str}");
            await serverProvider.AddProductCategoryAsync($"Z2{str}", name: $"Z2{str}");

            foreach (var clientProvider in clientsProvider)
            {
                // Get a random directory to be sure we are not conflicting with another test
                var directoryName = HelperDatabase.GetRandomName();
                options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

                var agent = new SyncAgent(clientProvider, serverProvider, options);

                // To generate a unique key constraint, will modify the batch part info on client just before load it.
                // Replacing $"Z1{str}" with $"Z2{str}" will generate a unique key constraint
                var interceptorId = agent.LocalOrchestrator.OnBatchChangesApplying(args =>
                {
                    if (args.BatchPartInfo != null && args.State == SyncRowState.Modified && args.SchemaTable.TableName == "ProductCategory")
                    {
                        var fullPath = args.BatchInfo.GetBatchPartInfoPath(args.BatchPartInfo);

                        var table = agent.LocalOrchestrator.LoadTableFromBatchPartInfo(fullPath);

                        foreach (var row in table.Rows)
                            if (row["ProductCategoryID"].ToString() == $"Z2{str}")
                                row["Name"] = $"Z1{str}";

                        agent.LocalOrchestrator.SaveTableToBatchPartInfoAsync(args.BatchInfo, args.BatchPartInfo, table);
                    }
                });

                agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
                {
                    // Continue On Error
                    args.Resolution = ErrorResolution.RetryOnNextSync;
                    Assert.NotNull(args.Exception);
                    Assert.NotNull(args.ErrorRow);
                    Assert.NotNull(args.SchemaTable);
                    Assert.Equal(SyncRowState.Modified, args.ApplyType);
                });

                var s = await agent.SynchronizeAsync(setup);

                Assert.Equal(2, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.NotNull(batchInfos);
                Assert.Single(batchInfos);
                Assert.Single(batchInfos[0].BatchPartsInfo);
                Assert.Contains("ERROR", batchInfos[0].BatchPartsInfo[0].FileName);
                Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

                var batchInfo = batchInfos[0];

                var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

                foreach (var syncTable in syncTables)
                {
                    Assert.Equal("ProductCategory", syncTable.TableName);
                    Assert.True(syncTable.HasRows);

                    Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
                }

                // To resolve the issue, just clear the interceptor
                agent.LocalOrchestrator.ClearInterceptors(interceptorId);

                // And then delete the values on server side
                var pc = await serverProvider.GetProductCategoryAsync($"Z2{str}");
                pc.Name = $"Z2{str}";
                await serverProvider.UpdateProductCategoryAsync(pc);

                s = await agent.SynchronizeAsync(setup);

                Assert.Equal(1, s.TotalChangesDownloadedFromServer);
                Assert.Equal(0, s.TotalChangesUploadedToServer);
                Assert.Equal(0, s.TotalChangesAppliedOnServer);
                Assert.Equal(1, s.TotalChangesAppliedOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
                Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
                Assert.Equal(0, s.TotalResolvedConflicts);

                batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

                Assert.Empty(batchInfos);
            }
        }


        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_UniqueKey_OnSameTable_RetryOneMoreTime_ThenResolveClient(SyncOptions options)
        //{
        //    // Only works for SQL

        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, true, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        if (clientProviderType != ProviderType.Sql)
        //            continue;

        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

        //        // create empty client databases
        //        await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);
        //        await agent.SynchronizeAsync(setup);

        //        await Generate_Client_UniqueKeyError(clientProvider as SqlSyncProvider);

        //        agent.RemoteOrchestrator.OnApplyChangesErrorOccured(args =>
        //        {
        //            // Continue On Error
        //            args.Resolution = ErrorResolution.RetryOneMoreTimeAndThrowOnError;
        //            Assert.NotNull(args.Exception);
        //            Assert.NotNull(args.ErrorRow);
        //            Assert.NotNull(args.SchemaTable);
        //            Assert.Equal(SyncRowState.Modified, args.ApplyType);
        //        });

        //        var exc = await Assert.ThrowsAsync<SyncException>(() => agent.SynchronizeAsync(setup));

        //        Assert.NotNull(exc);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.Empty(batchInfos);

        //        await Resolve_Client_UniqueKeyError_WithUpdate(clientProvider as SqlSyncProvider);

        //        var s = await agent.SynchronizeAsync(setup);

        //        batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.Empty(batchInfos);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(13, s.TotalChangesUploadedToServer);
        //        Assert.Equal(13, s.TotalChangesAppliedOnServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);
        //    }
        //}


        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_UniqueKey_OnSameTable_ContinueOnError_ThenResolveClient(SyncOptions options)
        //{
        //    // Only works for SQL

        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, true, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        if (clientProviderType != ProviderType.Sql)
        //            continue;

        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

        //        // create empty client databases
        //        await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);
        //        await agent.SynchronizeAsync(setup);

        //        await Generate_Client_UniqueKeyError(clientProvider as SqlSyncProvider);

        //        agent.RemoteOrchestrator.OnApplyChangesErrorOccured(args =>
        //        {
        //            // Continue On Error
        //            args.Resolution = ErrorResolution.ContinueOnError;
        //            Assert.NotNull(args.Exception);
        //            Assert.NotNull(args.ErrorRow);
        //            Assert.NotNull(args.SchemaTable);
        //            Assert.Equal(SyncRowState.Modified, args.ApplyType);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(2, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalChangesAppliedOnServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(1, s.TotalChangesFailedToApplyOnServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERROR", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        var batchInfo = batchInfos[0];

        //        var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
        //        }

        //        await Resolve_Client_UniqueKeyError_WithUpdate(clientProvider as SqlSyncProvider);

        //        s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(13, s.TotalChangesUploadedToServer);
        //        Assert.Equal(13, s.TotalChangesAppliedOnServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.Empty(batchInfos);
        //    }
        //}



        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_UniqueKey_OnSameTable_RetryOnNextSync_Twice_ThenResolveClient(SyncOptions options)
        //{
        //    // Only works for SQL

        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, true, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        if (clientProviderType != ProviderType.Sql)
        //            continue;

        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

        //        // create empty client databases
        //        await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);
        //        await agent.SynchronizeAsync(setup);

        //        await Generate_Client_UniqueKeyError(clientProvider as SqlSyncProvider);

        //        agent.RemoteOrchestrator.OnApplyChangesErrorOccured(args =>
        //        {
        //            // Continue On Error
        //            args.Resolution = ErrorResolution.RetryOnNextSync;
        //            Assert.NotNull(args.Exception);
        //            Assert.NotNull(args.ErrorRow);
        //            Assert.NotNull(args.SchemaTable);
        //            Assert.Equal(SyncRowState.Modified, args.ApplyType);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(2, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalChangesAppliedOnServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(1, s.TotalChangesFailedToApplyOnServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERROR", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        var batchInfo = batchInfos[0];

        //        var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
        //        }

        //        await Update_Client_UniqueKeyError(clientProvider as SqlSyncProvider);

        //        s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(13, s.TotalChangesUploadedToServer);
        //        Assert.Equal(12, s.TotalChangesAppliedOnServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(1, s.TotalChangesFailedToApplyOnServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        batchInfo = batchInfos[0];

        //        syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
        //        }



        //        await Resolve_Client_UniqueKeyError_WithUpdate(clientProvider as SqlSyncProvider);

        //        s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(13, s.TotalChangesUploadedToServer);
        //        Assert.Equal(13, s.TotalChangesAppliedOnServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);


        //        batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.Empty(batchInfos);
        //    }
        //}


        //// ------------------------------------------------------------------------
        //// Generate Foreign Key failure
        //// ------------------------------------------------------------------------

        ///// <summary>
        ///// Generate a foreign key failure
        ///// </summary>
        //private async Task Generate_ForeignKeyError()
        //{
        //    using var ctx = new AdventureWorksContext(serverProvider);
        //    ctx.Add(new ProductCategory
        //    {
        //        ProductCategoryId = "ZZZZ",
        //        Name = HelperDatabase.GetRandomName("SRV")
        //    });
        //    ctx.Add(new ProductCategory
        //    {
        //        ProductCategoryId = "AAAA",
        //        ParentProductCategoryId = "ZZZZ",
        //        Name = HelperDatabase.GetRandomName("SRV")
        //    });
        //    await ctx.SaveChangesAsync();

        //}

        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_ForeignKey_OnSameTable_RaiseError(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    await Generate_ForeignKeyError();

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectoryName(), directoryName);

        //        // create empty client databases
        //        await this.EnsureDatabaseSchemaAndSeedAsync(client, false, UseFallbackSchema);

        //        // Disable bulk operations to generate the fk constraint failure
        //        clientProvider.UseBulkOperations = false;

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Generate the foreignkey error
        //        agent.LocalOrchestrator.OnRowsChangesApplying(args =>
        //        {
        //            if (args.SyncRows == null || args.SyncRows.Count <= 0)
        //                return;
        //            var row = args.SyncRows[0];
        //            if (row["ParentProductCategoryId"] != null && row["ParentProductCategoryId"].ToString() == "ZZZZ")
        //                row["ParentProductCategoryId"] = "BBBBB";
        //        });

        //        var exc = await Assert.ThrowsAsync<SyncException>(() => agent.SynchronizeAsync(setup));

        //        Assert.NotNull(exc);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.Empty(batchInfos);
        //    }
        //}

        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_ForeignKey_OnSameTable_ContinueOnError(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    await Generate_ForeignKeyError();

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

        //        // create empty client databases
        //        await this.EnsureDatabaseSchemaAndSeedAsync(client, false, UseFallbackSchema);

        //        // Disable bulk operations to generate the fk constraint failure
        //        clientProvider.UseBulkOperations = false;

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Generate error on foreign key on second row
        //        agent.LocalOrchestrator.OnRowsChangesApplying(args =>
        //        {
        //            if (args.SyncRows == null || args.SyncRows.Count <= 0)
        //                return;
        //            var row = args.SyncRows[0];
        //            if (row["ParentProductCategoryId"] != null && row["ParentProductCategoryId"].ToString() == "ZZZZ")
        //                row["ParentProductCategoryId"] = "BBBBB";
        //        });

        //        agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
        //        {
        //            // Continue On Error
        //            args.Resolution = ErrorResolution.ContinueOnError;
        //            Assert.NotNull(args.Exception);
        //            Assert.NotNull(args.ErrorRow);
        //            Assert.NotNull(args.SchemaTable);
        //            Assert.Equal(SyncRowState.Modified, args.ApplyType);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        // Download 2 rows
        //        // But applied only 1
        //        // The other one is a failed inserted row
        //        Assert.Equal(2, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        var batchInfo = batchInfos[0];

        //        var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
        //        }

        //        s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        batchInfo = batchInfos[0];

        //        syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
        //        }

        //    }
        //}


        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_ForeignKey_OnSameTable_ContinueOnError_UsingSyncOptions(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    await Generate_ForeignKeyError();

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);
        //        options.ErrorResolutionPolicy = ErrorResolution.ContinueOnError;

        //        // create empty client databases
        //        await this.EnsureDatabaseSchemaAndSeedAsync(client, false, UseFallbackSchema);

        //        // Disable bulk operations to generate the fk constraint failure
        //        clientProvider.UseBulkOperations = false;

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Generate error on foreign key on second row
        //        agent.LocalOrchestrator.OnRowsChangesApplying(args =>
        //        {
        //            if (args.SyncRows == null || args.SyncRows.Count <= 0)
        //                return;
        //            var row = args.SyncRows[0];
        //            if (row["ParentProductCategoryId"] != null && row["ParentProductCategoryId"].ToString() == "ZZZZ")
        //                row["ParentProductCategoryId"] = "BBBBB";
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        // Download 2 rows
        //        // But applied only 1
        //        // The other one is a failed inserted row
        //        Assert.Equal(2, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        var batchInfo = batchInfos[0];

        //        var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
        //        }

        //        s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        batchInfo = batchInfos[0];

        //        syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.ApplyModifiedFailed, syncTable.Rows[0].RowState);
        //        }

        //    }
        //}


        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_ForeignKey_OnSameTable_RetryOneMoreTime(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    await Generate_ForeignKeyError();

        //    foreach (var clientProvider in clientsProvider)
        //    {

        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectory(), directoryName);

        //        // create empty client databases
        //        await this.EnsureDatabaseSchemaAndSeedAsync(client, false, UseFallbackSchema);

        //        // Disable bulk operations to generate the fk constraint failure
        //        clientProvider.UseBulkOperations = false;

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // As OnRowsChangesApplying will be called 2 times, we only apply tricky change one time
        //        var rowChanged = false;

        //        // Generate the foreignkey error
        //        agent.LocalOrchestrator.OnRowsChangesApplying(args =>
        //        {

        //            if (args.SyncRows == null || args.SyncRows.Count <= 0)
        //                return;

        //            var row = args.SyncRows[0];

        //            if (row["ParentProductCategoryId"] != null && row["ParentProductCategoryId"].ToString() == "ZZZZ")
        //            {
        //                // We need to change the row only one time
        //                if (rowChanged)
        //                    return;

        //                row["ParentProductCategoryId"] = "BBBBB";
        //                rowChanged = true;
        //            }
        //        });

        //        // Once error has been raised, we change back the row to the initial value
        //        // to let a chance to apply again at the end
        //        agent.LocalOrchestrator.OnRowsChangesApplied(args =>
        //        {
        //            if (args.SyncRows == null || args.SyncRows.Count <= 0)
        //                return;

        //            var row = args.SyncRows[0];

        //            if (row["ParentProductCategoryId"] != null && row["ParentProductCategoryId"].ToString() == "BBBBB")
        //            {
        //                row["ParentProductCategoryId"] = "ZZZZ";
        //                rowChanged = true;
        //            }
        //        });

        //        agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
        //        {
        //            // Continue On Error
        //            args.Resolution = ErrorResolution.RetryOneMoreTimeAndThrowOnError;
        //            Assert.NotNull(args.Exception);
        //            Assert.NotNull(args.ErrorRow);
        //            Assert.NotNull(args.SchemaTable);
        //            Assert.Equal(SyncRowState.Modified, args.ApplyType);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        // Download 2 rows
        //        // But applied only 1
        //        // The other one is a failed inserted row
        //        Assert.Equal(2, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(2, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.Empty(batchInfos);
        //    }
        //}

        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Error_ForeignKey_OnSameTable_RetryOnNextSync(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    await Generate_ForeignKeyError();

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        // Get a random directory to be sure we are not conflicting with another test
        //        var directoryName = HelperDatabase.GetRandomName();
        //        options.BatchDirectory = Path.Combine(SyncOptions.GetDefaultUserBatchDirectoryName(), directoryName);

        //        // create empty client databases
        //        await this.EnsureDatabaseSchemaAndSeedAsync(client, false, UseFallbackSchema);

        //        // Disable bulk operations to generate the fk constraint failure
        //        clientProvider.UseBulkOperations = false;

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Generate the foreignkey error
        //        agent.LocalOrchestrator.OnRowsChangesApplying(args =>
        //        {
        //            if (args.SyncRows == null || args.SyncRows.Count <= 0)
        //                return;
        //            var row = args.SyncRows[0];
        //            if (row["ParentProductCategoryId"] != null && row["ParentProductCategoryId"].ToString() == "ZZZZ")
        //                row["ParentProductCategoryId"] = "BBBBB";
        //        });

        //        agent.LocalOrchestrator.OnApplyChangesErrorOccured(args =>
        //        {
        //            // Continue On Error
        //            args.Resolution = ErrorResolution.RetryOnNextSync;
        //            Assert.NotNull(args.Exception);
        //            Assert.NotNull(args.ErrorRow);
        //            Assert.NotNull(args.SchemaTable);
        //            Assert.Equal(SyncRowState.Modified, args.ApplyType);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        // Download 2 rows
        //        // But applied only 1
        //        // The other one is a failed inserted row
        //        Assert.Equal(2, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(1, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        var batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.NotNull(batchInfos);
        //        Assert.Single(batchInfos);
        //        Assert.Single(batchInfos[0].BatchPartsInfo);
        //        Assert.Contains("ERRORS", batchInfos[0].BatchPartsInfo[0].FileName);
        //        Assert.Equal("ProductCategory", batchInfos[0].BatchPartsInfo[0].TableName);

        //        var batchInfo = batchInfos[0];

        //        var syncTables = agent.LocalOrchestrator.LoadTablesFromBatchInfo(batchInfo);

        //        foreach (var syncTable in syncTables)
        //        {
        //            Assert.Equal("ProductCategory", syncTable.TableName);
        //            Assert.True(syncTable.HasRows);

        //            Assert.Equal(SyncRowState.RetryModifiedOnNextSync, syncTable.Rows[0].RowState);
        //        }

        //        // clear interceptors
        //        agent.LocalOrchestrator.ClearInterceptors();

        //        // Resolve the conflict
        //        agent.LocalOrchestrator.OnRowsChangesApplying(args =>
        //        {
        //            if (args.SyncRows == null || args.SyncRows.Count <= 0)
        //                return;
        //            var row = args.SyncRows[0];

        //            if (row["ParentProductCategoryId"] != null && row["ParentProductCategoryId"].ToString() == "BBBBB")
        //                row["ParentProductCategoryId"] = "ZZZZ";

        //        });

        //        s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(0, s.TotalChangesFailedToApplyOnClient);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        batchInfos = agent.LocalOrchestrator.LoadBatchInfos();

        //        Assert.Empty(batchInfos);
        //    }
        //}

        //// ------------------------------------------------------------------------
        //// InsertClient - InsertServer
        //// ------------------------------------------------------------------------

        ///// <summary>
        ///// Generate an insert on both side; will be resolved as RemoteExistsLocalExists on both side
        ///// </summary>
        //private async Task Generate_InsertClient_InsertServer((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    // create empty client databases
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    // Execute a sync on all clients to initialize client and server schema 
        //    var agent = new SyncAgent(clientProvider, serverProvider, options);

        //    // init both server and client
        //    await agent.SynchronizeAsync(setup);

        //    // Insert the conflict product category on each client
        //    var productId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryNameClient = HelperDatabase.GetRandomName("CLI");
        //    var productCategoryNameServer = HelperDatabase.GetRandomName("SRV");

        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        ctx.Add(new ProductCategory
        //        {
        //            ProductCategoryId = productId,
        //            Name = productCategoryNameClient
        //        });
        //        await ctx.SaveChangesAsync();
        //    }

        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        ctx.Add(new ProductCategory
        //        {
        //            ProductCategoryId = productId,
        //            Name = productCategoryNameServer
        //        });
        //        await ctx.SaveChangesAsync();
        //    }

        //}


        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict since it's the default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_IC_IS_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + the conflict (some Clients.count)
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_InsertClient_InsertServer(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "SRV");
        //    }

        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict since it's the default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_IC_IS_ServerShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + the conflict (some Clients.count)
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_InsertClient_InsertServer(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is server; local is client
        //            Assert.StartsWith("SRV", remoteRow["Name"].ToString());
        //            Assert.StartsWith("CLI", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            // The conflict resolution is always the opposite from the one configured by options
        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is client; local is server
        //            Assert.StartsWith("CLI", remoteRow["Name"].ToString());
        //            Assert.StartsWith("SRV", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "SRV");
        //    }

        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Client should wins the conflict because configuration set to ClientWins
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_IC_IS_ClientShouldWins_CozConfiguration(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines (and not the conflict since it's resolved)
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_InsertClient_InsertServer(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        agent.Options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");
        //    }
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Client should wins the conflict because configuration set to ClientWins
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_IC_IS_ClientShouldWins_CozConfiguration_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines (and not the conflict since it's resolved)
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_InsertClient_InsertServer(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        agent.Options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            // Since we have a ClientWins resolution,
        //            // We should NOT have any conflict raised on the client side
        //            // Since the conflict has been resolver on server
        //            // And Server forces applied the client row
        //            // So far the client row is good and should not raise any conflict

        //            throw new Exception("Should not happen !!");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });


        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");
        //    }
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Client should wins the conflict because we have an event raised
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_IC_IS_ClientShouldWins_CozHandler(SyncOptions options)
        //{
        //    // Create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines (and not the conflict since it's resolved)
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_InsertClient_InsertServer(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            // Since we have a ClientWins resolution,
        //            // We should NOT have any conflict raised on the client side
        //            // Since the conflict has been resolver on server
        //            // And Server forces applied the client row
        //            // So far the client row is good and should not raise any conflict

        //            throw new Exception("Should not happen because ConflictResolution.ClientWins !!");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.StartsWith("SRV", localRow["Name"].ToString());
        //            Assert.StartsWith("CLI", remoteRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);

        //            // Client should wins
        //            acf.Resolution = ConflictResolution.ClientWins;
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");
        //    }

        //}

        //// ------------------------------------------------------------------------
        //// Update Client - Update Server
        //// ------------------------------------------------------------------------

        ///// <summary>
        ///// Generate an update on both side; will be resolved as RemoteExistsLocalExists on both side
        ///// </summary>
        //private async Task<string> Generate_UC_US_Conflict((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    // create empty client databases
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    // Conflict product category
        //    var conflictProductCategoryId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryNameClient = "CLI BIKES " + HelperDatabase.GetRandomName();
        //    var productCategoryNameServer = "SRV BIKES " + HelperDatabase.GetRandomName();

        //    // Insert line on server
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        ctx.Add(new ProductCategory { ProductCategoryId = conflictProductCategoryId, Name = "BIKES" });
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Execute a sync on all clients to initialize client and server schema 
        //    var agent = new SyncAgent(clientProvider, serverProvider, options);

        //    // Init both client and server
        //    await agent.SynchronizeAsync(setup);

        //    // Update each client to generate an update conflict
        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        var pc = ctx.ProductCategory.Find(conflictProductCategoryId);
        //        pc.Name = productCategoryNameClient;
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Update server
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        var pc = ctx.ProductCategory.Find(conflictProductCategoryId);
        //        pc.Name = productCategoryNameServer;
        //        await ctx.SaveChangesAsync();
        //    }

        //    return conflictProductCategoryId;
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict because default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_US_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + conflict that should be ovewritten on client
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_UC_US_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "SRV");
        //    }


        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict because default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_US_ServerShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + conflict that should be ovewritten on client
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_UC_US_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is server; local is client
        //            Assert.StartsWith("SRV", remoteRow["Name"].ToString());
        //            Assert.StartsWith("CLI", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            // The conflict resolution is always the opposite from the one configured by options
        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is client; local is server
        //            Assert.StartsWith("CLI", remoteRow["Name"].ToString());
        //            Assert.StartsWith("SRV", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "SRV");
        //    }


        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict because default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_US_ClientShouldWins_CozConfiguration(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + conflict that should be ovewritten on client
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var id = await Generate_UC_US_Conflict(client, options);
        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        agent.Options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");
        //    }
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict because default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_US_ClientShouldWins_CozConfiguration_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + conflict that should be ovewritten on client
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var id = await Generate_UC_US_Conflict(client, options);
        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        agent.Options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            // Check conflict is correctly set
        //            throw new Exception("Should not happen because ConflictResolution.ClientWins");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is client; local is server
        //            Assert.StartsWith("CLI", remoteRow["Name"].ToString());
        //            Assert.StartsWith("SRV", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");
        //    }
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Client should wins coz handler
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_US_ClientShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + conflict that should be ovewritten on client
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var id = await Generate_UC_US_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        agent.RemoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);

        //            Assert.StartsWith("SRV", localRow["Name"].ToString());
        //            Assert.StartsWith("CLI", remoteRow["Name"].ToString());

        //            // Client should wins
        //            acf.Resolution = ConflictResolution.ClientWins;
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");

        //    }
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Client should wins coz handler
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_US_Resolved_ByMerge(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var id = await Generate_UC_US_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is server; local is client
        //            Assert.StartsWith("BOTH", remoteRow["Name"].ToString());
        //            Assert.StartsWith("CLI", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            // The conflict resolution is always the opposite from the one configured by options
        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is client; local is server

        //            Assert.StartsWith("SRV", localRow["Name"].ToString());
        //            Assert.StartsWith("CLI", remoteRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);

        //            // Merge row
        //            acf.Resolution = ConflictResolution.MergeRow;

        //            Assert.NotNull(acf.FinalRow);

        //            acf.FinalRow["Name"] = "BOTH BIKES" + HelperDatabase.GetRandomName();

        //        });


        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "BOTH");
        //    }
        //}



        //// ------------------------------------------------------------------------
        //// Delete Client - Update Server
        //// ------------------------------------------------------------------------

        ///// <summary>
        ///// Generate a delete on the client and an update on the server; will generate:
        ///// - RemoteIsDeletedLocalExists from the Server POV
        ///// - RemoteExistsLocalIsDeleted from the Client POV
        ///// </summary>
        //private async Task<string> Generate_DC_US_Conflict((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    var productId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryName = HelperDatabase.GetRandomName("CLI");
        //    var productCategoryNameUpdated = HelperDatabase.GetRandomName("SRV");

        //    // create empty client databases
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    // Insert a product category and sync it on all clients
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        ctx.Add(new ProductCategory { ProductCategoryId = productId, Name = productCategoryName });
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Execute a sync on all clients to re-initialize client and server schema 
        //    await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(Tables);

        //    // Delete product category on client
        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        var pcdel = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        ctx.ProductCategory.Remove(pcdel);
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Update on Server
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        var pcupdated = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        pcupdated.Name = productCategoryNameUpdated;
        //        await ctx.SaveChangesAsync();
        //    }

        //    return productId;
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict since it's the default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_US_ClientShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its own deleted row (conflicting)
        //    // then download the updated row from server 
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var productId = await Generate_DC_US_Conflict(client, options);
        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        agent.Options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");
        //    }


        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict since it's the default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_US_ClientShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its own deleted row (conflicting)
        //    // then download the updated row from server 
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var productId = await Generate_DC_US_Conflict(client, options);
        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        agent.Options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            // Since we have a ClientWins resolution,
        //            // We should NOT have any conflict raised on the client side
        //            // Since the conflict has been resolver on server
        //            // And Server forces applied the client row
        //            // So far the client row is good and should not raise any conflict

        //            throw new Exception("Should not happen !!");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalExists, conflict.Type);

        //            acf.Resolution = ConflictResolution.ClientWins;
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "CLI");
        //    }


        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict since it's the default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_US_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_US_Conflict(client, options);
        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "SRV");
        //    }


        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict since it's the default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_US_ServerShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_US_Conflict(client, options);
        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Deleted, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalIsDeleted, conflict.Type);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalExists, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client, "SRV");
        //    }


        //}



        //// ------------------------------------------------------------------------
        //// Update Client When Outdated
        //// ------------------------------------------------------------------------

        ///// <summary>
        ///// Generate an outdated conflict. Both lines exists on both side but server has cleaned metadatas
        ///// </summary>
        //private async Task Generate_UC_OUTDATED_Conflict((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    // create empty client databases
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    var setup = new SyncSetup(Tables);

        //    // coz of ProductCategory Parent Id Foreign Key Constraints
        //    // on Reset table in MySql
        //    options.DisableConstraintsOnApplyChanges = true;

        //    // Execute a sync to initialize client and server schema 
        //    var agent = new SyncAgent(clientProvider, serverProvider, options);

        //    if (options.TransactionMode != TransactionMode.AllOrNothing && (clientProviderType == ProviderType.MySql || clientProviderType == ProviderType.MariaDB))
        //    {
        //        agent.LocalOrchestrator.OnGetCommand(async args =>
        //        {
        //            if (args.CommandType == DbCommandType.Reset)
        //            {
        //                var scopeInfo = await agent.LocalOrchestrator.GetScopeInfoAsync(args.Connection, args.Transaction);
        //                await agent.LocalOrchestrator.DisableConstraintsAsync(scopeInfo, args.Table.TableName, args.Table.SchemaName, args.Connection, args.Transaction);
        //            }
        //        });
        //    }

        //    // Since we may have an Outdated situation due to previous client, go for a Reinitialize sync type
        //    await agent.SynchronizeAsync(setup, SyncType.Reinitialize);

        //    // Insert the conflict product category on each client
        //    var productId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryNameClient = HelperDatabase.GetRandomName("CLI");

        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        ctx.Add(new ProductCategory
        //        {
        //            ProductCategoryId = productId,
        //            Name = productCategoryNameClient
        //        });
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Since we may have an Outdated situation due to previous client, go for a Reinitialize sync type
        //    var s = await agent.SynchronizeAsync(SyncType.ReinitializeWithUpload);

        //    // Generation of an outdated mark on the server
        //    var remoteOrchestrator = new RemoteOrchestrator(serverProvider, options);
        //    var ts = await remoteOrchestrator.GetLocalTimestampAsync();
        //    await remoteOrchestrator.DeleteMetadatasAsync(ts + 1);
        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on client and server then server purged metadata.
        ///// Should have an outdated situation, resolved by a reinitialize action
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_OUTDATED_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    var cpt = 1;
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_UC_OUTDATED_Conflict(client, options);

        //        var setup = new SyncSetup(Tables);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since we are reinitializing");

        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since we are reinitializing");
        //        });

        //        localOrchestrator.OnOutdated(oa =>
        //        {
        //            oa.Action = OutdatedAction.ReinitializeWithUpload;
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(cpt, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);

        //        cpt++;
        //    }

        //}

        ///// <summary>
        ///// Generate a conflict when inserting one row on client and server then server purged metadata.
        ///// Should have an outdated situation, resolved by a reinitialize action
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_OUTDATED_ServerShouldWins_EvenIf_ResolutionIsClientWins(SyncOptions options)
        //{

        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    var cpt = 1;
        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_UC_OUTDATED_Conflict(client, options);

        //        var setup = new SyncSetup(Tables);

        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;
        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since we are reinitializing");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since we are reinitializing");
        //        });

        //        localOrchestrator.OnOutdated(oa =>
        //        {
        //            oa.Action = OutdatedAction.ReinitializeWithUpload;
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(cpt, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //        cpt++;

        //    }

        //}




        //// ------------------------------------------------------------------------
        //// Update Client - Delete Server
        //// ------------------------------------------------------------------------


        ///// <summary>
        ///// Generate an update on the client and delete on the server; will be resolved as:
        ///// - RemoteExistsLocalIsDeleted from the server side POV
        ///// - RemoteIsDeletedLocalExists from the client side POV
        ///// </summary>
        ///// <param name="options"></param>
        ///// <returns></returns>
        //private async Task<string> Generate_UC_DS_Conflict((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    var productId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryName = HelperDatabase.GetRandomName("CLI");
        //    var productCategoryNameUpdated = HelperDatabase.GetRandomName("CLI_UPDATED");

        //    // create empty client database
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    // Insert a product category and sync it on all clients
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        ctx.Add(new ProductCategory { ProductCategoryId = productId, Name = productCategoryName });
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Execute a sync to initialize client and server schema 
        //    await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(Tables);

        //    // Update product category on each client
        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        var pcupdated = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        pcupdated.Name = productCategoryNameUpdated;
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Delete on Server
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        var pcdel = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        ctx.ProductCategory.Remove(pcdel);
        //        await ctx.SaveChangesAsync();
        //    }

        //    return productId;
        //}

        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_DS_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_UC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_DS_ServerShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_UC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is server; local is client
        //            Assert.StartsWith("CLI_UPDATED", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            // The conflict resolution is always the opposite from the one configured by options
        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalExists, conflict.Type);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is client; local is server
        //            Assert.StartsWith("CLI_UPDATED", remoteRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Deleted, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalIsDeleted, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_DS_ClientShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var productCategoryId = await Generate_UC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Resolution is set to client side
        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}


        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_DS_ClientShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        var productCategoryId = await Generate_UC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Resolution is set to client side
        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since Client is the winner of the conflict and conflict has been resolved on the server side");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is client; local is server
        //            Assert.StartsWith("CLI_UPDATED", remoteRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Deleted, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalIsDeleted, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}


        //// ------------------------------------------------------------------------
        //// Delete Client - Delete Server
        //// ------------------------------------------------------------------------


        ///// <summary>
        ///// Generate a deleted row on the server and on the client, it's resolved as:
        ///// - RemoteIsDeletedLocalIsDeleted from both side POV
        ///// </summary>
        //private async Task Generate_DC_DS_Conflict((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    var productId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryName = HelperDatabase.GetRandomName("CLI");
        //    var productCategoryNameUpdated = HelperDatabase.GetRandomName("SRV");

        //    // create empty client database
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    // Insert a product category and sync it on all clients
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        ctx.Add(new ProductCategory { ProductCategoryId = productId, Name = productCategoryName });
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Execute a sync to initialize client and server schema 
        //    await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(Tables);

        //    // Delete product category 
        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        var pcdel = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        ctx.ProductCategory.Remove(pcdel);
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Delete on Server
        //    using (var ctx = new AdventureWorksContext(serverProvider))
        //    {
        //        var pcdel = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        ctx.ProductCategory.Remove(pcdel);
        //        await ctx.SaveChangesAsync();
        //    }
        //}

        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_DS_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_DS_ServerShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            var conflict = await acf.GetSyncConflictAsync();
        //            // Check conflict is correctly set
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Deleted, conflict.LocalRow.RowState);

        //            // The conflict resolution is always the opposite from the one configured by options
        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalIsDeleted, conflict.Type);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Deleted, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalIsDeleted, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_DS_ClientShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}


        ///// <summary>
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_DS_ClientShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            Debug.WriteLine("Should not happen since Client is the winner of the conflict and conflict has been resolved on the server side");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Deleted, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalIsDeleted, conflict.Type);
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}


        //// ------------------------------------------------------------------------
        //// Delete Client - Not Exists Server
        //// ------------------------------------------------------------------------

        ///// <summary>
        ///// Generate a deleted row on client, that does not exists on server, it's resolved as:
        /////  - RemoteIsDeletedLocalNotExists from the Server POV 
        /////  - RemoteNotExistsLocalIsDeleted from the Client POV, but it can't happen
        ///// </summary>
        //private async Task Generate_DC_NULLS_Conflict((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    var productId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryName = HelperDatabase.GetRandomName("CLI");

        //    // create empty client database
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    // Execute a sync on all clients to initialize client and server schema 
        //    await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(Tables);


        //    // Insert a product category on all clients
        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        ctx.Add(new ProductCategory { ProductCategoryId = productId, Name = productCategoryName });
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Then delete it
        //    using (var ctx = new AdventureWorksContext(client, Fixture.UseFallbackSchema))
        //    {
        //        var pcdel = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        ctx.ProductCategory.Remove(pcdel);
        //        await ctx.SaveChangesAsync();
        //    }

        //    // So far we have a row marked as deleted in the tracking table.
        //}

        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_NULLS_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_NULLS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_NULLS_ServerShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_NULLS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Even if it's a server win here, the server should not send back anything, since he has anything related to this line in its metadatas");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalNotExists, conflict.Type);
        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Null(localRow);

        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_NULLS_ClientShouldWins(SyncOptions options)
        //{
        //    Debug.WriteLine($"-------------------------------");
        //    Debug.WriteLine($"- Start Test Conflict_DC_NULLS_ClientShouldWins {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        Debug.WriteLine($"-- Generate_DC_NULLS_Conflict client {client.DatabaseName}. {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");
        //        await Generate_DC_NULLS_Conflict(client, options);
        //        Debug.WriteLine($"-- Done Generate_DC_NULLS_Conflict client {client.DatabaseName}. {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Set conflict resolution to client
        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        Debug.WriteLine($"-- Sync for {client.DatabaseName}. {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");
        //        var s = await agent.SynchronizeAsync(setup);
        //        Debug.WriteLine($"-- Done Sync Result for {client.DatabaseName}. {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        Debug.WriteLine($"-- CheckProductCategoryRows for {client.DatabaseName}. {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");
        //        await CheckProductCategoryRows(client);
        //        Debug.WriteLine($"-- Done CheckProductCategoryRows for {client.DatabaseName}. {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");

        //    }

        //    Debug.WriteLine($"- End Test Conflict_DC_NULLS_ClientShouldWins {this.stopwatch.Elapsed.Minutes}:{this.stopwatch.Elapsed.Seconds}.{this.stopwatch.Elapsed.Milliseconds}");
        //}


        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_DC_NULLS_ClientShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_DC_NULLS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Set conflict resolution to client
        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since Client is the winner of the conflict and conflict has been resolved on the server side");
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalNotExists, conflict.Type);
        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Null(localRow);

        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// <summary>
        ///// Generate a deleted row on Server, that does not exists on Client, it's resolved as:
        ///// </summary>
        //private async Task Generate_NULLC_DS_Conflict((string DatabaseName, ProviderType ProviderType, CoreProvider Provider) client, SyncOptions options)
        //{
        //    var productId = HelperDatabase.GetRandomName().ToUpperInvariant().Substring(0, 6);
        //    var productCategoryName = HelperDatabase.GetRandomName("CLI");
        //    var productCategoryNameUpdated = HelperDatabase.GetRandomName("SRV");

        //    // create empty client database
        //    await this.CreateDatabaseAsync(clientProviderType, client.DatabaseName, true);

        //    // Execute a sync on all clients to initialize client and server schema 
        //    await new SyncAgent(clientProvider, serverProvider, options).SynchronizeAsync(Tables);

        //    // Insert a product category on server
        //    using (var ctx = new AdventureWorksContext(serverProvider, Fixture.UseFallbackSchema))
        //    {
        //        ctx.Add(new ProductCategory { ProductCategoryId = productId, Name = productCategoryName });
        //        await ctx.SaveChangesAsync();
        //    }

        //    // Then delete it
        //    using (var ctx = new AdventureWorksContext(serverProvider, Fixture.UseFallbackSchema))
        //    {
        //        var pcdel = ctx.ProductCategory.Single(pc => pc.ProductCategoryId == productId);
        //        ctx.ProductCategory.Remove(pcdel);
        //        await ctx.SaveChangesAsync();
        //    }

        //    // So far we have a row marked as deleted in the tracking table.
        //}


        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_NULLC_DS_ServerShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_NULLC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_NULLC_DS_ServerShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_NULLC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalNotExists, conflict.Type);
        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Null(localRow);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since since client did not sent anything. SO far server will send back the deleted row as standard batch row");
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        await CheckProductCategoryRows(client);
        //    }

        //}

        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_NULLC_DS_ClientShouldWins(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);


        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_NULLC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Set conflict resolution to client
        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(1, s.TotalResolvedConflicts);


        //        await CheckProductCategoryRows(client);
        //    }

        //}


        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_NULLC_DS_ClientShouldWins_CozHandler(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);


        //    foreach (var clientProvider in clientsProvider)
        //    {
        //        await Generate_NULLC_DS_Conflict(client, options);

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        // Set conflict resolution to client
        //        options.ConflictResolutionPolicy = ConflictResolutionPolicy.ClientWins;

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteIsDeletedLocalNotExists, conflict.Type);
        //            Assert.Equal(SyncRowState.Deleted, conflict.RemoteRow.RowState);
        //            Assert.Null(localRow);
        //        });

        //        // From Server : Remote is client, Local is server
        //        remoteOrchestrator.OnApplyChangesConflictOccured(acf =>
        //        {
        //            throw new Exception("Should not happen since since client did not sent anything. SO far server will send back the deleted row as standard batch row");
        //        });

        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(0, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalChangesAppliedOnClient);
        //        Assert.Equal(1, s.TotalResolvedConflicts);


        //        await CheckProductCategoryRows(client);
        //    }

        //}



        ///// Generate a conflict when inserting one row on server and the same row on each client
        ///// Server should wins the conflict because default behavior
        ///// </summary>
        //[Theory]
        //[ClassData(typeof(SyncOptionsData))]
        //public virtual async Task Conflict_UC_US_ClientChoosedTheWinner(SyncOptions options)
        //{
        //    // create a server schema without seeding
        //    await this.EnsureDatabaseSchemaAndSeedAsync(serverProvider, false, UseFallbackSchema);

        //    // Execute a sync on all clients and check results
        //    // Each client will upload its row (conflicting)
        //    // then download the others client lines + conflict that should be ovewritten on client
        //    foreach (var clientProvider in clientsProvider)
        //    {

        //        await Generate_UC_US_Conflict(client, options);

        //        var clientNameDecidedOnClientMachine = HelperDatabase.GetRandomName();

        //        var agent = new SyncAgent(clientProvider, serverProvider, options);

        //        var localOrchestrator = agent.LocalOrchestrator;
        //        var remoteOrchestrator = agent.RemoteOrchestrator;

        //        // From client : Remote is server, Local is client
        //        // From here, we are going to let the client decides who is the winner of the conflict
        //        localOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is server; local is client
        //            Assert.StartsWith("SRV", remoteRow["Name"].ToString());
        //            Assert.StartsWith("CLI", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            // The conflict resolution is always the opposite from the one configured by options
        //            Assert.Equal(ConflictResolution.ClientWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);

        //            // From that point, you can easily letting the client decides who is the winner
        //            // You can do a merge or whatever
        //            // Show a UI with the local / remote row and letting him decides what is the good row version
        //            // for testing purpose; will just going to set name to some fancy UI_CLIENT... instead of CLI or SRV

        //            // SHOW UI
        //            // OH.... CLIENT DECIDED TO SET NAME TO /// clientNameDecidedOnClientMachine 

        //            remoteRow["Name"] = clientNameDecidedOnClientMachine;
        //            // Mandatory to override the winner registered in the tracking table
        //            // Use with caution !
        //            // To be sure the row will be marked as updated locally, the scope id should be set to null
        //            acf.SenderScopeId = null;
        //        });

        //        // From Server : Remote is client, Local is server
        //        // From that point we do not do anything, letting the server to resolve the conflict and send back
        //        // the server row and client row conflicting to the client
        //        remoteOrchestrator.OnApplyChangesConflictOccured(async acf =>
        //        {
        //            // Check conflict is correctly set
        //            var conflict = await acf.GetSyncConflictAsync();
        //            var localRow = conflict.LocalRow;
        //            var remoteRow = conflict.RemoteRow;

        //            // remote is client; local is server
        //            Assert.StartsWith("CLI", remoteRow["Name"].ToString());
        //            Assert.StartsWith("SRV", localRow["Name"].ToString());

        //            Assert.Equal(SyncRowState.Modified, conflict.RemoteRow.RowState);
        //            Assert.Equal(SyncRowState.Modified, conflict.LocalRow.RowState);

        //            Assert.Equal(ConflictResolution.ServerWins, acf.Resolution);
        //            Assert.Equal(ConflictType.RemoteExistsLocalExists, conflict.Type);
        //        });

        //        // First sync, we allow server to resolve the conflict and send back the result to client
        //        var s = await agent.SynchronizeAsync(setup);

        //        Assert.Equal(1, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(1, s.TotalResolvedConflicts);

        //        // From this point the Server row Name is "SRV...."
        //        // And the Client row NAME is "UI_CLIENT..."
        //        // Make a new sync to send "UI_CLIENT..." to Server

        //        s = await agent.SynchronizeAsync(setup);


        //        Assert.Equal(0, s.TotalChangesDownloadedFromServer);
        //        Assert.Equal(1, s.TotalChangesUploadedToServer);
        //        Assert.Equal(0, s.TotalResolvedConflicts);

        //        // Check that the product category name has been correctly sended back to the server
        //        await CheckProductCategoryRows(client);

        //    }


        //}

    }
}