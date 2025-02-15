﻿using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Manager;
using Dotmim.Sync.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Dotmim.Sync
{
    public partial class BaseOrchestrator
    {


        internal async Task<bool> IsScopeInfoSchemaValidAsync(SyncContext context, DbConnection connection = default, DbTransaction transaction = default,
                        CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = default)
        {
            await using var runner = await this.GetConnectionAsync(context, SyncMode.NoTransaction, SyncStage.Migrating,
                connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            var scopeInfoTableName = this.Provider.GetParsers(new SyncTable(this.Options.ScopeInfoTableName), new SyncSetup()).tableName;
            var tableName = scopeInfoTableName.Unquoted().Normalized().ToString();
            var tableBuilder = this.GetTableBuilder(new SyncTable(tableName), new ScopeInfo { Setup = new SyncSetup() });

            // check columns
            var columns = (await tableBuilder.GetColumnsAsync(runner.Connection, runner.Transaction).ConfigureAwait(false)).ToList();

            if (columns.Count != 6)
                return false;

            if (columns[0].ColumnName != "sync_scope_name")
                return false;
            if (columns[1].ColumnName != "sync_scope_schema")
                return false;
            if (columns[2].ColumnName != "sync_scope_setup")
                return false;
            if (columns[3].ColumnName != "sync_scope_version")
                return false;
            if (columns[4].ColumnName != "sync_scope_last_clean_timestamp")
                return false;
            if (columns[5].ColumnName != "sync_scope_properties")
                return false;

            return true;
        }

        internal async Task<bool> IsScopeInfoClientSchemaValidAsync(SyncContext context, DbConnection connection = default, DbTransaction transaction = default,
                CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = default)
        {
            await using var runner = await this.GetConnectionAsync(context, SyncMode.NoTransaction, SyncStage.Migrating,
                connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            var scopeInfoTableName = this.Provider.GetParsers(new SyncTable(this.Options.ScopeInfoTableName), new SyncSetup()).tableName;
            var tableName = $"{scopeInfoTableName.Unquoted().Normalized()}_client";
            var tableBuilder = this.GetTableBuilder(new SyncTable(tableName), new ScopeInfo { Setup = new SyncSetup() });

            // check columns
            var columns = (await tableBuilder.GetColumnsAsync(runner.Connection, runner.Transaction).ConfigureAwait(false)).ToList();

            if (columns.Count != 10)
                return false;

            if (columns[0].ColumnName != "sync_scope_id")
                return false;
            if (columns[1].ColumnName != "sync_scope_name")
                return false;
            if (columns[2].ColumnName != "sync_scope_hash")
                return false;
            if (columns[3].ColumnName != "sync_scope_parameters")
                return false;
            if (columns[4].ColumnName != "scope_last_sync_timestamp")
                return false;
            if (columns[5].ColumnName != "scope_last_server_sync_timestamp")
                return false;
            if (columns[6].ColumnName != "scope_last_sync_duration")
                return false;
            if (columns[7].ColumnName != "scope_last_sync")
                return false;
            if (columns[8].ColumnName != "sync_scope_errors")
                return false;
            if (columns[9].ColumnName != "sync_scope_properties")
                return false;

            return true;
        }


        internal virtual async Task<SyncTable> MigrateScopeInfoClientTableAsync(SyncContext context, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            await using var runner = await this.GetConnectionAsync(context, SyncMode.WithTransaction, SyncStage.Migrating, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            var dbBuilder = this.Provider.GetDatabaseBuilder();

            // scope info client table name (and tmp table name)
            var parsedName = ParserName.Parse(this.Options.ScopeInfoTableName);
            var cScopeInfoClientTableName = $"{parsedName.Unquoted().Normalized()}_client";
            var tmpCScopeInfoClientTableName = $"tmp{cScopeInfoClientTableName}";
            string message = "";

            // ----------------------------------------------------
            // Step 1 : Renaming scope_info_client to tmpscope_info_client
            // ----------------------------------------------------
            var tmpScopeInfoClientExists = await dbBuilder.ExistsTableAsync(tmpCScopeInfoClientTableName, null,
                runner.Connection, runner.Transaction).ConfigureAwait(false);

            bool existsCScopeInfoClient;
            (context, existsCScopeInfoClient) = await InternalExistsScopeInfoTableAsync(context, DbScopeType.ScopeInfoClient,
                runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

            if (!tmpScopeInfoClientExists && existsCScopeInfoClient)
            {
                await dbBuilder.RenameTableAsync(cScopeInfoClientTableName, parsedName.SchemaName, tmpCScopeInfoClientTableName, parsedName.SchemaName,
                    runner.Connection, runner.Transaction).ConfigureAwait(false);

                message = $"- Temporary renamed {cScopeInfoClientTableName} to {tmpCScopeInfoClientTableName}.";
                await this.InterceptAsync(new UpgradeProgressArgs(context, message, SyncVersion.Current, runner.Connection, runner.Transaction), runner.Progress).ConfigureAwait(false);
            }

            // ----------------------------------------------------
            // Step 3 : Create scope_info_client 
            // ----------------------------------------------------
            (context, existsCScopeInfoClient) = await InternalExistsScopeInfoTableAsync(context, DbScopeType.ScopeInfoClient,
                runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

            if (!existsCScopeInfoClient)
                (context, _) = await this.InternalCreateScopeInfoTableAsync(context, DbScopeType.ScopeInfoClient,
                    runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

            message = $"- Created {cScopeInfoClientTableName} table.";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, SyncVersion.Current, runner.Connection, runner.Transaction), runner.Progress).ConfigureAwait(false);

            tmpScopeInfoClientExists = await dbBuilder.ExistsTableAsync(tmpCScopeInfoClientTableName, null,
                runner.Connection, runner.Transaction).ConfigureAwait(false);

            SyncTable table = null;

            if (tmpScopeInfoClientExists)
                table = await dbBuilder.GetTableAsync(tmpCScopeInfoClientTableName, null, runner.Connection, runner.Transaction).ConfigureAwait(false);

            return table;

        }

        internal virtual async Task<SyncTable> MigrateScopeInfoTableAsync(SyncContext context, DbConnection connection, DbTransaction transaction,
                        CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            await using var runner = await this.GetConnectionAsync(context, SyncMode.WithTransaction, SyncStage.Migrating, connection, transaction, cancellationToken, progress).ConfigureAwait(false);
            var dbBuilder = this.Provider.GetDatabaseBuilder();

            // scope info table name (and tmp table name)
            var parsedName = ParserName.Parse(this.Options.ScopeInfoTableName);
            var cScopeInfoTableName = $"{parsedName.Unquoted().Normalized()}";
            var cScopeInfoClientTableName = $"{parsedName.Unquoted().Normalized()}_client";
            var tmpCScopeInfoTableName = $"tmp{parsedName.Unquoted().Normalized()}";
            var message = "";

            // Initialize database if needed
            await dbBuilder.EnsureDatabaseAsync(runner.Connection, runner.Transaction).ConfigureAwait(false);

            // ----------------------------------------------------
            // Step 1 : Renaming scope_info to tmpscope_info
            // ----------------------------------------------------
            var tmpScopeInfoExists = await dbBuilder.ExistsTableAsync(tmpCScopeInfoTableName, null,
                runner.Connection, runner.Transaction).ConfigureAwait(false);

            var existsCScopeInfo = await dbBuilder.ExistsTableAsync(cScopeInfoTableName, parsedName.SchemaName,
                runner.Connection, runner.Transaction).ConfigureAwait(false);

            if (!tmpScopeInfoExists && existsCScopeInfo)
            {
                await dbBuilder.RenameTableAsync(cScopeInfoTableName, parsedName.SchemaName, tmpCScopeInfoTableName, parsedName.SchemaName,
                    runner.Connection, runner.Transaction).ConfigureAwait(false);

                message = $"- Temporary renamed {cScopeInfoTableName} to {tmpCScopeInfoTableName}.";
                await this.InterceptAsync(new UpgradeProgressArgs(context, message, SyncVersion.Current, runner.Connection, runner.Transaction), runner.Progress).ConfigureAwait(false);
            }

            // ----------------------------------------------------
            // Step 2 : Create scope_info 
            // ----------------------------------------------------
            var pScopeInfo = parsedName.Schema();
            existsCScopeInfo = await dbBuilder.ExistsTableAsync(pScopeInfo.ObjectName, pScopeInfo.SchemaName,
                runner.Connection, runner.Transaction).ConfigureAwait(false);

            if (!existsCScopeInfo)
                (context, _) = await this.InternalCreateScopeInfoTableAsync(context, DbScopeType.ScopeInfo,
                    runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

            message = $"- Created new version of {(string.IsNullOrEmpty(parsedName.SchemaName) ? cScopeInfoTableName : $"{parsedName.SchemaName}.{cScopeInfoTableName}")} table.";
            await this.InterceptAsync(new UpgradeProgressArgs(context, message, SyncVersion.Current, runner.Connection, runner.Transaction), runner.Progress).ConfigureAwait(false);

            var oldScopeInfotable = await dbBuilder.GetTableAsync(tmpCScopeInfoTableName, parsedName.SchemaName, runner.Connection, runner.Transaction).ConfigureAwait(false);

            return oldScopeInfotable;
        }


        private async Task<Version> AutoUpgdrateToNewVersionAsync(ScopeInfo scopeInfo, SyncContext context, Version newVersion, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            var message = $"Upgrade to {newVersion}:";

            await this.InterceptAsync(new UpgradeProgressArgs(context, message, newVersion, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
            return newVersion;
        }
    }
}
