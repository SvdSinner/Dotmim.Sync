using Dotmim.Sync.Builders;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Data.SqlTypes;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Dotmim.Sync.SqlServer
{
    public static class SqlManagementUtils
    {
        /// <summary>
        /// Get all Tables
        /// </summary>
        public static async Task<SyncSetup> GetAllTablesAsync(SqlConnection connection, SqlTransaction transaction)
        {
            var command = $"Select tbl.name as TableName, " +
                          $"sch.name as SchemaName " +
                          $"  from sys.tables as tbl  " +
                          $"  Inner join sys.schemas as sch on tbl.schema_id = sch.schema_id;";

            var syncSetup = new SyncSetup();

            using (var sqlCommand = new SqlCommand(command, connection))
            {
                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                using (var reader = await sqlCommand.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (reader.Read())
                    {
                        var tableName = reader.GetString(0);
                        var schemaName = reader.GetString(1) == "dbo" ? null : reader.GetString(1);
                        var setupTable = new SetupTable(tableName, schemaName);
                        syncSetup.Tables.Add(setupTable);
                    }
                }

                foreach (var setupTable in syncSetup.Tables)
                {
                    var syncTableColumnsList = await GetColumnsForTableAsync(setupTable.TableName, setupTable.SchemaName, connection, transaction).ConfigureAwait(false);

                    foreach (var column in syncTableColumnsList.Rows)
                        setupTable.Columns.Add(column["name"].ToString());
                }


                if (!alreadyOpened)
                    connection.Close();

            }
            return syncSetup;
        }

        /// <summary>
        /// Get Table
        /// </summary>
        public static async Task<SyncTable> GetTableDefinitionAsync(string tableName, string schemaName, SqlConnection connection, SqlTransaction transaction)
        {
            var command = $"Select top 1 tbl.name as TableName, " +
                          $"sch.name as SchemaName " +
                          $"  from sys.tables as tbl  " +
                          $"  Inner join sys.schemas as sch on tbl.schema_id = sch.schema_id " +
                          $"  Where tbl.name = @tableName and sch.name = @schemaName ";

            var tableNameNormalized = ParserName.Parse(tableName).Unquoted().Normalized().ToString();
            var tableNameString = ParserName.Parse(tableName).ToString();

            var schemaNameString = "dbo";
            if (!string.IsNullOrEmpty(schemaName))
            {
                schemaNameString = ParserName.Parse(schemaName).ToString();
                schemaNameString = string.IsNullOrWhiteSpace(schemaNameString) ? "dbo" : schemaNameString;
            }

            var syncTable = new SyncTable(tableNameNormalized, schemaNameString);

            using (var sqlCommand = new SqlCommand(command, connection))
            {
                sqlCommand.Parameters.AddWithValue("@tableName", tableNameString);
                sqlCommand.Parameters.AddWithValue("@schemaName", schemaNameString);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                using (var reader = await sqlCommand.ExecuteReaderAsync().ConfigureAwait(false))
                    syncTable.Load(reader);

                if (!alreadyOpened)
                    connection.Close();

            }
            return syncTable;
        }


        public static async Task<SyncTable> GetTableAsync(string tableName, string schemaName, SqlConnection connection, SqlTransaction transaction)
        {
            var pTableName = ParserName.Parse(tableName);
            var pSchemaName = ParserName.Parse(schemaName);

            var name = $"{pTableName.Quoted()}";
            var sName = String.IsNullOrEmpty(schemaName) ? (String.IsNullOrEmpty(pTableName.SchemaName) ? "[dbo]" : pTableName.SchemaName)  : $"{pSchemaName.Quoted()}";

            var command = $"Select * from {sName}.{name};";

            var syncTable = new SyncTable(name, sName);

            using var sqlCommand = new SqlCommand(command, connection);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            if (!alreadyOpened)
                await connection.OpenAsync().ConfigureAwait(false);

            sqlCommand.Transaction = transaction;

            using (var reader = await sqlCommand.ExecuteReaderAsync().ConfigureAwait(false))
                syncTable.Load(reader);

            if (!alreadyOpened)
                connection.Close();

            return syncTable;
        }

        public static async Task RenameTableAsync(string tableName, string schemaName, string newTableName, string newSchemaName, SqlConnection connection, SqlTransaction transaction)
        {
            var pTableName = ParserName.Parse(tableName).Unquoted().ToString();
            var pSchemaName = ParserName.Parse(schemaName).Unquoted().ToString();

            var pNewTableName = ParserName.Parse(newTableName).Unquoted().ToString(); 
            var pNewSchemaName = ParserName.Parse(newSchemaName).Unquoted().ToString();

            if (string.IsNullOrEmpty(pSchemaName) && string.IsNullOrEmpty(pNewSchemaName) && pSchemaName != pNewSchemaName)
                throw new Exception("Cannot rename tables into a different schema");//
            var commandText = //$"exec sp_rename '{quotedTableName}', '{quotedNewTableName}';";
                $"DECLARE --@tablename sysname = '{pTableName}', @schemaname sysname = '{pSchemaName}', @newTableName sysname = '{pNewTableName}', \r\n" +
                //TODO:Clean this up if it works.
                @"      @sql nvarchar(MAX) ='', @childname sysname, @childSchema sysname;
--Get schema if not provided
IF LEN(ISNULL(@schemaname, '')) = 0
	SELECT @schemaname = SCHEMA_NAME(t.schema_id) FROM sys.tables t WHERE t.name = @tablename;
DECLARE childrenToRename CURSOR FOR 
SELECT o.name ObjName, s.name SchemaName
FROM sys.objects o INNER JOIN sys.schemas s ON s.schema_id = o.schema_id INNER JOIN sys.tables t ON t.object_id =o.parent_object_id
WHERE @tablename = OBJECT_NAME(o.parent_object_id) AND SCHEMA_NAME(t.schema_id) = @schemaname
OPEN childrenToRename;
FETCH NEXT FROM childrenToRename INTO @childname, @childSchema;
WHILE @@FETCH_STATUS = 0
BEGIN
	DECLARE @fullChildName NVARCHAR(256) = '['+@childSchema+'].['+ @childname + ']';
	DECLARE @newName sysname = REPLACE(@childname, @tablename, @newTableName);
	PRINT ('Renaming child: ' + @fullChildName + ' to: '+ @newName);
	EXEC sp_rename @fullChildName, @newName;
	FETCH NEXT FROM childrenToRename INTO @childname, @childSchema;
END
CLOSE childrenToRename;
DEALLOCATE childrenToRename;
DECLARE @fullName NVARCHAR(256) = '['+@schemaname+'].['+ @tablename + ']';
EXEC sp_rename @fullName, @newTableName;";
            using var sqlCommand = new SqlCommand(commandText, connection);
            sqlCommand.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = pTableName });
            sqlCommand.Parameters.Add(new SqlParameter("@schemaname", SqlDbType.NVarChar, 128) { Value = pSchemaName });
            sqlCommand.Parameters.Add(new SqlParameter("@newTableName", SqlDbType.NVarChar, 128) { Value = pNewTableName });

            bool alreadyOpened = connection.State == ConnectionState.Open;

            if (!alreadyOpened)
                await connection.OpenAsync().ConfigureAwait(false);

            sqlCommand.Transaction = transaction;

            await sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (!alreadyOpened)
                connection.Close();
        }


        /// <summary>
        /// Get Table
        /// </summary>
        public static async Task<SyncTable> GetTriggerAsync(string triggerName, string schemaName, SqlConnection connection, SqlTransaction transaction)
        {
            var command = $"SELECT tr.name FROM sys.triggers tr " +
                           "JOIN sys.tables t ON tr.parent_id = t.object_id " +
                           "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                           "WHERE tr.name = @triggerName and s.name = @schemaName";

            var triggerNameNormalized = ParserName.Parse(triggerName).Unquoted().Normalized().ToString();
            var triggerNameString = ParserName.Parse(triggerName).ToString();

            var schemaNameString = "dbo";
            if (!string.IsNullOrEmpty(schemaName))
            {
                schemaNameString = ParserName.Parse(schemaName).ToString();
                schemaNameString = string.IsNullOrWhiteSpace(schemaNameString) ? "dbo" : schemaNameString;
            }

            var syncTable = new SyncTable(triggerNameNormalized, schemaNameString);

            using (var sqlCommand = new SqlCommand(command, connection))
            {
                sqlCommand.Parameters.AddWithValue("@triggerName", triggerNameString);
                sqlCommand.Parameters.AddWithValue("@schemaName", schemaNameString);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                using (var reader = await sqlCommand.ExecuteReaderAsync().ConfigureAwait(false))
                    syncTable.Load(reader);

                if (!alreadyOpened)
                    connection.Close();

            }
            return syncTable;
        }

        /// <summary>
        /// Get columns for table
        /// </summary>
        public static async Task<SyncTable> GetColumnsForTableAsync(string tableName, string schemaName, SqlConnection connection, SqlTransaction transaction)
        {

            var commandColumn = $"Select col.name as name, " +
                                $"col.column_id,  " +
                                $"typ.name as [type],  " +
                                $"col.max_length,  " +
                                $"col.precision,  " +
                                $"col.scale,  " +
                                $"col.is_nullable,  " +
                                $"col.is_computed,  " +
                                $"col.is_identity,  " +
                                $"ind.is_unique,  " +
                                $"ident_seed(sch.name + '.' + tbl.name) AS seed, " +
                                $"ident_incr(sch.name + '.' + tbl.name) AS step, " +
                                $"object_definition(col.default_object_id) AS defaultvalue " +
                                $"  from sys.columns as col " +
                                $"  Inner join sys.tables as tbl on tbl.object_id = col.object_id " +
                                $"  Inner join sys.schemas as sch on tbl.schema_id = sch.schema_id " +
                                $"  Inner Join sys.systypes typ on typ.xusertype = col.system_type_id " +
                                $"  Left outer join sys.indexes ind on ind.object_id = col.object_id and ind.index_id = col.column_id " +
                                $"  Where tbl.name = @tableName and sch.name = @schemaName ";

            var tableNameNormalized = ParserName.Parse(tableName).Unquoted().Normalized().ToString();
            var tableNameString = ParserName.Parse(tableName).ToString();

            var schemaNameString = "dbo";
            if (!string.IsNullOrEmpty(schemaName))
            {
                schemaNameString = ParserName.Parse(schemaName).ToString();
                schemaNameString = string.IsNullOrWhiteSpace(schemaNameString) ? "dbo" : schemaNameString;
            }

            var syncTable = new SyncTable(tableNameNormalized);
            using (var sqlCommand = new SqlCommand(commandColumn, connection))
            {
                sqlCommand.Parameters.AddWithValue("@tableName", tableNameString);
                sqlCommand.Parameters.AddWithValue("@schemaName", schemaNameString);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                using (var reader = await sqlCommand.ExecuteReaderAsync().ConfigureAwait(false))
                    syncTable.Load(reader);

                if (!alreadyOpened)
                    connection.Close();

            }
            return syncTable;
        }

        /// <summary>
        /// Get columns for table
        /// </summary>
        public static async Task<SyncTable> GetPrimaryKeysForTableAsync(string tableName, string schemaName, SqlConnection connection, SqlTransaction transaction)
        {

            var commandColumn = @"select ind.name, col.name as columnName, ind_col.column_id, ind_col.key_ordinal 
                                  from sys.indexes ind
                                  left outer join sys.index_columns ind_col on ind_col.object_id = ind.object_id and ind_col.index_id = ind.index_id
                                  inner join sys.columns col on col.object_id = ind_col.object_id and col.column_id = ind_col.column_id
                                  inner join sys.tables tbl on tbl.object_id = ind.object_id
                                  inner join sys.schemas as sch on tbl.schema_id = sch.schema_id
                                  where tbl.name = @tableName and sch.name = @schemaName and ind.index_id >= 0 and ind.type <> 3 and ind.type <> 4 and ind.is_hypothetical = 0 and ind.is_primary_key = 1
                                  order by ind_col.column_id";

            var tableNameNormalized = ParserName.Parse(tableName).Unquoted().Normalized().ToString();
            var tableNameString = ParserName.Parse(tableName).ToString();

            var schemaNameString = "dbo";
            if (!string.IsNullOrEmpty(schemaName))
            {
                schemaNameString = ParserName.Parse(schemaName).ToString();
                schemaNameString = string.IsNullOrWhiteSpace(schemaNameString) ? "dbo" : schemaNameString;
            }

            var syncTable = new SyncTable(tableNameNormalized);
            using (var sqlCommand = new SqlCommand(commandColumn, connection))
            {
                sqlCommand.Parameters.AddWithValue("@tableName", tableNameString);
                sqlCommand.Parameters.AddWithValue("@schemaName", schemaNameString);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;


                using (var reader = await sqlCommand.ExecuteReaderAsync().ConfigureAwait(false))
                    syncTable.Load(reader);

                if (!alreadyOpened)
                    connection.Close();


            }
            return syncTable;
        }

        public static async Task<SyncTable> GetRelationsForTableAsync(SqlConnection connection, SqlTransaction transaction, string tableName, string schemaName)
        {

            var commandRelations = @"
                SELECT f.name AS ForeignKey,
                    constraint_column_id as ForeignKeyOrder,
                    SCHEMA_NAME (f.schema_id)  AS SchemaName,
                    OBJECT_NAME(f.parent_object_id) AS TableName,
                    COL_NAME(fc.parent_object_id, fc.parent_column_id) AS ColumnName,
                    SCHEMA_NAME (reft.schema_id) AS ReferenceSchemaName,
                    OBJECT_NAME (f.referenced_object_id)  AS ReferenceTableName,
                    COL_NAME(fc.referenced_object_id, fc.referenced_column_id) AS ReferenceColumnName
                FROM sys.foreign_keys AS f
                INNER JOIN sys.foreign_key_columns AS fc ON f.OBJECT_ID = fc.constraint_object_id
                INNER JOIN sys.tables reft on reft.object_id =  f.referenced_object_id
                WHERE OBJECT_NAME(f.parent_object_id) = @tableName AND SCHEMA_NAME(f.schema_id) = @schemaName";

            var tableNameNormalized = ParserName.Parse(tableName).Unquoted().Normalized().ToString();
            var tableNameString = ParserName.Parse(tableName).ToString();

            var schemaNameString = ParserName.Parse(schemaName).ToString();
            // default as dbo
            schemaNameString = string.IsNullOrEmpty(schemaNameString) ? "dbo" : schemaNameString;

            var syncTable = new SyncTable(tableNameNormalized, schemaNameString);

            using (var sqlCommand = new SqlCommand(commandRelations, connection))
            {
                sqlCommand.Parameters.AddWithValue("@tableName", tableNameString);
                sqlCommand.Parameters.AddWithValue("@schemaName", schemaNameString);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                using (var reader = await sqlCommand.ExecuteReaderAsync().ConfigureAwait(false))
                    syncTable.Load(reader);

                if (!alreadyOpened)
                    connection.Close();
            }
            return syncTable;
        }

        public static async Task DropProcedureIfExistsAsync(SqlConnection connection, SqlTransaction transaction, int commandTimout, string quotedProcedureName)
        {
            var procName = ParserName.Parse(quotedProcedureName).ToString();
            using (var sqlCommand = new SqlCommand(string.Format(CultureInfo.InvariantCulture, "IF EXISTS (SELECT * FROM sys.procedures p JOIN sys.schemas s ON s.schema_id = p.schema_id WHERE p.name = @procName AND s.name = @schemaName) DROP PROCEDURE {0}", quotedProcedureName), connection))
            {
                sqlCommand.CommandTimeout = commandTimout;
                sqlCommand.Parameters.AddWithValue("@procName", procName);
                sqlCommand.Parameters.AddWithValue("@schemaName", SqlManagementUtils.GetUnquotedSqlSchemaName(ParserName.Parse(quotedProcedureName)));

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;


                await sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

                if (!alreadyOpened)
                    connection.Close();


            }
        }

        public static async Task DropTableIfExistsAsync(string tableName, string schemaName, SqlConnection connection, SqlTransaction transaction)
        {
            var pTableName = ParserName.Parse(tableName).Unquoted().ToString();
            var pSchemaName = ParserName.Parse(schemaName).Unquoted().ToString();
            pSchemaName = string.IsNullOrEmpty(schemaName) ? "dbo" : pSchemaName;

            var quotedTableName = $"{pSchemaName}.{pTableName}";

            var commandText = $"IF EXISTS " +
                $"(SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name = @tableName AND s.name = @schemaName) " +
                $"DROP TABLE {quotedTableName}";

            using var sqlCommand = new SqlCommand(commandText, connection);

            sqlCommand.Parameters.AddWithValue("@tableName", pTableName);
            sqlCommand.Parameters.AddWithValue("@schemaName", pSchemaName);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            if (!alreadyOpened)
                await connection.OpenAsync().ConfigureAwait(false);

            sqlCommand.Transaction = transaction;

            await sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (!alreadyOpened)
                connection.Close();
        }

        public static async Task DropTriggerIfExistsAsync(SqlConnection connection, SqlTransaction transaction, int commandTimeout, string quotedTriggerName)
        {
            var triggerName = ParserName.Parse(quotedTriggerName).ToString();

            using (var sqlCommand = new SqlCommand(string.Format(CultureInfo.InvariantCulture, "IF EXISTS (SELECT tr.name FROM sys.triggers tr JOIN sys.tables t ON tr.parent_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE tr.name = @triggerName and s.name = @schemaName) DROP TRIGGER {0}", quotedTriggerName), connection))
            {
                sqlCommand.CommandTimeout = commandTimeout;
                sqlCommand.Parameters.AddWithValue("@triggerName", triggerName);
                sqlCommand.Parameters.AddWithValue("@schemaName", SqlManagementUtils.GetUnquotedSqlSchemaName(ParserName.Parse(quotedTriggerName)));

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;


                await sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

                if (!alreadyOpened)
                    connection.Close();


            }
        }

        public static async Task DropTypeIfExistsAsync(SqlConnection connection, SqlTransaction transaction, int commandTimeout, string quotedTypeName)
        {
            var typeName = ParserName.Parse(quotedTypeName).ToString();

            using (var sqlCommand = new SqlCommand(string.Format(CultureInfo.InvariantCulture, "IF EXISTS (SELECT * FROM sys.types t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name = @typeName AND s.name = @schemaName) DROP TYPE {0}", quotedTypeName), connection))
            {
                sqlCommand.CommandTimeout = commandTimeout;
                sqlCommand.Parameters.AddWithValue("@typeName", typeName);
                sqlCommand.Parameters.AddWithValue("@schemaName", SqlManagementUtils.GetUnquotedSqlSchemaName(ParserName.Parse(quotedTypeName)));

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                await sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

                if (!alreadyOpened)
                    connection.Close();


            }
        }


        public static string GetUnquotedSqlSchemaName(ParserName parser)
        {
            if (string.IsNullOrEmpty(parser.SchemaName))
                return "dbo";

            return parser.SchemaName;
        }

        public static async Task<bool> IsChangeTrackingEnabledAsync(SqlConnection connection, SqlTransaction transaction)
        {
            bool flag;
            string commandText = @"if (exists(
                                Select* from sys.change_tracking_databases ct
                                Inner join sys.databases d on d.database_id = ct.database_id
                                where d.name = @databaseName)) 
                                Select 1 Else Select 0";

            using (var sqlCommand = new SqlCommand(commandText, connection))
            {
                sqlCommand.Parameters.AddWithValue("@databaseName", connection.Database);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                var result = await sqlCommand.ExecuteScalarAsync().ConfigureAwait(false);

                flag = (int)result != 0;

                if (!alreadyOpened)
                    connection.Close();
            }
            return flag;

        }


        public static async Task<(string DatabaseName, string EngineVersion)> GetHelloAsync(SqlConnection connection, SqlTransaction transaction)
        {
            string dbName = null;
            string dbVersion = null;

            using (DbCommand dbCommand = connection.CreateCommand())
            {
                dbCommand.CommandText = "SELECT name, @@VERSION as version FROM sys.databases WHERE name = @databaseName;";

                var sqlParameter = new SqlParameter()
                {
                    ParameterName = "@databaseName",
                    Value = connection.Database
                };
                dbCommand.Parameters.Add(sqlParameter);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                dbCommand.Transaction = transaction;

                using (var reader = await dbCommand.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (reader.HasRows)
                    {
                        reader.Read();

                        dbName = reader.GetString(0);
                        dbVersion = reader.GetString(1);
                    }
                }

                if (!alreadyOpened)
                    connection.Close();
            }
            return (dbName, dbVersion);
        }

        public static async Task<bool> DatabaseExistsAsync(SqlConnection connection, SqlTransaction transaction)
        {
            bool tableExist;

            using (DbCommand dbCommand = connection.CreateCommand())
            {
                dbCommand.CommandText = "IF EXISTS (SELECT * FROM sys.databases WHERE name = @databaseName) SELECT 1 ELSE SELECT 0";

                var sqlParameter = new SqlParameter()
                {
                    ParameterName = "@databaseName",
                    Value = connection.Database
                };
                dbCommand.Parameters.Add(sqlParameter);

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                dbCommand.Transaction = transaction;

                var result = await dbCommand.ExecuteScalarAsync().ConfigureAwait(false);

                tableExist = (int)result != 0;

                if (!alreadyOpened)
                    connection.Close();
            }
            return tableExist;
        }

        public static async Task<bool> ProcedureExistsAsync(SqlConnection connection, SqlTransaction transaction, string quotedProcedureName)
        {
            bool flag;
            var procedureName = ParserName.Parse(quotedProcedureName).ToString();

            using (var sqlCommand = new SqlCommand("IF EXISTS (SELECT * FROM sys.procedures p JOIN sys.schemas s ON s.schema_id = p.schema_id WHERE p.name = @procName AND s.name = @schemaName) SELECT 1 ELSE SELECT 0", connection))
            {
                sqlCommand.Parameters.AddWithValue("@procName", procedureName);
                sqlCommand.Parameters.AddWithValue("@schemaName", SqlManagementUtils.GetUnquotedSqlSchemaName(ParserName.Parse(quotedProcedureName)));

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                var result = await sqlCommand.ExecuteScalarAsync().ConfigureAwait(false);

                flag = (int)result != 0;

                if (!alreadyOpened)
                    connection.Close();


            }
            return flag;
        }

        public static async Task<bool> TableExistsAsync(string tableName, string schemaName, SqlConnection connection, SqlTransaction transaction)
        {
            var pTableName = ParserName.Parse(tableName).Unquoted().ToString();
            var pSchemaName = ParserName.Parse(schemaName).Unquoted().ToString();
            pSchemaName = string.IsNullOrEmpty(schemaName) ? "dbo" : pSchemaName;

            using DbCommand dbCommand = connection.CreateCommand();

            dbCommand.CommandText = "IF EXISTS (SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name = @tableName AND s.name = @schemaName) SELECT 1 ELSE SELECT 0";

            SqlParameter sqlParameter = new SqlParameter()
            {
                ParameterName = "@tableName",
                Value = pTableName
            };
            dbCommand.Parameters.Add(sqlParameter);

            sqlParameter = new SqlParameter()
            {
                ParameterName = "@schemaName",
                Value = pSchemaName
            };
            dbCommand.Parameters.Add(sqlParameter);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            if (!alreadyOpened)
                await connection.OpenAsync().ConfigureAwait(false);

            dbCommand.Transaction = transaction;

            var result = await dbCommand.ExecuteScalarAsync().ConfigureAwait(false);

            var tableExist = (int)result != 0;

            if (!alreadyOpened)
                connection.Close();



            return tableExist;
        }

        public static async Task<bool> SchemaExistsAsync(string schemaName, SqlConnection connection, SqlTransaction transaction)
        {
            bool schemaExist;

            if (string.IsNullOrEmpty(schemaName))
                return true;

            var pSchemaName = ParserName.Parse(schemaName);
            var sName = pSchemaName.Quoted();


            using DbCommand dbCommand = connection.CreateCommand();
            dbCommand.CommandText = "IF EXISTS (SELECT sch.name FROM sys.schemas sch WHERE sch.name = @schemaName) SELECT 1 ELSE SELECT 0";

            var sqlParameter = new SqlParameter()
            {
                ParameterName = "@schemaName",
                Value = sName
            };
            dbCommand.Parameters.Add(sqlParameter);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            if (!alreadyOpened)
                await connection.OpenAsync().ConfigureAwait(false);

            dbCommand.Transaction = transaction;

            var result = await dbCommand.ExecuteScalarAsync().ConfigureAwait(false);

            schemaExist = (int)result != 0;

            if (!alreadyOpened)
                connection.Close();


            return schemaExist;
        }

        public static async Task<bool> TriggerExistsAsync(string quotedTriggerName, SqlConnection connection, SqlTransaction transaction)
        {
            bool triggerExist;
            var triggerName = ParserName.Parse(quotedTriggerName).ToString();

            using (var sqlCommand = new SqlCommand("IF EXISTS (SELECT tr.name FROM sys.triggers tr JOIN sys.tables t ON tr.parent_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE tr.name = @triggerName and s.name = @schemaName) SELECT 1 ELSE SELECT 0", connection))
            {
                sqlCommand.Parameters.AddWithValue("@triggerName", triggerName);
                sqlCommand.Parameters.AddWithValue("@schemaName", SqlManagementUtils.GetUnquotedSqlSchemaName(ParserName.Parse(quotedTriggerName)));

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                var result = await sqlCommand.ExecuteScalarAsync().ConfigureAwait(false);

                triggerExist = (int)result != 0;

                if (!alreadyOpened)
                    connection.Close();


            }
            return triggerExist;
        }

        public static async Task<bool> TypeExistsAsync(string quotedTypeName, SqlConnection connection, SqlTransaction transaction)
        {
            bool typeExist;

            var columnName = ParserName.Parse(quotedTypeName).ToString();

            using (SqlCommand sqlCommand = new SqlCommand("IF EXISTS (SELECT * FROM sys.types t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name = @typeName AND s.name = @schemaName) SELECT 1 ELSE SELECT 0", connection))
            {
                sqlCommand.Parameters.AddWithValue("@typeName", columnName);
                sqlCommand.Parameters.AddWithValue("@schemaName", SqlManagementUtils.GetUnquotedSqlSchemaName(ParserName.Parse(quotedTypeName)));

                bool alreadyOpened = connection.State == ConnectionState.Open;

                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                sqlCommand.Transaction = transaction;

                var result = await sqlCommand.ExecuteScalarAsync().ConfigureAwait(false);

                typeExist = (int)result != 0;

                if (!alreadyOpened)
                    connection.Close();


            }
            return typeExist;
        }

        public static string JoinTwoTablesOnClause(IEnumerable<string> columns, string leftName, string rightName)
        {
            var stringBuilder = new StringBuilder();
            string strRightName = (string.IsNullOrEmpty(rightName) ? string.Empty : string.Concat(rightName, "."));
            string strLeftName = (string.IsNullOrEmpty(leftName) ? string.Empty : string.Concat(leftName, "."));

            string str = "";
            foreach (var column in columns)
            {
                var quotedColumn = ParserName.Parse(column).Quoted().ToString();

                stringBuilder.Append(str);
                stringBuilder.Append(strLeftName);
                stringBuilder.Append(quotedColumn);
                stringBuilder.Append(" = ");
                stringBuilder.Append(strRightName);
                stringBuilder.Append(quotedColumn);

                str = " AND ";
            }
            return stringBuilder.ToString();
        }

        public static string ColumnsAndParameters(IEnumerable<string> columns, string fromPrefix)
        {
            StringBuilder stringBuilder = new StringBuilder();
            string strFromPrefix = string.IsNullOrEmpty(fromPrefix) ? string.Empty : string.Concat(fromPrefix, ".");
            string str1 = "";
            foreach (var column in columns)
            {
                var quotedColumn = ParserName.Parse(column).Quoted().ToString();
                var unquotedColumn = ParserName.Parse(column).Unquoted().Normalized().ToString();

                stringBuilder.Append(str1);
                stringBuilder.Append(strFromPrefix);
                stringBuilder.Append(quotedColumn);
                stringBuilder.Append(" = ");
                stringBuilder.Append($"@{unquotedColumn}");
                str1 = " AND ";
            }
            return stringBuilder.ToString();
        }

        public static string CommaSeparatedUpdateFromParameters(SyncTable table, string fromPrefix = "")
        {
            StringBuilder stringBuilder = new StringBuilder();
            string strFromPrefix = (string.IsNullOrEmpty(fromPrefix) ? string.Empty : string.Concat(fromPrefix, "."));
            string strSeparator = "";
            foreach (var mutableColumn in table.GetMutableColumns(false))
            {
                var quotedColumn = ParserName.Parse(mutableColumn).Quoted().ToString();
                var unquotedColumn = ParserName.Parse(mutableColumn).Unquoted().Normalized().ToString();

                stringBuilder.AppendLine($"{strSeparator} {strFromPrefix}{quotedColumn} = @{unquotedColumn}");
                strSeparator = ", ";
            }
            return stringBuilder.ToString();

        }




    }
}
