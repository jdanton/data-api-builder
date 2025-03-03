// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Service.Configurations;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.Models;
using Azure.DataApiBuilder.Service.Parsers;
using Azure.DataApiBuilder.Service.Resolvers;
using Microsoft.Extensions.Logging;
using static Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLNaming;

namespace Azure.DataApiBuilder.Service.Services
{
    /// <summary>
    /// Reads schema information from the database to make it
    /// available for the GraphQL/REST services.
    /// </summary>
    public abstract class SqlMetadataProvider<ConnectionT, DataAdapterT, CommandT> : ISqlMetadataProvider
        where ConnectionT : DbConnection, new()
        where DataAdapterT : DbDataAdapter, new()
        where CommandT : DbCommand, new()
    {
        private ODataParser _oDataParser = new();

        private readonly DatabaseType _databaseType;

        private readonly Dictionary<string, Entity> _entities;

        // Dictionary mapping singular graphql types to entity name keys in the configuration
        private readonly Dictionary<string, string> _graphQLSingularTypeToEntityNameMap = new();

        // Dictionary containing mapping of graphQL stored procedure exposed query/mutation name
        // to their corresponding entity names defined in the config.
        public Dictionary<string, string> GraphQLStoredProcedureExposedNameToEntityNameMap { get; set; } = new();

        // Contains all the referencing and referenced columns for each pair
        // of referencing and referenced tables.
        public Dictionary<RelationShipPair, ForeignKeyDefinition>? PairToFkDefinition { get; set; }

        protected IQueryExecutor QueryExecutor { get; }

        private const int NUMBER_OF_RESTRICTIONS = 4;

        protected string ConnectionString { get; init; }

        protected IQueryBuilder SqlQueryBuilder { get; init; }

        protected DataSet EntitiesDataSet { get; init; }

        private RuntimeConfigProvider _runtimeConfigProvider;

        private Dictionary<string, Dictionary<string, string>> EntityBackingColumnsToExposedNames { get; } = new();

        private Dictionary<string, Dictionary<string, string>> EntityExposedNamesToBackingColumnNames { get; } = new();

        private Dictionary<string, string> EntityPathToEntityName { get; } = new();

        /// <summary>
        /// Maps an entity name to a DatabaseObject.
        /// </summary>
        public Dictionary<string, DatabaseObject> EntityToDatabaseObject { get; set; } =
            new(StringComparer.InvariantCulture);

        private readonly ILogger<ISqlMetadataProvider> _logger;

        public SqlMetadataProvider(
            RuntimeConfigProvider runtimeConfigProvider,
            IQueryExecutor queryExecutor,
            IQueryBuilder queryBuilder,
            ILogger<ISqlMetadataProvider> logger)
        {
            RuntimeConfig runtimeConfig = runtimeConfigProvider.GetRuntimeConfiguration();
            _runtimeConfigProvider = runtimeConfigProvider;
            _databaseType = runtimeConfig.DatabaseType;
            _entities = runtimeConfig.Entities;
            _graphQLSingularTypeToEntityNameMap = runtimeConfig.GraphQLSingularTypeToEntityNameMap;
            _logger = logger;
            foreach (KeyValuePair<string, Entity> entity in _entities)
            {
                entity.Value.TryPopulateSourceFields();
                if (runtimeConfigProvider.GetRuntimeConfiguration().RestGlobalSettings.Enabled)
                {
                    _logger.LogInformation($"{entity.Key} path: {runtimeConfigProvider.RestPath}/{entity.Key}");
                }
                else
                {
                    _logger.LogInformation($"REST calls are disabled for Entity: {entity.Key}");
                }
            }

            ConnectionString = runtimeConfig.ConnectionString;
            EntitiesDataSet = new();
            SqlQueryBuilder = queryBuilder;
            QueryExecutor = queryExecutor;
        }

        /// <inheritdoc />
        public ODataParser GetODataParser()
        {
            return _oDataParser;
        }

        /// <inheritdoc />
        public DatabaseType GetDatabaseType()
        {
            return _databaseType;
        }

        /// <summary>
        /// Obtains the underlying query builder.
        /// </summary>
        /// <returns></returns>
        public IQueryBuilder GetQueryBuilder()
        {
            return SqlQueryBuilder;
        }

        /// <inheritdoc />
        public virtual string GetSchemaName(string entityName)
        {
            if (!EntityToDatabaseObject.TryGetValue(entityName, out DatabaseObject? databaseObject))
            {
                throw new DataApiBuilderException(message: $"Table Definition for {entityName} has not been inferred.",
                    statusCode: HttpStatusCode.InternalServerError,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.EntityNotFound);
            }

            return databaseObject!.SchemaName;
        }

        /// <inheritdoc />
        public string GetDatabaseObjectName(string entityName)
        {
            if (!EntityToDatabaseObject.TryGetValue(entityName, out DatabaseObject? databaseObject))
            {
                throw new DataApiBuilderException(message: $"Table Definition for {entityName} has not been inferred.",
                    statusCode: HttpStatusCode.InternalServerError,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.EntityNotFound);
            }

            return databaseObject!.Name;
        }

        /// <inheritdoc />
        public SourceDefinition GetSourceDefinition(string entityName)
        {
            if (!EntityToDatabaseObject.TryGetValue(entityName, out DatabaseObject? databaseObject))
            {
                throw new DataApiBuilderException(message: $"Table Definition for {entityName} has not been inferred.",
                    statusCode: HttpStatusCode.InternalServerError,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.EntityNotFound);
            }

            return databaseObject.SourceDefinition;
        }

        /// <inheritdoc />
        public StoredProcedureDefinition GetStoredProcedureDefinition(string entityName)
        {
            if (!EntityToDatabaseObject.TryGetValue(entityName, out DatabaseObject? databaseObject))
            {
                throw new DataApiBuilderException(message: $"Stored Procedure Definition for {entityName} has not been inferred.",
                    statusCode: HttpStatusCode.InternalServerError,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.EntityNotFound);
            }

            return ((DatabaseStoredProcedure)databaseObject).StoredProcedureDefinition;
        }

        /// <inheritdoc />
        public bool TryGetExposedColumnName(string entityName, string backingFieldName, out string? name)
        {
            return EntityBackingColumnsToExposedNames[entityName].TryGetValue(backingFieldName, out name);
        }

        /// <inheritdoc />
        public bool TryGetBackingColumn(string entityName, string field, [NotNullWhen(true)] out string? name)
        {
            return EntityExposedNamesToBackingColumnNames[entityName].TryGetValue(field, out name);
        }

        /// <inheritdoc />
        public virtual bool TryGetEntityNameFromPath(string entityPathName, [NotNullWhen(true)] out string? entityName)
        {
            return EntityPathToEntityName.TryGetValue(entityPathName, out entityName);
        }

        /// <inheritdoc />
        public IDictionary<string, DatabaseObject> GetEntityNamesAndDbObjects()
        {
            return EntityToDatabaseObject;
        }

        /// <inheritdoc />
        public string GetEntityName(string graphQLType)
        {
            if (_entities.ContainsKey(graphQLType))
            {
                return graphQLType;
            }

            if (!_graphQLSingularTypeToEntityNameMap.TryGetValue(graphQLType, out string? entityName))
            {
                throw new DataApiBuilderException(
                    "GraphQL type doesn't match any entity name or singular type in the runtime config.",
                    System.Net.HttpStatusCode.BadRequest,
                    DataApiBuilderException.SubStatusCodes.BadRequest);
            }

            return entityName!;
        }

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            GenerateDatabaseObjectForEntities();
            await PopulateObjectDefinitionForEntities();
            GenerateExposedToBackingColumnMapsForEntities();
            // When IsLateConfigured is true we are in a hosted scenario and do not reveal primary key information.
            if (!_runtimeConfigProvider.IsLateConfigured)
            {
                LogPrimaryKeys();
            }

            GenerateRestPathToEntityMap();
            InitODataParser();
            timer.Stop();
            _logger.LogTrace($"Done inferring Sql database schema in {timer.ElapsedMilliseconds}ms.");
        }

        /// <summary>
        /// Log Primary key information. Function only called when not
        /// in a hosted scenario. Log relevant information about Primary keys
        /// including backing and exposed names, type, isNullable, and isAutoGenerated.
        /// </summary>
        private void LogPrimaryKeys()
        {
            ColumnDefinition column;
            foreach (string entityName in _entities.Keys)
            {
                SourceDefinition sourceDefinition = GetSourceDefinition(entityName);
                _logger.LogDebug($"Logging primary key information for entity: {entityName}.");
                foreach (string pK in sourceDefinition.PrimaryKey)
                {
                    string? exposedPKeyName;
                    column = sourceDefinition.Columns[pK];
                    if (TryGetExposedColumnName(entityName, pK, out exposedPKeyName))
                    {
                        _logger.LogDebug($"Primary key column name: {pK}\n" +
                        $"      Primary key mapped name: {exposedPKeyName}\n" +
                        $"      Type: {column.SystemType.Name}\n" +
                        $"      IsNullable: {column.IsNullable}\n" +
                        $"      IsAutoGenerated: {column.IsAutoGenerated}");
                    }
                }
            }
        }

        /// <summary>
        /// Verify that the stored procedure exists in the database schema, then populate its database object parameters accordingly
        /// </summary>
        private async Task FillSchemaForStoredProcedureAsync(
            Entity procedureEntity,
            string entityName,
            string schemaName,
            string storedProcedureSourceName,
            StoredProcedureDefinition storedProcedureDefinition)
        {
            using ConnectionT conn = new();
            conn.ConnectionString = ConnectionString;
            await QueryExecutor.SetManagedIdentityAccessTokenIfAnyAsync(conn);
            await conn.OpenAsync();

            string tablePrefix = GetTablePrefix(conn.Database, schemaName);

            string[] procedureRestrictions = new string[NUMBER_OF_RESTRICTIONS];

            // To restrict the parameters for the current stored procedure, specify its name
            procedureRestrictions[0] = conn.Database;
            procedureRestrictions[1] = schemaName;
            procedureRestrictions[2] = storedProcedureSourceName;

            DataTable procedureMetadata = await conn.GetSchemaAsync(collectionName: "Procedures", restrictionValues: procedureRestrictions);

            // Stored procedure does not exist in DB schema
            if (procedureMetadata.Rows.Count == 0)
            {
                throw new DataApiBuilderException(
                    message: $"No stored procedure definition found for the given database object {storedProcedureSourceName}",
                    statusCode: HttpStatusCode.ServiceUnavailable,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization);
            }

            // Each row in the procedureParams DataTable corresponds to a single parameter
            DataTable parameterMetadata = await conn.GetSchemaAsync(collectionName: "ProcedureParameters", restrictionValues: procedureRestrictions);

            // For each row/parameter, add an entry to StoredProcedureDefinition.Parameters dictionary
            foreach (DataRow row in parameterMetadata.Rows)
            {
                // row["DATA_TYPE"] has value type string so a direct cast to System.Type is not supported.
                Type systemType = SqlToCLRType((string)row["DATA_TYPE"]);
                // Add to parameters dictionary without the leading @ sign
                storedProcedureDefinition.Parameters.TryAdd(((string)row["PARAMETER_NAME"])[1..],
                    new()
                    {
                        SystemType = systemType,
                        DbType = DbTypeHelper.GetDbTypeFromSystemType(systemType)
                    }
                );
            }

            // Loop through parameters specified in config, throw error if not found in schema
            // else set runtime config defined default values.
            // Note: we defer type checking of parameters specified in config until request time
            Dictionary<string, object>? configParameters = procedureEntity.Parameters;
            if (configParameters is not null)
            {
                foreach ((string configParamKey, object configParamValue) in configParameters)
                {
                    if (!storedProcedureDefinition.Parameters.TryGetValue(configParamKey, out ParameterDefinition? parameterDefinition))
                    {
                        throw new DataApiBuilderException(
                            message: $"Could not find parameter \"{configParamKey}\" specified in config for procedure \"{schemaName}.{storedProcedureSourceName}\"",
                            statusCode: HttpStatusCode.ServiceUnavailable,
                            subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization);
                    }
                    else
                    {
                        parameterDefinition.HasConfigDefault = true;
                        parameterDefinition.ConfigDefaultValue = configParamValue is null ? null : configParamValue.ToString();
                    }
                }
            }

            // Generating exposed stored-procedure query/mutation name and adding to the dictionary mapping it to its entity name.
            GraphQLStoredProcedureExposedNameToEntityNameMap.TryAdd(GenerateStoredProcedureGraphQLFieldName(entityName, procedureEntity), entityName);
        }

        /// <summary>
        /// Takes a string version of a sql data type and returns its .NET common language runtime (CLR) counterpart
        /// </summary>
        public abstract Type SqlToCLRType(string sqlType);

        /// <summary>
        /// Generates the map used to find a given entity based
        /// on the path that will be used for that entity.
        /// </summary>
        private void GenerateRestPathToEntityMap()
        {
            RuntimeConfig runtimeConfig = _runtimeConfigProvider.GetRuntimeConfiguration();
            string graphQLGlobalPath = runtimeConfig.GraphQLGlobalSettings.Path;

            foreach (string entityName in _entities.Keys)
            {
                Entity entity = _entities[entityName];
                string path = GetEntityPath(entity, entityName).TrimStart('/');
                ValidateEntityAndGraphQLPathUniqueness(path, graphQLGlobalPath);

                if (!string.IsNullOrEmpty(path))
                {
                    EntityPathToEntityName[path] = entityName;
                }
            }
        }

        /// <summary>
        /// Validate that an Entity's REST path does not conflict with the developer configured
        /// or the internal default GraphQL path (/graphql).
        /// </summary>
        /// <param name="path">Entity's calculated REST path.</param>
        /// <param name="graphQLGlobalPath">Developer configured GraphQL Path</param>
        /// <exception cref="DataApiBuilderException"></exception>
        public static void ValidateEntityAndGraphQLPathUniqueness(string path, string graphQLGlobalPath)
        {
            // Handle case when path does not have forward slash (/) prefix
            // by adding one if not present or ignoring an existing slash.
            // entityName -> /entityName
            // /entityName -> /entityName (no change)
            if (!string.IsNullOrWhiteSpace(path) && path[0] != '/')
            {
                path = '/' + path;
            }

            if (string.Equals(path, graphQLGlobalPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, GlobalSettings.GRAPHQL_DEFAULT_PATH, StringComparison.OrdinalIgnoreCase))
            {
                throw new DataApiBuilderException(
                    message: "Entity's REST path conflicts with GraphQL reserved paths.",
                    statusCode: HttpStatusCode.ServiceUnavailable,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.ConfigValidationError);
            }
        }

        /// <summary>
        /// Deserialize and return the entity's path.
        /// </summary>
        /// <param name="entity">Entity object to get the path of.</param>
        /// <param name="entityName">name of the entity</param>
        /// <returns>route for the given Entity.</returns>
        private static string GetEntityPath(Entity entity, string entityName)
        {
            // if entity.Rest is null or true we just use entity name
            if (entity.Rest is null || ((JsonElement)entity.Rest).ValueKind is JsonValueKind.True)
            {
                return entityName;
            }

            // for false return empty string so we know not to add in caller
            if (((JsonElement)entity.Rest).ValueKind is JsonValueKind.False)
            {
                return string.Empty;
            }

            // otherwise we have to convert each part of the Rest property we want into correct objects
            // they are json element so this means deserializing at each step with case insensitivity
            JsonSerializerOptions options = RuntimeConfig.SerializerOptions;
            JsonElement restConfigElement = (JsonElement)entity.Rest;
            if (entity.ObjectType is SourceType.StoredProcedure)
            {
                if (restConfigElement.TryGetProperty("path", out JsonElement path))
                {
                    if (path.ValueKind is JsonValueKind.True || path.ValueKind is JsonValueKind.False)
                    {
                        bool restEnabled = JsonSerializer.Deserialize<bool>(path, options)!;
                        if (restEnabled)
                        {
                            return entityName;
                        }
                        else
                        {
                            return string.Empty;
                        }
                    }
                    else
                    {
                        return JsonSerializer.Deserialize<string>(path, options)!;
                    }
                }
                else
                {
                    return entityName;
                }
            }
            else
            {
                RestEntitySettings rest = JsonSerializer.Deserialize<RestEntitySettings>((JsonElement)restConfigElement, options)!;
                if (rest.Path is not null)
                {
                    return JsonSerializer.Deserialize<string>((JsonElement)rest.Path, options)!;
                }
                else
                {
                    return entityName;
                }
            }
        }

        /// <summary>
        /// Returns the default schema name. Throws exception here since
        /// each derived class should override this method.
        /// </summary>
        /// <exception cref="NotSupportedException"></exception>
        public virtual string GetDefaultSchemaName()
        {
            throw new NotSupportedException($"Cannot get default schema " +
                $"name for database type {_databaseType}");
        }

        /// <summary>
        /// Creates a Database object with the given schema and table names.
        /// </summary>
        protected virtual DatabaseTable GenerateDbTable(string schemaName, string tableName)
        {
            return new(schemaName, tableName);
        }

        /// <summary>
        /// Builds the dictionary of parameters and their values required for the
        /// foreign key query.
        /// </summary>
        /// <param name="schemaNames"></param>
        /// <param name="tableNames"></param>
        /// <returns>The dictionary populated with parameters.</returns>
        protected virtual Dictionary<string, DbConnectionParam>
            GetForeignKeyQueryParams(
                string[] schemaNames,
                string[] tableNames)
        {
            Dictionary<string, DbConnectionParam> parameters = new();
            string[] schemaNameParams =
                BaseSqlQueryBuilder.CreateParams(
                    kindOfParam: BaseSqlQueryBuilder.SCHEMA_NAME_PARAM,
                    schemaNames.Count());
            string[] tableNameParams =
                BaseSqlQueryBuilder.CreateParams(
                    kindOfParam: BaseSqlQueryBuilder.TABLE_NAME_PARAM,
                    tableNames.Count());

            for (int i = 0; i < schemaNames.Count(); ++i)
            {
                parameters.Add(schemaNameParams[i], new(schemaNames[i], DbType.String));
            }

            for (int i = 0; i < tableNames.Count(); ++i)
            {
                parameters.Add(tableNameParams[i], new(tableNames[i], DbType.String));
            }

            return parameters;
        }

        /// <summary>
        /// Create a DatabaseObject for all the exposed entities.
        /// </summary>
        private void GenerateDatabaseObjectForEntities()
        {
            string schemaName, dbObjectName;
            Dictionary<string, DatabaseObject> sourceObjects = new();
            foreach ((string entityName, Entity entity)
                in _entities)
            {
                if (!EntityToDatabaseObject.ContainsKey(entityName))
                {
                    // Reuse the same Database object for multiple entities if they share the same source.
                    if (!sourceObjects.TryGetValue(entity.SourceName, out DatabaseObject? sourceObject))
                    {
                        // parse source name into a tuple of (schemaName, databaseObjectName)
                        (schemaName, dbObjectName) = ParseSchemaAndDbTableName(entity.SourceName)!;

                        // if specified as stored procedure in config,
                        // initialize DatabaseObject as DatabaseStoredProcedure,
                        // else with DatabaseTable (for tables) / DatabaseView (for views).

                        if (entity.ObjectType is SourceType.StoredProcedure)
                        {
                            sourceObject = new DatabaseStoredProcedure(schemaName, dbObjectName)
                            {
                                SourceType = entity.ObjectType,
                                StoredProcedureDefinition = new()
                            };
                        }
                        else if (entity.ObjectType is SourceType.Table)
                        {
                            sourceObject = new DatabaseTable()
                            {
                                SchemaName = schemaName,
                                Name = dbObjectName,
                                SourceType = entity.ObjectType,
                                TableDefinition = new()
                            };
                        }
                        else
                        {
                            sourceObject = new DatabaseView(schemaName, dbObjectName)
                            {
                                SchemaName = schemaName,
                                Name = dbObjectName,
                                SourceType = entity.ObjectType,
                                ViewDefinition = new()
                            };
                        }

                        sourceObjects.Add(entity.SourceName, sourceObject);
                    }

                    EntityToDatabaseObject.Add(entityName, sourceObject);

                    if (entity.Relationships is not null && entity.ObjectType is SourceType.Table)
                    {
                        AddForeignKeysForRelationships(entityName, entity, (DatabaseTable)sourceObject);
                    }
                }
            }
        }

        /// <summary>
        /// Adds a foreign key definition for each of the nested entities
        /// specified in the relationships section of this entity
        /// to gather the referencing and referenced columns from the database at a later stage.
        /// Sets the referencing and referenced tables based on the kind of relationship.
        /// If encounter a linking object, use that as the referencing table
        /// for the foreign key definition.
        /// There may not be a foreign key defined on the backend in which case
        /// the relationship.source.fields and relationship.target fields are mandatory.
        /// Initializing a definition here is an indication to find the foreign key
        /// between the referencing and referenced tables.
        /// </summary>
        /// <param name="entityName"></param>
        /// <param name="entity"></param>
        /// <param name="databaseTable"></param>
        /// <exception cref="InvalidOperationException"></exception>
        private void AddForeignKeysForRelationships(
            string entityName,
            Entity entity,
            DatabaseTable databaseTable)
        {
            RelationshipMetadata? relationshipData;
            SourceDefinition sourceDefinition = GetSourceDefinition(entityName);
            if (!sourceDefinition.SourceEntityRelationshipMap
                .TryGetValue(entityName, out relationshipData))
            {
                relationshipData = new();
                sourceDefinition.SourceEntityRelationshipMap.Add(entityName, relationshipData);
            }

            string targetSchemaName, targetDbTableName, linkingTableSchema, linkingTableName;
            foreach (Relationship relationship in entity.Relationships!.Values)
            {
                string targetEntityName = relationship.TargetEntity;
                if (!_entities.TryGetValue(targetEntityName, out Entity? targetEntity))
                {
                    throw new InvalidOperationException($"Target Entity {targetEntityName} should be one of the exposed entities.");
                }

                (targetSchemaName, targetDbTableName) = ParseSchemaAndDbTableName(targetEntity.SourceName)!;
                DatabaseTable targetDbTable = new(targetSchemaName, targetDbTableName);
                // If a linking object is specified,
                // give that higher preference and add two foreign keys for this targetEntity.
                if (relationship.LinkingObject is not null)
                {
                    (linkingTableSchema, linkingTableName) = ParseSchemaAndDbTableName(relationship.LinkingObject)!;
                    DatabaseTable linkingDbTable = new(linkingTableSchema, linkingTableName);
                    AddForeignKeyForTargetEntity(
                        targetEntityName,
                        referencingDbTable: linkingDbTable,
                        referencedDbTable: databaseTable,
                        referencingColumns: relationship.LinkingSourceFields,
                        referencedColumns: relationship.SourceFields,
                        relationshipData);

                    AddForeignKeyForTargetEntity(
                        targetEntityName,
                        referencingDbTable: linkingDbTable,
                        referencedDbTable: targetDbTable,
                        referencingColumns: relationship.LinkingTargetFields,
                        referencedColumns: relationship.TargetFields,
                        relationshipData);
                }
                else if (relationship.Cardinality == Cardinality.One)
                {
                    // For Many-One OR One-One Relationships, optimistically
                    // add foreign keys from either sides in the hopes of finding their metadata
                    // at a later stage when we query the database about foreign keys.
                    // Both or either of these may be present if its a One-One relationship,
                    // The second fk would not be present if its a Many-One relationship.
                    // When the configuration file doesn't specify how to relate these entities,
                    // at least 1 of the following foreign keys should be present.

                    // Adding this foreign key in the hopes of finding a foreign key
                    // in the underlying database object of the source entity referencing
                    // the target entity.
                    // This foreign key may NOT exist for either of the following reasons:
                    // a. this source entity is related to the target entity in an One-to-One relationship
                    // but the foreign key was added to the target entity's underlying source
                    // This is covered by the foreign key below.
                    // OR
                    // b. no foreign keys were defined at all.
                    AddForeignKeyForTargetEntity(
                        targetEntityName,
                        referencingDbTable: databaseTable,
                        referencedDbTable: targetDbTable,
                        referencingColumns: relationship.SourceFields,
                        referencedColumns: relationship.TargetFields,
                        relationshipData);

                    // Adds another foreign key defintion with targetEntity.GetSourceName()
                    // as the referencingTableName - in the situation of a One-to-One relationship
                    // and the foreign key is defined in the source of targetEntity.
                    // This foreign key WILL NOT exist if its a Many-One relationship.
                    AddForeignKeyForTargetEntity(
                        targetEntityName,
                        referencingDbTable: targetDbTable,
                        referencedDbTable: databaseTable,
                        referencingColumns: relationship.TargetFields,
                        referencedColumns: relationship.SourceFields,
                        relationshipData);
                }
                else if (relationship.Cardinality is Cardinality.Many)
                {
                    // Case of publisher(One)-books(Many)
                    // we would need to obtain the foreign key information from the books table
                    // about the publisher id so we can do the join.
                    // so, the referencingTable is the source of the target entity.
                    AddForeignKeyForTargetEntity(
                        targetEntityName,
                        referencingDbTable: targetDbTable,
                        referencedDbTable: databaseTable,
                        referencingColumns: relationship.TargetFields,
                        referencedColumns: relationship.SourceFields,
                        relationshipData);
                }
            }
        }

        /// <summary>
        /// Adds a new foreign key definition for the target entity
        /// in the relationship metadata.
        /// </summary>
        private static void AddForeignKeyForTargetEntity(
            string targetEntityName,
            DatabaseTable referencingDbTable,
            DatabaseTable referencedDbTable,
            string[]? referencingColumns,
            string[]? referencedColumns,
            RelationshipMetadata relationshipData)
        {
            ForeignKeyDefinition foreignKeyDefinition = new()
            {
                Pair = new()
                {
                    ReferencingDbTable = referencingDbTable,
                    ReferencedDbTable = referencedDbTable
                }
            };

            if (referencingColumns is not null)
            {
                foreignKeyDefinition.ReferencingColumns.AddRange(referencingColumns);
            }

            if (referencedColumns is not null)
            {
                foreignKeyDefinition.ReferencedColumns.AddRange(referencedColumns);
            }

            if (relationshipData
                .TargetEntityToFkDefinitionMap.TryGetValue(targetEntityName, out List<ForeignKeyDefinition>? foreignKeys))
            {
                foreignKeys.Add(foreignKeyDefinition);
            }
            else
            {
                relationshipData.TargetEntityToFkDefinitionMap
                    .Add(targetEntityName,
                        new List<ForeignKeyDefinition>() { foreignKeyDefinition });
            }
        }

        /// <summary>
        /// Helper function will parse the schema and database object name
        /// from the provided source string and sort out if a default schema
        /// should be used.
        /// </summary>
        /// <param name="source">source string to parse</param>
        /// <returns>The appropriate schema and db object name as a tuple of strings.</returns>
        /// <exception cref="DataApiBuilderException"></exception>
        public (string, string) ParseSchemaAndDbTableName(string source)
        {
            (string? schemaName, string dbTableName) = EntitySourceNamesParser.ParseSchemaAndTable(source)!;

            // if schemaName is empty we check if the DB type is postgresql
            // and if the schema name was included in the connection string
            // as a value associated with the keyword 'SearchPath'.
            // if the DB type is not postgresql or if the connection string
            // does not include the schema name, we use the default schema name.
            // if schemaName is not empty we must check if Database Type is MySql
            // and in this case we throw an exception since there should be no
            // schema name in this case.
            if (string.IsNullOrEmpty(schemaName))
            {
                // if DatabaseType is not postgresql will short circuit and use default
                if (_databaseType is not DatabaseType.postgresql ||
                    !PostgreSqlMetadataProvider.TryGetSchemaFromConnectionString(
                        connectionString: ConnectionString,
                        out schemaName))
                {
                    schemaName = GetDefaultSchemaName();
                }
            }
            else if (_databaseType is DatabaseType.mysql)
            {
                throw new DataApiBuilderException(message: $"Invalid database object name: \"{schemaName}.{dbTableName}\"",
                                               statusCode: System.Net.HttpStatusCode.ServiceUnavailable,
                                               subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization);
            }

            return (schemaName, dbTableName);
        }

        /// <summary>
        /// Enrich the entities in the runtime config with the
        /// object definition information needed by the runtime to serve requests.
        /// Populates table definition for entities specified as tables or views
        /// Populates procedure definition for entities specified as stored procedures
        /// </summary>
        private async Task PopulateObjectDefinitionForEntities()
        {
            foreach ((string entityName, Entity entity) in _entities)
            {
                SourceType entitySourceType = entity.ObjectType;
                if (entitySourceType is SourceType.StoredProcedure)
                {
                    await FillSchemaForStoredProcedureAsync(
                        entity,
                        entityName,
                        GetSchemaName(entityName),
                        GetDatabaseObjectName(entityName),
                        GetStoredProcedureDefinition(entityName));

                    if (GetDatabaseType() == DatabaseType.mssql)
                    {
                        await PopulateResultSetDefinitionsForStoredProcedureAsync(
                            GetSchemaName(entityName),
                            GetDatabaseObjectName(entityName),
                            GetStoredProcedureDefinition(entityName));
                    }
                }
                else if (entitySourceType is SourceType.Table)
                {
                    await PopulateSourceDefinitionAsync(
                        entityName,
                        GetSchemaName(entityName),
                        GetDatabaseObjectName(entityName),
                        GetSourceDefinition(entityName),
                        entity.KeyFields);
                }
                else
                {
                    ViewDefinition viewDefinition = (ViewDefinition)GetSourceDefinition(entityName);
                    await PopulateSourceDefinitionAsync(
                        entityName,
                        GetSchemaName(entityName),
                        GetDatabaseObjectName(entityName),
                        viewDefinition,
                        entity.KeyFields);
                }
            }

            await PopulateForeignKeyDefinitionAsync();
        }

        /// <summary>
        /// Queries DB to get the result fields name and type to
        /// populate the result set definition for entities specified as stored procedures
        /// </summary>
        private async Task PopulateResultSetDefinitionsForStoredProcedureAsync(
            string schemaName,
            string storedProcedureName,
            SourceDefinition sourceDefinition)
        {
            StoredProcedureDefinition storedProcedureDefinition = (StoredProcedureDefinition)sourceDefinition;
            string dbStoredProcedureName = $"{schemaName}.{storedProcedureName}";
            // Generate query to get result set details
            // of the stored procedure.
            string queryForResultSetDetails = SqlQueryBuilder.BuildStoredProcedureResultDetailsQuery(
                dbStoredProcedureName);

            // Execute the query to get columns' details.
            JsonArray? resultArray = await QueryExecutor.ExecuteQueryAsync(
                sqltext: queryForResultSetDetails,
                parameters: null!,
                dataReaderHandler: QueryExecutor.GetJsonArrayAsync);
            using JsonDocument sqlResult = JsonDocument.Parse(resultArray!.ToJsonString());

            // Iterate through each row returned by the query which corresponds to
            // one row in the result set.
            foreach (JsonElement element in sqlResult.RootElement.EnumerateArray())
            {
                string resultFieldName = element.GetProperty("result_field_name").ToString();
                Type resultFieldType = SqlToCLRType(element.GetProperty("result_type").ToString());
                bool isResultFieldNullable = element.GetProperty("is_nullable").GetBoolean();

                // Store the dictionary containing result set field with its type as Columns
                storedProcedureDefinition.Columns.TryAdd(resultFieldName, new(resultFieldType) { IsNullable = isResultFieldNullable });
            }
        }

        /// <summary>
        /// Helper method to create params for the query.
        /// </summary>
        /// <param name="paramName">Common prefix of param names.</param>
        /// <param name="paramValues">Values of the param.</param>
        /// <returns></returns>
        private static Dictionary<string, object> GetQueryParams(
            string paramName,
            object[] paramValues)
        {
            Dictionary<string, object> parameters = new();
            for (int paramNumber = 0; paramNumber < paramValues.Length; paramNumber++)
            {
                parameters.Add($"{paramName}{paramNumber}", paramValues[paramNumber]);
            }

            return parameters;
        }

        /// <summary>
        /// Generate the mappings of exposed names to
        /// backing columns, and of backing columns to
        /// exposed names. Used to generate EDM Model using
        /// the exposed names, and to translate between
        /// exposed name and backing column (or the reverse)
        /// when needed while processing the request.
        /// For now, only do this for tables/views as Stored Procedures do not have a SourceDefinition
        /// In the future, mappings for SPs could be used for parameter renaming.
        /// We also handle logging the primary key information here since this is when we first have
        /// the exposed names suitable for logging.
        /// </summary>
        private void GenerateExposedToBackingColumnMapsForEntities()
        {
            foreach (string entityName in _entities.Keys)
            {
                // InCase of StoredProcedures, result set definitions becomes the column definition.
                Dictionary<string, string>? mapping = GetMappingForEntity(entityName);
                EntityBackingColumnsToExposedNames[entityName] = mapping is not null ? mapping : new();
                EntityExposedNamesToBackingColumnNames[entityName] = EntityBackingColumnsToExposedNames[entityName].ToDictionary(x => x.Value, x => x.Key);
                SourceDefinition sourceDefinition = GetSourceDefinition(entityName);
                foreach (string columnName in sourceDefinition.Columns.Keys)
                {
                    if (!EntityExposedNamesToBackingColumnNames[entityName].ContainsKey(columnName) && !EntityBackingColumnsToExposedNames[entityName].ContainsKey(columnName))
                    {
                        EntityBackingColumnsToExposedNames[entityName].Add(columnName, columnName);
                        EntityExposedNamesToBackingColumnNames[entityName].Add(columnName, columnName);
                    }
                }
            }
        }

        /// <summary>
        /// Obtains the underlying mapping that belongs
        /// to a given entity.
        /// </summary>
        /// <param name="entityName">entity whose map we get.</param>
        /// <returns>mapping belonging to eneity.</returns>
        private Dictionary<string, string>? GetMappingForEntity(string entityName)
        {
            _entities.TryGetValue(entityName, out Entity? entity);
            return entity is not null ? entity.Mappings : null;
        }

        /// <summary>
        /// Initialize OData parser by buidling OData model.
        /// The parser will be used for parsing filter clause and order by clause.
        /// </summary>
        private void InitODataParser()
        {
            _oDataParser.BuildModel(this);
        }

        /// <summary>
        /// Fills the table definition with information of all columns and
        /// primary keys.
        /// </summary>
        /// <param name="schemaName">Name of the schema.</param>
        /// <param name="tableName">Name of the table.</param>
        /// <param name="sourceDefinition">Table definition to fill.</param>
        /// <param name="entityName">EntityName included to pass on for error messaging.</param>
        private async Task PopulateSourceDefinitionAsync(
            string entityName,
            string schemaName,
            string tableName,
            SourceDefinition sourceDefinition,
            string[]? runtimeConfigKeyFields)
        {
            DataTable dataTable = await GetTableWithSchemaFromDataSetAsync(entityName, schemaName, tableName);

            List<DataColumn> primaryKeys = new(dataTable.PrimaryKey);
            if (runtimeConfigKeyFields is null || runtimeConfigKeyFields.Length == 0)
            {
                sourceDefinition.PrimaryKey = new(primaryKeys.Select(primaryKey => primaryKey.ColumnName));
            }
            else
            {
                sourceDefinition.PrimaryKey = new(runtimeConfigKeyFields);
            }

            if (sourceDefinition.PrimaryKey.Count == 0)
            {
                throw new DataApiBuilderException(
                       message: $"Primary key not configured on the given database object {tableName}",
                       statusCode: HttpStatusCode.ServiceUnavailable,
                       subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization);
            }

            using DataTableReader reader = new(dataTable);
            DataTable schemaTable = reader.GetSchemaTable();
            RuntimeConfig runtimeConfig = _runtimeConfigProvider.GetRuntimeConfiguration();
            foreach (DataRow columnInfoFromAdapter in schemaTable.Rows)
            {
                string columnName = columnInfoFromAdapter["ColumnName"].ToString()!;

                if (runtimeConfig.GraphQLGlobalSettings.Enabled
                    && _entities.TryGetValue(entityName, out Entity? entity)
                    && IsGraphQLReservedName(entity, columnName, graphQLEnabledGlobally: runtimeConfig.GraphQLGlobalSettings.Enabled))
                {
                    throw new DataApiBuilderException(
                       message: $"The column '{columnName}' violates GraphQL name restrictions.",
                       statusCode: HttpStatusCode.ServiceUnavailable,
                       subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization);
                }

                Type systemTypeOfColumn = (Type)columnInfoFromAdapter["DataType"];
                ColumnDefinition column = new()
                {
                    IsNullable = (bool)columnInfoFromAdapter["AllowDBNull"],
                    IsAutoGenerated = (bool)columnInfoFromAdapter["IsAutoIncrement"],
                    SystemType = systemTypeOfColumn,
                    DbType = DbTypeHelper.GetDbTypeFromSystemType(systemTypeOfColumn)
                };

                // Tests may try to add the same column simultaneously
                // hence we use TryAdd here.
                // If the addition fails, it is assumed the column definition
                // has already been added and need not error out.
                sourceDefinition.Columns.TryAdd(columnName, column);
            }

            DataTable columnsInTable = await GetColumnsAsync(schemaName, tableName);

            PopulateColumnDefinitionWithHasDefault(
                sourceDefinition,
                columnsInTable);
        }

        /// <summary>
        /// Determine whether the provided field of a GraphQL enabled entity meets GraphQL reserved name requirements.
        /// Criteria:
        /// - Is GraphQL enabled globally
        /// - Is GraphQL implicitly enabled e.g. entity.GraphQL is null, or explicitly enabled e.g. entity.GraphQL is true).
        /// - If field has a mapped value (alias), then use the mapped value to evaluate name violation.
        /// - If field does not have an alias/mapped value, then use the provided field name to
        /// check for naming violations.
        /// </summary>
        /// <param name="entity">Entity to check </param>
        /// <param name="databaseColumnName">Name to evaluate against GraphQL naming requirements</param>
        /// <param name="graphQLEnabledGlobally">Whether GraphQL is enabled globally in the runtime configuration.</param>
        /// <exception cref="DataApiBuilderException"/>
        /// <returns>True if no name rules are broken. Otherwise, false</returns>
        public static bool IsGraphQLReservedName(Entity entity, string databaseColumnName, bool graphQLEnabledGlobally)
        {
            if (graphQLEnabledGlobally)
            {
                if (entity.GraphQL is null || (entity.GraphQL is not null && entity.GraphQL is bool enabled && enabled))
                {
                    if (entity.Mappings is not null
                        && entity.Mappings.TryGetValue(databaseColumnName, out string? fieldAlias)
                        && !string.IsNullOrWhiteSpace(fieldAlias))
                    {
                        databaseColumnName = fieldAlias;
                    }

                    return IsIntrospectionField(databaseColumnName);
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the DataTable from the EntitiesDataSet if already present.
        /// If not present, fills it first and returns the same.
        /// </summary>
        private async Task<DataTable> GetTableWithSchemaFromDataSetAsync(
            string entityName,
            string schemaName,
            string tableName)
        {
            DataTable? dataTable = EntitiesDataSet.Tables[tableName];
            if (dataTable is null)
            {
                try
                {
                    dataTable = await FillSchemaForTableAsync(schemaName, tableName);
                }
                catch (Exception ex) when (ex is not DataApiBuilderException)
                {
                    string message;
                    // Check exception content to ensure proper error message for connection string.
                    // If MySql has a non-empty, invalid connection string, it will have the
                    // MYSQL_INVALID_CONNECTION_STRING_MESSAGE in its message when the connection
                    // string is totally invalid and lacks even the basic format of a valid connection
                    // string (ie: ConnectionString="&#@&^@*&^#$"), or will have a targetsite in
                    // the exception with a name of MYSQL_INVALID_CONNECTION_STRING_OPTIONS in the
                    // case where the connection string follows the correct general form, but does
                    // not have keys with valid names (ie: ConnectionString="foo=bar;baz=qux")
                    if (ex.Message.Contains(MySqlMetadataProvider.MYSQL_INVALID_CONNECTION_STRING_MESSAGE) ||
                       (ex.TargetSite is not null &&
                        string.Equals(ex.TargetSite.Name, MySqlMetadataProvider.MYSQL_INVALID_CONNECTION_STRING_OPTIONS)))
                    {
                        message = DataApiBuilderException.CONNECTION_STRING_ERROR_MESSAGE +
                            $"Underlying Exception message: {ex.Message}";
                    }
                    else
                    {
                        message = $"Cannot obtain Schema for entity {entityName} " +
                            $"with underlying database object source: {schemaName}.{tableName} " +
                            $"due to: {ex.Message}";
                    }

                    throw new DataApiBuilderException(
                        message,
                        statusCode: HttpStatusCode.ServiceUnavailable,
                        subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization);
                }
            }

            return dataTable!;
        }

        /// <summary>
        /// Using a data adapter, obtains the schema of the given table name
        /// and adds the corresponding entity in the data set.
        /// </summary>
        private async Task<DataTable> FillSchemaForTableAsync(
            string schemaName,
            string tableName)
        {
            using ConnectionT conn = new();
            // If connection string is set to empty string
            // we throw here to avoid having to sort out
            // complicated db specific exception messages.
            // This is caught and returned as DataApiBuilderException.
            // The runtime config has a public setter so we check
            // here for empty connection string to ensure that
            // it was not set to an invalid state after initialization.
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                throw new DataApiBuilderException(
                    DataApiBuilderException.CONNECTION_STRING_ERROR_MESSAGE +
                    " Connection string is null, empty, or whitespace.",
                    statusCode: HttpStatusCode.ServiceUnavailable,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization);
            }

            try
            {
                // for non-MySql DB types, this will throw an exception
                // for malformed connection strings
                conn.ConnectionString = ConnectionString;
                await QueryExecutor.SetManagedIdentityAccessTokenIfAnyAsync(conn);
            }
            catch (Exception ex)
            {
                string message = DataApiBuilderException.CONNECTION_STRING_ERROR_MESSAGE +
                    $" Underlying Exception message: {ex.Message}";
                throw new DataApiBuilderException(
                    message,
                    statusCode: HttpStatusCode.ServiceUnavailable,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.ErrorInInitialization,
                    innerException: ex);
            }

            await conn.OpenAsync();

            DataAdapterT adapterForTable = new();
            CommandT selectCommand = new()
            {
                Connection = conn
            };

            string tablePrefix = GetTablePrefix(conn.Database, schemaName);
            selectCommand.CommandText
                = ($"SELECT * FROM {tablePrefix}.{SqlQueryBuilder.QuoteIdentifier(tableName)}");
            adapterForTable.SelectCommand = selectCommand;

            DataTable[] dataTable = adapterForTable.FillSchema(EntitiesDataSet, SchemaType.Source, tableName);
            return dataTable[0];
        }

        private string GetTablePrefix(string databaseName, string schemaName)
        {
            StringBuilder tablePrefix = new(SqlQueryBuilder.QuoteIdentifier(databaseName));
            if (!string.IsNullOrEmpty(schemaName))
            {
                schemaName = SqlQueryBuilder.QuoteIdentifier(schemaName);
                tablePrefix.Append($".{schemaName}");
            }

            return tablePrefix.ToString();
        }

        /// <summary>
        /// Gets the metadata information of each column of
        /// the given schema.table
        /// </summary>
        /// <returns>A data table where each row corresponds to a
        /// column of the table.</returns>
        protected virtual async Task<DataTable> GetColumnsAsync(
            string schemaName,
            string tableName)
        {
            using ConnectionT conn = new();
            conn.ConnectionString = ConnectionString;
            await QueryExecutor.SetManagedIdentityAccessTokenIfAnyAsync(conn);
            await conn.OpenAsync();
            // We can specify the Catalog, Schema, Table Name, Column Name to get
            // the specified column(s).
            // Hence, we should create a 4 members array.
            string[] columnRestrictions = new string[NUMBER_OF_RESTRICTIONS];

            // To restrict the columns for the current table, specify the table's name
            // in column restrictions.
            columnRestrictions[0] = conn.Database;
            columnRestrictions[1] = schemaName;
            columnRestrictions[2] = tableName;

            // Each row in the columnsInTable DataTable corresponds to
            // a single column of the table.
            DataTable columnsInTable = await conn.GetSchemaAsync("Columns", columnRestrictions);

            return columnsInTable;
        }

        /// <summary>
        /// Populates the column definition with HasDefault property.
        /// </summary>
        private static void PopulateColumnDefinitionWithHasDefault(
            SourceDefinition sourceDefinition,
            DataTable allColumnsInTable)
        {
            foreach (DataRow columnInfo in allColumnsInTable.Rows)
            {
                string columnName = (string)columnInfo["COLUMN_NAME"];
                bool hasDefault =
                    Type.GetTypeCode(columnInfo["COLUMN_DEFAULT"].GetType()) != TypeCode.DBNull;
                ColumnDefinition? columnDefinition;
                if (sourceDefinition.Columns.TryGetValue(columnName, out columnDefinition))
                {
                    columnDefinition.HasDefault = hasDefault;

                    if (hasDefault)
                    {
                        columnDefinition.DefaultValue = columnInfo["COLUMN_DEFAULT"];
                    }
                }
            }
        }

        /// <summary>
        /// Fills the table definition with information of the foreign keys
        /// for all the tables.
        /// </summary>
        private async Task PopulateForeignKeyDefinitionAsync()
        {
            // For each database object, that has a relationship metadata,
            // build the array storing all the schemaNames(for now the defaultSchemaName)
            // and the array for all tableNames
            List<string> schemaNames = new();
            List<string> tableNames = new();
            IEnumerable<SourceDefinition> dbEntitiesToBePopulatedWithFK =
                FindAllEntitiesWhoseForeignKeyIsToBeRetrieved(schemaNames, tableNames);

            // No need to do any further work if there are no FK to be retrieved
            if (dbEntitiesToBePopulatedWithFK.Count() == 0)
            {
                return;
            }

            // Build the query required to get the foreign key information.
            string queryForForeignKeyInfo =
                ((BaseSqlQueryBuilder)SqlQueryBuilder).BuildForeignKeyInfoQuery(tableNames.Count());

            // Build the parameters dictionary for the foreign key info query
            // consisting of all schema names and table names.
            Dictionary<string, DbConnectionParam> parameters =
                GetForeignKeyQueryParams(
                    schemaNames.ToArray(),
                    tableNames.ToArray());

            // Gather all the referencing and referenced columns for each pair
            // of referencing and referenced tables.
            PairToFkDefinition = await QueryExecutor.ExecuteQueryAsync(queryForForeignKeyInfo, parameters, SummarizeFkMetadata);

            if (PairToFkDefinition is not null)
            {
                FillInferredFkInfo(dbEntitiesToBePopulatedWithFK);
            }

            ValidateAllFkHaveBeenInferred(dbEntitiesToBePopulatedWithFK);
        }

        /// <summary>
        /// Helper method to find all the entities whose foreign key information is to be retrieved.
        /// </summary>
        /// <param name="schemaNames">List of names of the schemas to which entities belong.</param>
        /// <param name="tableNames">List of names of the entities(tables)</param>
        /// <returns>A collection of entity names</returns>
        private IEnumerable<SourceDefinition> FindAllEntitiesWhoseForeignKeyIsToBeRetrieved(
            List<string> schemaNames,
            List<string> tableNames)
        {
            Dictionary<string, SourceDefinition> sourceNameToSourceDefinition = new();
            foreach ((string entityName, DatabaseObject dbObject) in EntityToDatabaseObject)
            {
                // Ensure we're only doing this on tables, not stored procedures which have no table definition,
                // not views whose underlying base table's foreign key constraints are taken care of
                // by database itself.
                if (dbObject.SourceType is SourceType.Table)
                {
                    if (!sourceNameToSourceDefinition.ContainsKey(dbObject.Name))
                    {
                        SourceDefinition sourceDefinition = GetSourceDefinition(entityName);
                        foreach ((_, RelationshipMetadata relationshipData)
                            in sourceDefinition.SourceEntityRelationshipMap)
                        {
                            IEnumerable<List<ForeignKeyDefinition>> foreignKeysForAllTargetEntities
                                = relationshipData.TargetEntityToFkDefinitionMap.Values;
                            foreach (List<ForeignKeyDefinition> fkDefinitionsForTargetEntity
                                in foreignKeysForAllTargetEntities)
                            {
                                foreach (ForeignKeyDefinition fk in fkDefinitionsForTargetEntity)
                                {
                                    schemaNames.Add(fk.Pair.ReferencingDbTable.SchemaName);
                                    tableNames.Add(fk.Pair.ReferencingDbTable.Name);
                                    sourceNameToSourceDefinition.TryAdd(dbObject.Name, sourceDefinition);
                                }
                            }
                        }
                    }
                }
            }

            return sourceNameToSourceDefinition.Values;
        }

        /// <summary>
        /// Method to validate that the foreign key information is populdated
        /// for all the expected entities
        /// </summary>
        /// <param name="dbEntitiesToBePopulatedWithFK">List of database entities
        /// whose definition has to be populated with foreign key information.</param>
        /// <exception cref="NotSupportedException"></exception>
        private static void ValidateAllFkHaveBeenInferred(
            IEnumerable<SourceDefinition> dbEntitiesToBePopulatedWithFK)
        {
            foreach (SourceDefinition sourceDefinition in dbEntitiesToBePopulatedWithFK)
            {
                foreach ((string sourceEntityName, RelationshipMetadata relationshipData)
                        in sourceDefinition.SourceEntityRelationshipMap)
                {
                    IEnumerable<List<ForeignKeyDefinition>> foreignKeys = relationshipData.TargetEntityToFkDefinitionMap.Values;
                    // If none of the inferred foreign keys have the referencing columns,
                    // it means metadata is still missing fail the bootstrap.
                    if (!foreignKeys.Any(fkList => fkList.Any(fk => fk.ReferencingColumns.Count() != 0)))
                    {
                        throw new NotSupportedException($"Some of the relationship information missing and could not be inferred for {sourceEntityName}.");
                    }
                }
            }
        }

        /// <summary>
        /// Each row in the results of the given data reader represents one column from one foreign key
        /// between an ordered pair of referencing and referenced database objects.
        /// This data reader handler summarizes this foreign key metadata so that
        /// for each referencing and referenced table pair, there is exactly one foreign key definition
        /// containing the list of all referencing columns and referenced columns.
        /// </summary>
        /// <param name="reader">The DbDataReader.</param>
        /// <param name="args">Arguments to this function.</param>
        /// <returns>A dictionary mapping ordered relationship pairs to
        /// foreign key definition between them.</returns>
        private async Task<Dictionary<RelationShipPair, ForeignKeyDefinition>?>
            SummarizeFkMetadata(DbDataReader reader, List<string>? args = null)
        {
            // Extract all the rows in the current Result Set of DbDataReader.
            DbResultSet foreignKeysInfoWithProperties =
                await QueryExecutor.ExtractResultSetFromDbDataReader(reader);

            Dictionary<RelationShipPair, ForeignKeyDefinition> pairToFkDefinition = new();

            foreach (DbResultSetRow foreignKeyInfoWithProperties in foreignKeysInfoWithProperties.Rows)
            {
                Dictionary<string, object?> foreignKeyInfo = foreignKeyInfoWithProperties.Columns;
                string referencingSchemaName =
                    (string)foreignKeyInfo[$"Referencing{nameof(DatabaseObject.SchemaName)}"]!;
                string referencingTableName = (string)foreignKeyInfo[$"Referencing{nameof(SourceDefinition)}"]!;
                string referencedSchemaName =
                    (string)foreignKeyInfo[$"Referenced{nameof(DatabaseObject.SchemaName)}"]!;
                string referencedTableName = (string)foreignKeyInfo[$"Referenced{nameof(SourceDefinition)}"]!;

                DatabaseTable referencingDbObject = GenerateDbTable(referencingSchemaName, referencingTableName);
                DatabaseTable referencedDbObject = GenerateDbTable(referencedSchemaName, referencedTableName);
                RelationShipPair pair = new(referencingDbObject, referencedDbObject);
                if (!pairToFkDefinition.TryGetValue(pair, out ForeignKeyDefinition? foreignKeyDefinition))
                {
                    foreignKeyDefinition = new()
                    {
                        Pair = pair
                    };
                    pairToFkDefinition.Add(pair, foreignKeyDefinition);
                }

                // Add the referenced and referencing columns to the foreign key definition.
                foreignKeyDefinition.ReferencedColumns.Add(
                    (string)foreignKeyInfo[nameof(ForeignKeyDefinition.ReferencedColumns)]!);
                foreignKeyDefinition.ReferencingColumns.Add(
                    (string)foreignKeyInfo[nameof(ForeignKeyDefinition.ReferencingColumns)]!);
            }

            return pairToFkDefinition;
        }

        /// <summary>
        /// Fills the table definition with the inferred foreign key metadata
        /// about the referencing and referenced columns.
        /// </summary>
        /// <param name="dbEntitiesToBePopulatedWithFK">List of database entities
        /// whose definition has to be populated with foreign key information.</param>
        private void FillInferredFkInfo(
            IEnumerable<SourceDefinition> dbEntitiesToBePopulatedWithFK)
        {
            // For each table definition that has to be populated with the inferred
            // foreign key information.
            foreach (SourceDefinition sourceDefinition in dbEntitiesToBePopulatedWithFK)
            {
                // For each source entities, which maps to this table definition
                // and has a relationship metadata to be filled.
                foreach ((_, RelationshipMetadata relationshipData)
                       in sourceDefinition.SourceEntityRelationshipMap)
                {
                    // Enumerate all the foreign keys required for all the target entities
                    // that this source is related to.
                    IEnumerable<List<ForeignKeyDefinition>> foreignKeysForAllTargetEntities =
                        relationshipData.TargetEntityToFkDefinitionMap.Values;
                    // For each target, loop through each foreign key
                    foreach (List<ForeignKeyDefinition> foreignKeysForTarget in foreignKeysForAllTargetEntities)
                    {
                        // For each foreign key between this pair of source and target entities
                        // which needs the referencing columns,
                        // find the fk inferred for this pair the backend and
                        // equate the referencing columns and referenced columns.
                        foreach (ForeignKeyDefinition fk in foreignKeysForTarget)
                        {
                            // if the referencing and referenced columns count > 0,
                            // we have already gathered this information from the runtime config.
                            if (fk.ReferencingColumns.Count > 0 && fk.ReferencedColumns.Count > 0)
                            {
                                continue;
                            }

                            // Add the referencing and referenced columns for this foreign key definition
                            // for the target.
                            if (PairToFkDefinition is not null && PairToFkDefinition.TryGetValue(
                                    fk.Pair, out ForeignKeyDefinition? inferredDefinition))
                            {
                                // Only add the referencing columns if they have not been
                                // specified in the configuration file.
                                if (fk.ReferencingColumns.Count == 0)
                                {
                                    fk.ReferencingColumns.AddRange(inferredDefinition.ReferencingColumns);
                                }

                                // Only add the referenced columns if they have not been
                                // specified in the configuration file.
                                if (fk.ReferencedColumns.Count == 0)
                                {
                                    fk.ReferencedColumns.AddRange(inferredDefinition.ReferencedColumns);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// For the given two database objects, returns true if a foreignKey exists between them.
        /// Else returns false.
        /// </summary>
        public bool VerifyForeignKeyExistsInDB(
            DatabaseTable databaseTableA,
            DatabaseTable databaseTableB)
        {
            if (PairToFkDefinition is null)
            {
                return false;
            }

            RelationShipPair pairAB = new(databaseTableA, databaseTableB);
            RelationShipPair pairBA = new(databaseTableB, databaseTableA);

            return (PairToFkDefinition.ContainsKey(pairAB) || PairToFkDefinition.ContainsKey(pairBA));
        }

        /// <summary>
        /// Retrieving the partition key path, for cosmosdb_nosql only
        /// </summary>
        public string? GetPartitionKeyPath(string database, string container)
            => throw new NotImplementedException();

        /// <summary>
        /// Setting the partition key path, for cosmosdb_nosql only
        /// </summary>
        public void SetPartitionKeyPath(string database, string container, string partitionKeyPath)
            => throw new NotImplementedException();

        public bool IsDevelopmentMode()
        {
            return _runtimeConfigProvider.IsDeveloperMode();
        }
    }
}

