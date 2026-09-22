using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Dataverse Catalog Tree: dual-purpose connector for recursive 1:N catalog hierarchies.
/// Exposes an MCP endpoint for Copilot Studio plus typed operations for Power Automate,
/// both backed by one traversal engine that assembles a whole subtree server-side.
/// </summary>
public class Script : ScriptBase
{
    private const bool APP_INSIGHTS_ENABLED = false;
    private const string APP_INSIGHTS_KEY = "[INSERT_YOUR_APP_INSIGHTS_INSTRUMENTATION_KEY]";
    private const string APP_INSIGHTS_ENDPOINT = "https://dc.applicationinsights.azure.com/v2/track";

    private const string ApiPath = "/api/data/v9.2/";
    private const string ProtocolVersion = "2024-11-05";

    private const int DefaultDepth = 3;
    private const int MaxDepth = 10;
    private const int DefaultMaxNodes = 500;
    private const int MaxNodeBudget = 5000;
    private const int IdChunkSize = 25;
    private const int PageSize = 500;
    private const int MaxDataverseRequests = 80;
    private const int MaxValidValues = 40;
    private const int MaxExpandChain = 12;

    private static readonly HashSet<string> KnownOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "InvokeMCP", "DescribeCatalog", "GetTree", "GetChildren", "GetAncestors",
        "SearchNodes", "CreateNode", "UpdateNode", "MoveNode", "DeleteNode",
        "GetTables", "GetChildRelationships"
    };

    private readonly Dictionary<string, TableInfo> _tableCache =
        new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, TableSummary> _allTables;
    private int _dataverseRequests;
    private DateTime _startedUtc = DateTime.UtcNow;

    // ---------------------------------------------------------------- models

    private class TableSummary
    {
        public string LogicalName;
        public string EntitySetName;
        public string DisplayName;
        public string PrimaryId;
        public string PrimaryName;
        public bool IsCustom;
    }

    private class RelationshipInfo
    {
        public string SchemaName;
        public string ParentTable;
        public string ChildTable;
        public string LookupAttribute;
        public string ChildNavProperty;
        public string ParentNavProperty;
        public bool IsHierarchical;
        public bool IsSelfReference;

        public string LookupValueField
        {
            get { return "_" + LookupAttribute + "_value"; }
        }
    }

    private class TableInfo
    {
        public string LogicalName;
        public string EntitySetName;
        public string PrimaryId;
        public string PrimaryName;
        public string DisplayName;
        public bool IsCustom;
        public List<RelationshipInfo> Children = new List<RelationshipInfo>();
    }

    private class CatalogLevel
    {
        public TableInfo Table;
        public RelationshipInfo Relationship;
    }

    private class CatalogDefinition
    {
        public TableInfo Root;
        public List<CatalogLevel> Levels = new List<CatalogLevel>();
        public bool SelfReference;
        public bool Repeating;
        public bool Hierarchical;
        public string LevelsPath;
        public List<string> Notes = new List<string>();
        public List<RelationshipInfo> Candidates = new List<RelationshipInfo>();
    }

    private class Node
    {
        public string Id;
        public string Table;
        public string EntitySet;
        public string Label;
        public string ParentId;
        public string Path;
        public int Depth;
        public JObject Fields;
        public List<Node> Children = new List<Node>();
        public int? ChildCount;
        public bool HasMoreChildren;
    }

    private class TreeOptions
    {
        public string RootId;
        public int Depth = DefaultDepth;
        public int MaxNodes = DefaultMaxNodes;
        public List<string> Fields = new List<string>();
        public string Filter;
        public string Format = "tree";
        public string Strategy = "auto";
        public bool IncludeChildCounts = true;
        public int RootTop = 50;
    }

    private class TreeResult
    {
        public List<Node> Roots = new List<Node>();
        public List<Node> All = new List<Node>();
        public int Cycles;
        public bool Truncated;
        public string TruncationReason;
        public int MaxDepthReached;
        public string Strategy;
    }

    /// <summary>Error that names the values that would have worked, so a caller can self-correct.</summary>
    private class CatalogException : Exception
    {
        public string Code;
        public string Table;
        public string ProvidedValue;
        public JArray ValidValues;
        public string Hint;

        public CatalogException(string code, string message) : base(message)
        {
            Code = code;
            ValidValues = new JArray();
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["error"] = Code,
                ["message"] = Message,
                ["table"] = Table ?? string.Empty,
                ["providedValue"] = ProvidedValue ?? string.Empty,
                ["validValues"] = ValidValues ?? new JArray(),
                ["hint"] = Hint ?? string.Empty
            };
        }
    }

    // --------------------------------------------------------------- routing

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        _startedUtc = DateTime.UtcNow;
        var operation = ResolveOperation();

        try
        {
            switch (operation)
            {
                case "InvokeMCP":
                    return await HandleMcpAsync().ConfigureAwait(false);
                case "DescribeCatalog":
                    return await HandleDescribeAsync().ConfigureAwait(false);
                case "GetTree":
                    return await HandleGetTreeAsync().ConfigureAwait(false);
                case "GetChildren":
                    return await HandleGetChildrenAsync().ConfigureAwait(false);
                case "GetAncestors":
                    return await HandleGetAncestorsAsync().ConfigureAwait(false);
                case "SearchNodes":
                    return await HandleSearchAsync().ConfigureAwait(false);
                case "CreateNode":
                    return await HandleCreateNodeAsync().ConfigureAwait(false);
                case "UpdateNode":
                    return await HandleUpdateNodeAsync().ConfigureAwait(false);
                case "MoveNode":
                    return await HandleMoveNodeAsync().ConfigureAwait(false);
                case "DeleteNode":
                    return await HandleDeleteNodeAsync().ConfigureAwait(false);
                case "GetTables":
                    return await HandleGetTablesAsync().ConfigureAwait(false);
                case "GetChildRelationships":
                    return await HandleGetChildRelationshipsAsync().ConfigureAwait(false);
                default:
                    var unknown = new CatalogException("unknown_operation",
                        "Operation '" + (operation ?? "(none)") + "' is not implemented by this connector.");
                    unknown.ValidValues = new JArray(KnownOperations.Select(o => new JObject { ["value"] = o }));
                    return JsonResponse(HttpStatusCode.BadRequest, unknown.ToJson());
            }
        }
        catch (CatalogException cx)
        {
            await LogToAppInsightsAsync("CatalogError", new Dictionary<string, string>
            {
                ["operation"] = operation ?? string.Empty,
                ["code"] = cx.Code,
                ["message"] = cx.Message
            }).ConfigureAwait(false);

            return JsonResponse(HttpStatusCode.BadRequest, cx.ToJson());
        }
        catch (Exception ex)
        {
            await LogToAppInsightsAsync("UnhandledError", new Dictionary<string, string>
            {
                ["operation"] = operation ?? string.Empty,
                ["message"] = ex.Message,
                ["stack"] = ex.StackTrace ?? string.Empty
            }).ConfigureAwait(false);

            return JsonResponse(HttpStatusCode.InternalServerError, new JObject
            {
                ["error"] = "unhandled_exception",
                ["message"] = ex.Message
            });
        }
    }

    /// <summary>OperationId arrives base64 encoded in some regions; fall back to the request path.</summary>
    private string ResolveOperation()
    {
        var operationId = this.Context.OperationId;

        if (!string.IsNullOrEmpty(operationId))
        {
            if (KnownOperations.Contains(operationId))
            {
                return operationId;
            }

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(operationId));
                if (KnownOperations.Contains(decoded))
                {
                    return decoded;
                }
            }
            catch
            {
                // Not base64; fall through to path routing.
            }
        }

        var path = (this.Context.Request.RequestUri.AbsolutePath ?? string.Empty).TrimEnd('/');

        if (path.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase) || path.Length == 0) return "InvokeMCP";
        if (path.EndsWith("/catalog/describe", StringComparison.OrdinalIgnoreCase)) return "DescribeCatalog";
        if (path.EndsWith("/catalog/tree", StringComparison.OrdinalIgnoreCase)) return "GetTree";
        if (path.EndsWith("/catalog/children", StringComparison.OrdinalIgnoreCase)) return "GetChildren";
        if (path.EndsWith("/catalog/ancestors", StringComparison.OrdinalIgnoreCase)) return "GetAncestors";
        if (path.EndsWith("/catalog/search", StringComparison.OrdinalIgnoreCase)) return "SearchNodes";
        if (path.EndsWith("/catalog/node/move", StringComparison.OrdinalIgnoreCase)) return "MoveNode";
        if (path.EndsWith("/catalog/node/update", StringComparison.OrdinalIgnoreCase)) return "UpdateNode";
        if (path.EndsWith("/catalog/node/delete", StringComparison.OrdinalIgnoreCase)) return "DeleteNode";
        if (path.EndsWith("/catalog/node", StringComparison.OrdinalIgnoreCase)) return "CreateNode";
        if (path.EndsWith("/metadata/tables", StringComparison.OrdinalIgnoreCase)) return "GetTables";
        if (path.EndsWith("/metadata/childrelationships", StringComparison.OrdinalIgnoreCase)) return "GetChildRelationships";

        return operationId;
    }

    private Dictionary<string, string> ParseQuery()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = this.Context.Request.RequestUri.Query;

        if (string.IsNullOrEmpty(query))
        {
            return values;
        }

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair.Substring(0, separator);
            var value = separator < 0 ? string.Empty : pair.Substring(separator + 1);

            values[Uri.UnescapeDataString(key.Replace("+", " "))] = Uri.UnescapeDataString(value.Replace("+", " "));
        }

        return values;
    }

    private async Task<JObject> ReadBodyAsync()
    {
        if (this.Context.Request.Content == null)
        {
            return new JObject();
        }

        var raw = await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new JObject();
        }

        try
        {
            return JObject.Parse(raw);
        }
        catch (JsonException)
        {
            throw new CatalogException("invalid_json", "The request body is not valid JSON.");
        }
    }

    private HttpResponseMessage JsonResponse(HttpStatusCode statusCode, JToken payload)
    {
        var response = new HttpResponseMessage(statusCode);
        response.Content = CreateJsonContent(payload.ToString(Newtonsoft.Json.Formatting.None));
        return response;
    }

    // ----------------------------------------------------- Dataverse access

    private async Task<JObject> SendDataverseAsync(HttpMethod method, string relativeUrl, JObject body, string prefer)
    {
        if (_dataverseRequests >= MaxDataverseRequests)
        {
            throw new CatalogException("request_budget_exceeded",
                "This traversal needed more than " + MaxDataverseRequests + " Dataverse calls. Reduce depth or maxNodes, or start from a lower node.");
        }

        _dataverseRequests++;

        var baseUrl = this.Context.Request.RequestUri.GetLeftPart(UriPartial.Authority);
        var url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativeUrl
            : baseUrl + ApiPath + relativeUrl.TrimStart('/');

        var request = new HttpRequestMessage(method, url);

        if (this.Context.Request.Headers.Authorization != null)
        {
            request.Headers.Authorization = this.Context.Request.Headers.Authorization;
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
        request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        request.Headers.TryAddWithoutValidation("If-None-Match", "null");

        if (!string.IsNullOrEmpty(prefer))
        {
            request.Headers.TryAddWithoutValidation("Prefer", prefer);
        }

        if (body != null)
        {
            request.Content = CreateJsonContent(body.ToString(Newtonsoft.Json.Formatting.None));
        }

        var response = await this.Context.SendAsync(request, this.CancellationToken).ConfigureAwait(false);
        var raw = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildDataverseException(response.StatusCode, raw, url);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            var created = new JObject();
            IEnumerable<string> entityId;

            if (response.Headers.TryGetValues("OData-EntityId", out entityId))
            {
                created["OData-EntityId"] = entityId.FirstOrDefault() ?? string.Empty;
            }

            return created;
        }

        return JObject.Parse(raw);
    }

    private CatalogException BuildDataverseException(HttpStatusCode statusCode, string raw, string url)
    {
        var message = raw;
        var code = "dataverse_error";

        try
        {
            var parsed = JObject.Parse(raw);
            var error = parsed["error"] as JObject;

            if (error != null)
            {
                message = error["message"] != null ? error["message"].ToString() : raw;
            }
        }
        catch
        {
            // Leave the raw payload as the message.
        }

        if (statusCode == HttpStatusCode.NotFound)
        {
            code = "record_not_found";
        }
        else if (message.IndexOf("Could not find a property named", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            code = "unknown_column";
        }

        var exception = new CatalogException(code, message);
        exception.Hint = code == "unknown_column"
            ? "Call describe_catalog or list the table columns before requesting extra fields."
            : "Verify the table, ids and filter passed to this operation.";

        return exception;
    }

    private async Task<JObject> GetJsonAsync(string relativeUrl)
    {
        return await SendDataverseAsync(HttpMethod.Get, relativeUrl, null, null).ConfigureAwait(false);
    }

    private static string EscapeOData(string value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }

    private static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        foreach (var character in trimmed)
        {
            var isValid = (character >= 'a' && character <= 'z')
                || (character >= 'A' && character <= 'Z')
                || (character >= '0' && character <= '9')
                || character == '_';

            if (!isValid)
            {
                return null;
            }
        }

        return trimmed.ToLowerInvariant();
    }

    private static string RequireGuid(string value, string parameterName)
    {
        Guid parsed;

        if (!Guid.TryParse((value ?? string.Empty).Trim().Trim('{', '}'), out parsed))
        {
            var exception = new CatalogException("invalid_id",
                "The value supplied for " + parameterName + " is not a GUID.");
            exception.ProvidedValue = value;
            exception.Hint = "Use an id returned by get_catalog_tree, search_catalog or get_node_children. Never construct an id.";
            throw exception;
        }

        return parsed.ToString();
    }

    private static string LabelOf(JToken label)
    {
        if (label == null || label.Type != JTokenType.Object)
        {
            return null;
        }

        var userLocalized = label["UserLocalizedLabel"];

        if (userLocalized != null && userLocalized.Type == JTokenType.Object && userLocalized["Label"] != null)
        {
            return userLocalized["Label"].ToString();
        }

        var localized = label["LocalizedLabels"] as JArray;

        if (localized != null && localized.Count > 0
            && localized[0].Type == JTokenType.Object && localized[0]["Label"] != null)
        {
            return localized[0]["Label"].ToString();
        }

        return null;
    }

    // ------------------------------------------------------------- metadata

    private async Task<TableInfo> GetTableAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var missing = new CatalogException("table_required", "A table name is required.");
            missing.Hint = "Pass the table logical name, for example tst_category.";
            throw missing;
        }

        TableInfo cached;

        if (_tableCache.TryGetValue(name, out cached))
        {
            return cached;
        }

        var identifier = SanitizeIdentifier(name);

        if (identifier == null)
        {
            var invalid = new CatalogException("invalid_table", "'" + name + "' is not a valid table name.");
            invalid.ProvidedValue = name;
            invalid.Hint = "Table names contain only letters, digits and underscores.";
            throw invalid;
        }

        var filter = "LogicalName eq '" + EscapeOData(identifier) + "' or EntitySetName eq '" + EscapeOData(identifier) + "'";
        var url = "EntityDefinitions"
            + "?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,DisplayName,IsCustomEntity"
            + "&$filter=" + Uri.EscapeDataString(filter)
            + "&$expand=OneToManyRelationships($select=SchemaName,ReferencingEntity,ReferencingAttribute,"
            + "ReferencedEntityNavigationPropertyName,ReferencingEntityNavigationPropertyName,IsHierarchical)";

        var response = await GetJsonAsync(url).ConfigureAwait(false);
        var records = response["value"] as JArray;

        if (records == null || records.Count == 0)
        {
            var notFound = new CatalogException("unknown_table", "No table named '" + name + "' exists in this environment.");
            notFound.ProvidedValue = name;
            notFound.Hint = "Call GetTables with a search term to list real table names.";
            throw notFound;
        }

        var definition = (JObject)records[0];
        var table = new TableInfo
        {
            LogicalName = definition.Value<string>("LogicalName"),
            EntitySetName = definition.Value<string>("EntitySetName"),
            PrimaryId = definition.Value<string>("PrimaryIdAttribute"),
            PrimaryName = definition.Value<string>("PrimaryNameAttribute"),
            IsCustom = definition.Value<bool?>("IsCustomEntity") ?? false
        };

        table.DisplayName = LabelOf(definition["DisplayName"]) ?? table.LogicalName;

        var relationships = definition["OneToManyRelationships"] as JArray;

        if (relationships != null)
        {
            foreach (JObject relationship in relationships.OfType<JObject>())
            {
                var childTable = relationship.Value<string>("ReferencingEntity");
                var lookupAttribute = relationship.Value<string>("ReferencingAttribute");

                if (string.IsNullOrEmpty(childTable) || string.IsNullOrEmpty(lookupAttribute))
                {
                    continue;
                }

                table.Children.Add(new RelationshipInfo
                {
                    SchemaName = relationship.Value<string>("SchemaName"),
                    ParentTable = table.LogicalName,
                    ChildTable = childTable,
                    LookupAttribute = lookupAttribute,
                    ParentNavProperty = relationship.Value<string>("ReferencedEntityNavigationPropertyName"),
                    ChildNavProperty = relationship.Value<string>("ReferencingEntityNavigationPropertyName"),
                    IsHierarchical = relationship.Value<bool?>("IsHierarchical") ?? false,
                    IsSelfReference = string.Equals(childTable, table.LogicalName, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        _tableCache[table.LogicalName] = table;
        _tableCache[table.EntitySetName] = table;
        _tableCache[name] = table;

        return table;
    }

    private async Task<Dictionary<string, TableSummary>> GetAllTablesAsync()
    {
        if (_allTables != null)
        {
            return _allTables;
        }

        var url = "EntityDefinitions"
            + "?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,DisplayName,IsCustomEntity";

        var response = await GetJsonAsync(url).ConfigureAwait(false);
        var records = response["value"] as JArray;

        _allTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase);

        if (records == null)
        {
            return _allTables;
        }

        foreach (JObject record in records.OfType<JObject>())
        {
            var logicalName = record.Value<string>("LogicalName");

            if (string.IsNullOrEmpty(logicalName))
            {
                continue;
            }

            _allTables[logicalName] = new TableSummary
            {
                LogicalName = logicalName,
                EntitySetName = record.Value<string>("EntitySetName"),
                DisplayName = LabelOf(record["DisplayName"]) ?? logicalName,
                PrimaryId = record.Value<string>("PrimaryIdAttribute"),
                PrimaryName = record.Value<string>("PrimaryNameAttribute"),
                IsCustom = record.Value<bool?>("IsCustomEntity") ?? false
            };
        }

        return _allTables;
    }

    /// <summary>Relationships worth offering as the next level, custom tables and self-references first.</summary>
    private async Task<List<RelationshipInfo>> GetCandidateRelationshipsAsync(TableInfo table, bool customOnly)
    {
        var all = await GetAllTablesAsync().ConfigureAwait(false);
        var candidates = new List<RelationshipInfo>();

        foreach (var relationship in table.Children)
        {
            TableSummary child;
            var known = all.TryGetValue(relationship.ChildTable, out child);

            if (customOnly && !relationship.IsSelfReference && (!known || !child.IsCustom))
            {
                continue;
            }

            candidates.Add(relationship);
        }

        return candidates
            .OrderByDescending(r => r.IsHierarchical)
            .ThenByDescending(r => r.IsSelfReference)
            .ThenBy(r => r.ChildTable, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<JObject> RelationshipToJsonAsync(RelationshipInfo relationship)
    {
        var all = await GetAllTablesAsync().ConfigureAwait(false);
        TableSummary child;
        all.TryGetValue(relationship.ChildTable, out child);

        var childLabel = child != null ? child.DisplayName : relationship.ChildTable;

        return new JObject
        {
            ["schemaName"] = relationship.SchemaName,
            ["displayName"] = childLabel + " (" + relationship.SchemaName + ")",
            ["childTable"] = relationship.ChildTable,
            ["childEntitySet"] = child != null ? child.EntitySetName : null,
            ["lookupAttribute"] = relationship.LookupAttribute,
            ["navigationProperty"] = relationship.ParentNavProperty,
            ["isSelfReference"] = relationship.IsSelfReference,
            ["isHierarchical"] = relationship.IsHierarchical
        };
    }

    // --------------------------------------------------- definition resolve

    private async Task<CatalogDefinition> ResolveDefinitionAsync(string rootTable, string levelsSpec)
    {
        var root = await GetTableAsync(rootTable).ConfigureAwait(false);
        var definition = new CatalogDefinition { Root = root };

        if (string.IsNullOrWhiteSpace(levelsSpec))
        {
            await ResolveImplicitDefinitionAsync(definition).ConfigureAwait(false);
        }
        else
        {
            await ResolveExplicitDefinitionAsync(definition, levelsSpec).ConfigureAwait(false);
        }

        definition.LevelsPath = string.Join(">", definition.Levels.Select(l => l.Relationship.SchemaName));

        return definition;
    }

    private async Task ResolveImplicitDefinitionAsync(CatalogDefinition definition)
    {
        var root = definition.Root;
        var selfReferences = root.Children.Where(r => r.IsSelfReference).ToList();
        var chosen = selfReferences.FirstOrDefault(r => r.IsHierarchical);

        if (chosen == null && selfReferences.Count == 1)
        {
            chosen = selfReferences[0];
        }

        if (chosen == null && selfReferences.Count > 1)
        {
            var ambiguous = new CatalogException("ambiguous_self_reference",
                "'" + root.LogicalName + "' has " + selfReferences.Count
                + " self-referencing relationships and none is marked hierarchical. Pass levels to choose one.");
            ambiguous.Table = root.LogicalName;
            ambiguous.ValidValues = new JArray(selfReferences.Take(MaxValidValues).Select(r => new JObject
            {
                ["value"] = r.SchemaName,
                ["description"] = "Parent lookup " + r.LookupAttribute
            }));
            ambiguous.Hint = "Pass levels=<schemaName> using one of these values.";
            throw ambiguous;
        }

        if (chosen == null)
        {
            var candidates = await GetCandidateRelationshipsAsync(root, true).ConfigureAwait(false);
            var needsLevels = new CatalogException("levels_required",
                "'" + root.LogicalName + "' has no self-referencing parent lookup, so the level path cannot be inferred. Pass levels as an ordered relationship path.");
            needsLevels.Table = root.LogicalName;

            var values = new JArray();

            foreach (var candidate in candidates.Take(MaxValidValues))
            {
                values.Add(new JObject
                {
                    ["value"] = candidate.SchemaName,
                    ["description"] = "Children in " + candidate.ChildTable + " via " + candidate.LookupAttribute
                });
            }

            needsLevels.ValidValues = values;
            needsLevels.Hint = "Example: levels=" + (candidates.Count > 0 ? candidates[0].SchemaName : "<relationshipSchemaName>")
                + ">NextRelationshipSchemaName. Call describe_catalog for the full list.";
            throw needsLevels;
        }

        definition.SelfReference = true;
        definition.Repeating = true;
        definition.Hierarchical = chosen.IsHierarchical;
        definition.Levels.Add(new CatalogLevel { Table = root, Relationship = chosen });

        if (!chosen.IsHierarchical)
        {
            definition.Notes.Add("Relationship '" + chosen.SchemaName
                + "' is not marked hierarchical. Marking it hierarchical in Dataverse lets the whole subtree be read in a single query.");
        }
    }

    private async Task ResolveExplicitDefinitionAsync(CatalogDefinition definition, string levelsSpec)
    {
        var tokens = levelsSpec.Split(new[] { '>', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var current = definition.Root;

        foreach (var token in tokens)
        {
            var matches = current.Children.Where(r =>
                string.Equals(r.SchemaName, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.ParentNavProperty, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.ChildTable, token, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count > 1)
            {
                var exact = matches
                    .Where(r => string.Equals(r.SchemaName, token, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (exact.Count == 1)
                {
                    matches = exact;
                }
            }

            if (matches.Count == 0)
            {
                var candidates = await GetCandidateRelationshipsAsync(current, true).ConfigureAwait(false);
                var unknown = new CatalogException("unknown_relationship",
                    "'" + token + "' is not a relationship from '" + current.LogicalName + "'.");
                unknown.Table = current.LogicalName;
                unknown.ProvidedValue = token;
                unknown.ValidValues = new JArray(candidates.Take(MaxValidValues).Select(r => new JObject
                {
                    ["value"] = r.SchemaName,
                    ["description"] = "Children in " + r.ChildTable + " via " + r.LookupAttribute
                }));
                unknown.Hint = "Retry with one of these relationship schema names.";
                throw unknown;
            }

            if (matches.Count > 1)
            {
                var ambiguous = new CatalogException("ambiguous_relationship",
                    "'" + token + "' matches " + matches.Count + " relationships from '" + current.LogicalName + "'.");
                ambiguous.Table = current.LogicalName;
                ambiguous.ProvidedValue = token;
                ambiguous.ValidValues = new JArray(matches.Take(MaxValidValues).Select(r => new JObject
                {
                    ["value"] = r.SchemaName,
                    ["description"] = "Children in " + r.ChildTable + " via " + r.LookupAttribute
                }));
                ambiguous.Hint = "Retry using the exact relationship schema name.";
                throw ambiguous;
            }

            var relationship = matches[0];
            var childTable = await GetTableAsync(relationship.ChildTable).ConfigureAwait(false);

            definition.Levels.Add(new CatalogLevel { Table = childTable, Relationship = relationship });
            current = childTable;
        }

        if (definition.Levels.Count == 0)
        {
            var empty = new CatalogException("levels_empty", "The levels value did not contain any relationship names.");
            empty.Table = definition.Root.LogicalName;
            empty.ProvidedValue = levelsSpec;
            throw empty;
        }

        if (definition.Levels.Count == 1 && definition.Levels[0].Relationship.IsSelfReference)
        {
            definition.SelfReference = true;
            definition.Repeating = true;
            definition.Hierarchical = definition.Levels[0].Relationship.IsHierarchical;
        }
    }

    private JObject DefinitionToJson(CatalogDefinition definition, string strategy)
    {
        var levels = new JArray();

        for (var index = 0; index < definition.Levels.Count; index++)
        {
            var level = definition.Levels[index];

            levels.Add(new JObject
            {
                ["depth"] = index + 1,
                ["table"] = level.Table.LogicalName,
                ["entitySet"] = level.Table.EntitySetName,
                ["relationship"] = level.Relationship.SchemaName,
                ["lookupAttribute"] = level.Relationship.LookupAttribute,
                ["labelAttribute"] = level.Table.PrimaryName
            });
        }

        return new JObject
        {
            ["rootTable"] = definition.Root.LogicalName,
            ["rootEntitySet"] = definition.Root.EntitySetName,
            ["labelAttribute"] = definition.Root.PrimaryName,
            ["mode"] = definition.Repeating ? "selfReference" : "levels",
            ["repeating"] = definition.Repeating,
            ["hierarchical"] = definition.Hierarchical,
            ["recommendedStrategy"] = strategy,
            ["levelsPath"] = definition.LevelsPath,
            ["levels"] = levels,
            ["notes"] = new JArray(definition.Notes)
        };
    }

    // ------------------------------------------------------------ traversal

    private static IEnumerable<List<string>> Chunk(List<string> source, int size)
    {
        for (var index = 0; index < source.Count; index += size)
        {
            yield return source.Skip(index).Take(size).ToList();
        }
    }

    private static string CombineFilters(List<string> filters)
    {
        var usable = filters.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        return usable.Count == 0 ? null : string.Join(" and ", usable);
    }

    private static string BuildSelect(TableInfo table, RelationshipInfo parentRelationship, List<string> fields)
    {
        var columns = new List<string> { table.PrimaryId };

        if (!string.IsNullOrEmpty(table.PrimaryName))
        {
            columns.Add(table.PrimaryName);
        }

        if (parentRelationship != null)
        {
            columns.Add(parentRelationship.LookupValueField);
        }

        if (fields != null)
        {
            columns.AddRange(fields);
        }

        return string.Join(",", columns.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildOrderBy(TableInfo table)
    {
        return string.IsNullOrEmpty(table.PrimaryName) ? null : table.PrimaryName + " asc";
    }

    private async Task<JObject> QueryRecordsAsync(TableInfo table, string filter, string orderBy, int top, string select)
    {
        var url = table.EntitySetName + "?$select=" + Uri.EscapeDataString(select);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += "&$filter=" + Uri.EscapeDataString(filter);
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            url += "&$orderby=" + Uri.EscapeDataString(orderBy);
        }

        url += "&$top=" + Math.Max(1, top);

        return await GetJsonAsync(url).ConfigureAwait(false);
    }

    private async Task<JObject> GetRecordAsync(TableInfo table, string id, string select)
    {
        var url = table.EntitySetName + "(" + RequireGuid(id, "id") + ")?$select=" + Uri.EscapeDataString(select);
        return await GetJsonAsync(url).ConfigureAwait(false);
    }

    private static Node ToNode(JObject record, TableInfo table, int depth, Node parent, List<string> fields)
    {
        var label = string.IsNullOrEmpty(table.PrimaryName) ? null : record.Value<string>(table.PrimaryName);

        if (string.IsNullOrEmpty(label))
        {
            label = "(unnamed " + table.LogicalName + ")";
        }

        var node = new Node
        {
            Id = record.Value<string>(table.PrimaryId),
            Table = table.LogicalName,
            EntitySet = table.EntitySetName,
            Label = label,
            Depth = depth,
            ParentId = parent != null ? parent.Id : null,
            Path = parent != null ? parent.Path + " / " + label : label
        };

        if (fields != null && fields.Count > 0)
        {
            var extras = new JObject();

            foreach (var field in fields)
            {
                if (record[field] != null)
                {
                    extras[field] = record[field];
                }
            }

            node.Fields = extras;
        }

        return node;
    }

    private string ChooseStrategy(CatalogDefinition definition, TreeOptions options)
    {
        var requested = (options.Strategy ?? "auto").ToLowerInvariant();
        var hasRoot = !string.IsNullOrEmpty(options.RootId);
        var hasFilter = !string.IsNullOrWhiteSpace(options.Filter);

        if (requested == "hierarchy")
        {
            if (!definition.Repeating || !definition.Hierarchical || !hasRoot || hasFilter)
            {
                var unavailable = new CatalogException("strategy_unavailable",
                    "The hierarchy strategy needs a root id and a self-referencing relationship marked hierarchical, and cannot apply a filter.");
                unavailable.Table = definition.Root.LogicalName;
                unavailable.ProvidedValue = "hierarchy";
                unavailable.ValidValues = new JArray(
                    new JObject { ["value"] = "auto", ["description"] = "Pick the cheapest correct engine" },
                    new JObject { ["value"] = "levels", ["description"] = "Batched query per level, works everywhere" });
                unavailable.Hint = "Use strategy=auto.";
                throw unavailable;
            }

            return "hierarchy";
        }

        if (requested == "expand")
        {
            if (!hasRoot || options.Depth > MaxExpandChain)
            {
                var unavailable = new CatalogException("strategy_unavailable",
                    "The expand strategy needs a root id and a depth of at most " + MaxExpandChain + ".");
                unavailable.ProvidedValue = "expand";
                unavailable.Hint = "Use strategy=auto.";
                throw unavailable;
            }

            return "expand";
        }

        if (requested == "levels")
        {
            return "levels";
        }

        if (definition.Repeating && definition.Hierarchical && hasRoot && !hasFilter)
        {
            return "hierarchy";
        }

        return "levels";
    }

    private CatalogLevel LevelAt(CatalogDefinition definition, int depth)
    {
        if (definition.Repeating)
        {
            return definition.Levels[0];
        }

        var index = depth - 1;
        return index >= 0 && index < definition.Levels.Count ? definition.Levels[index] : null;
    }

    private RelationshipInfo ParentRelationshipOf(CatalogDefinition definition, TableInfo table)
    {
        if (definition.Repeating)
        {
            return string.Equals(table.LogicalName, definition.Root.LogicalName, StringComparison.OrdinalIgnoreCase)
                ? definition.Levels[0].Relationship
                : null;
        }

        foreach (var level in definition.Levels)
        {
            if (string.Equals(level.Table.LogicalName, table.LogicalName, StringComparison.OrdinalIgnoreCase))
            {
                return level.Relationship;
            }
        }

        return null;
    }

    private async Task<TreeResult> WalkAsync(CatalogDefinition definition, TreeOptions options, string strategy)
    {
        if (strategy == "hierarchy")
        {
            return await WalkHierarchyAsync(definition, options).ConfigureAwait(false);
        }

        if (strategy == "expand")
        {
            return await WalkExpandAsync(definition, options).ConfigureAwait(false);
        }

        return await WalkLevelsAsync(definition, options).ConfigureAwait(false);
    }

    /// <summary>One batched query per level. Works for any shape and keeps full control of limits.</summary>
    private async Task<TreeResult> WalkLevelsAsync(CatalogDefinition definition, TreeOptions options)
    {
        var result = new TreeResult { Strategy = "levels" };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootRelationship = definition.Repeating ? definition.Levels[0].Relationship : null;
        var rootSelect = BuildSelect(definition.Root, rootRelationship, options.Fields);
        var frontier = new List<Node>();

        if (!string.IsNullOrEmpty(options.RootId))
        {
            var record = await GetRecordAsync(definition.Root, options.RootId, rootSelect).ConfigureAwait(false);
            var node = ToNode(record, definition.Root, 0, null, options.Fields);
            result.Roots.Add(node);
            frontier.Add(node);
        }
        else
        {
            var filters = new List<string>();

            if (definition.Repeating)
            {
                filters.Add(rootRelationship.LookupValueField + " eq null");
            }

            if (!string.IsNullOrWhiteSpace(options.Filter))
            {
                filters.Add("(" + options.Filter + ")");
            }

            var rootTop = Math.Min(options.RootTop, options.MaxNodes);
            var response = await QueryRecordsAsync(definition.Root, CombineFilters(filters),
                BuildOrderBy(definition.Root), rootTop + 1, rootSelect).ConfigureAwait(false);

            var rows = (response["value"] as JArray) ?? new JArray();

            if (rows.Count > rootTop)
            {
                result.Truncated = true;
                result.TruncationReason = "rootLimit";
            }

            foreach (var row in rows.OfType<JObject>().Take(rootTop))
            {
                var node = ToNode(row, definition.Root, 0, null, options.Fields);
                result.Roots.Add(node);
                frontier.Add(node);
            }
        }

        foreach (var node in frontier)
        {
            visited.Add(node.Id);
            result.All.Add(node);
        }

        var effectiveDepth = options.Depth;

        if (!definition.Repeating && effectiveDepth > definition.Levels.Count)
        {
            effectiveDepth = definition.Levels.Count;
        }

        for (var depth = 1; depth <= effectiveDepth && frontier.Count > 0; depth++)
        {
            var level = LevelAt(definition, depth);

            if (level == null)
            {
                break;
            }

            var childTable = level.Table;
            var relationship = level.Relationship;
            var select = BuildSelect(childTable, relationship, options.Fields);
            var parentsById = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in frontier)
            {
                parentsById[node.Id] = node;
            }

            var nextFrontier = new List<Node>();
            var budgetExhausted = false;

            foreach (var chunk in Chunk(frontier.Select(n => n.Id).ToList(), IdChunkSize))
            {
                var budget = options.MaxNodes - result.All.Count;

                if (budget <= 0)
                {
                    budgetExhausted = true;
                    break;
                }

                var clause = "(" + string.Join(" or ",
                    chunk.Select(id => relationship.LookupValueField + " eq " + id)) + ")";

                var filters = new List<string> { clause };

                if (!string.IsNullOrWhiteSpace(options.Filter))
                {
                    filters.Add("(" + options.Filter + ")");
                }

                var top = Math.Min(PageSize, budget + 1);
                var response = await QueryRecordsAsync(childTable, CombineFilters(filters),
                    BuildOrderBy(childTable), top, select).ConfigureAwait(false);

                var rows = (response["value"] as JArray) ?? new JArray();
                var overflow = rows.Count > budget;
                var take = overflow ? budget : rows.Count;

                for (var index = 0; index < take; index++)
                {
                    var row = rows[index] as JObject;

                    if (row == null)
                    {
                        continue;
                    }

                    var parentId = row.Value<string>(relationship.LookupValueField);
                    Node parent;

                    if (string.IsNullOrEmpty(parentId) || !parentsById.TryGetValue(parentId, out parent))
                    {
                        continue;
                    }

                    var childId = row.Value<string>(childTable.PrimaryId);

                    if (string.IsNullOrEmpty(childId) || !visited.Add(childId))
                    {
                        result.Cycles++;
                        continue;
                    }

                    var node = ToNode(row, childTable, depth, parent, options.Fields);
                    parent.Children.Add(node);
                    result.All.Add(node);
                    nextFrontier.Add(node);
                }

                if (overflow)
                {
                    budgetExhausted = true;
                    break;
                }

                if (rows.Count >= PageSize)
                {
                    result.Truncated = true;
                    result.TruncationReason = result.TruncationReason ?? "pageLimit";
                }
            }

            if (nextFrontier.Count > 0)
            {
                result.MaxDepthReached = depth;
            }

            if (budgetExhausted)
            {
                result.Truncated = true;
                result.TruncationReason = "nodeBudget";
                frontier = nextFrontier;
                break;
            }

            frontier = nextFrontier;
        }

        foreach (var node in result.All)
        {
            node.ChildCount = node.Children.Count;
        }

        if (options.IncludeChildCounts && frontier.Count > 0)
        {
            await ApplyBoundaryChildCountsAsync(definition, options, result, frontier).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Counts children of the deepest returned nodes in one batched pass, so the caller can say
    /// "12 more below this node" instead of inventing them or implying the branch is a leaf.
    /// </summary>
    private async Task ApplyBoundaryChildCountsAsync(CatalogDefinition definition, TreeOptions options,
        TreeResult result, List<Node> boundary)
    {
        var nextDepth = boundary[0].Depth + 1;
        var level = LevelAt(definition, nextDepth);

        if (level == null)
        {
            return;
        }

        var relationship = level.Relationship;
        var childTable = level.Table;
        var select = string.Join(",", new[] { childTable.PrimaryId, relationship.LookupValueField });
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in Chunk(boundary.Select(n => n.Id).ToList(), IdChunkSize))
        {
            var clause = "(" + string.Join(" or ",
                chunk.Select(id => relationship.LookupValueField + " eq " + id)) + ")";

            var filters = new List<string> { clause };

            if (!string.IsNullOrWhiteSpace(options.Filter))
            {
                filters.Add("(" + options.Filter + ")");
            }

            JObject response;

            try
            {
                response = await QueryRecordsAsync(childTable, CombineFilters(filters), null, PageSize, select)
                    .ConfigureAwait(false);
            }
            catch (CatalogException)
            {
                return;
            }

            var rows = (response["value"] as JArray) ?? new JArray();

            foreach (var row in rows.OfType<JObject>())
            {
                var parentId = row.Value<string>(relationship.LookupValueField);

                if (string.IsNullOrEmpty(parentId))
                {
                    continue;
                }

                counts[parentId] = counts.ContainsKey(parentId) ? counts[parentId] + 1 : 1;
            }
        }

        foreach (var node in boundary)
        {
            int count;

            if (!counts.TryGetValue(node.Id, out count) || count == 0)
            {
                continue;
            }

            node.ChildCount = count;
            node.HasMoreChildren = true;
            result.Truncated = true;
            result.TruncationReason = result.TruncationReason ?? "depthLimit";
        }
    }

    /// <summary>Single FetchXML query using the hierarchical eq-or-under operator.</summary>
    private async Task<TreeResult> WalkHierarchyAsync(CatalogDefinition definition, TreeOptions options)
    {
        var result = new TreeResult { Strategy = "hierarchy" };
        var table = definition.Root;
        var relationship = definition.Levels[0].Relationship;
        var rootId = RequireGuid(options.RootId, "id");
        var fetchCount = Math.Min(options.MaxNodes + 1, MaxNodeBudget);

        var fetch = new StringBuilder();
        fetch.Append("<fetch mapping='logical' count='").Append(fetchCount).Append("'>");
        fetch.Append("<entity name='").Append(table.LogicalName).Append("'>");
        fetch.Append("<attribute name='").Append(table.PrimaryId).Append("' />");

        if (!string.IsNullOrEmpty(table.PrimaryName))
        {
            fetch.Append("<attribute name='").Append(table.PrimaryName).Append("' />");
        }

        fetch.Append("<attribute name='").Append(relationship.LookupAttribute).Append("' />");

        foreach (var field in options.Fields)
        {
            fetch.Append("<attribute name='").Append(field).Append("' />");
        }

        if (!string.IsNullOrEmpty(table.PrimaryName))
        {
            fetch.Append("<order attribute='").Append(table.PrimaryName).Append("' />");
        }

        fetch.Append("<filter><condition attribute='").Append(table.PrimaryId)
            .Append("' operator='eq-or-under' value='").Append(rootId).Append("' /></filter>");
        fetch.Append("</entity></fetch>");

        var url = table.EntitySetName + "?fetchXml=" + Uri.EscapeDataString(fetch.ToString());
        var response = await GetJsonAsync(url).ConfigureAwait(false);
        var rows = (response["value"] as JArray) ?? new JArray();

        var childrenByParent = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        JObject rootRecord = null;

        foreach (var row in rows.OfType<JObject>())
        {
            var id = row.Value<string>(table.PrimaryId);

            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (string.Equals(id, rootId, StringComparison.OrdinalIgnoreCase))
            {
                rootRecord = row;
                continue;
            }

            var parentId = row.Value<string>(relationship.LookupValueField);

            if (string.IsNullOrEmpty(parentId))
            {
                continue;
            }

            if (!childrenByParent.ContainsKey(parentId))
            {
                childrenByParent[parentId] = new List<JObject>();
            }

            childrenByParent[parentId].Add(row);
        }

        if (rootRecord == null)
        {
            var notFound = new CatalogException("record_not_found",
                "No record with id " + rootId + " exists in '" + table.LogicalName + "'.");
            notFound.Table = table.LogicalName;
            notFound.ProvidedValue = rootId;
            notFound.Hint = "Use search_catalog to resolve a name to a real id.";
            throw notFound;
        }

        var rootNode = ToNode(rootRecord, table, 0, null, options.Fields);
        result.Roots.Add(rootNode);
        result.All.Add(rootNode);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootNode.Id };
        var frontier = new List<Node> { rootNode };

        for (var depth = 1; depth <= options.Depth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<Node>();

            foreach (var parent in frontier)
            {
                List<JObject> children;

                if (!childrenByParent.TryGetValue(parent.Id, out children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (result.All.Count >= options.MaxNodes)
                    {
                        result.Truncated = true;
                        result.TruncationReason = "nodeBudget";
                        parent.HasMoreChildren = true;
                        break;
                    }

                    var childId = child.Value<string>(table.PrimaryId);

                    if (!visited.Add(childId))
                    {
                        result.Cycles++;
                        continue;
                    }

                    var node = ToNode(child, table, depth, parent, options.Fields);
                    parent.Children.Add(node);
                    result.All.Add(node);
                    nextFrontier.Add(node);
                }
            }

            if (nextFrontier.Count > 0)
            {
                result.MaxDepthReached = depth;
            }

            frontier = nextFrontier;
        }

        // The single query already returned the whole subtree, so counts are exact and free.
        foreach (var node in result.All)
        {
            List<JObject> children;
            var actual = childrenByParent.TryGetValue(node.Id, out children) ? children.Count : 0;

            node.ChildCount = actual;

            if (actual > node.Children.Count)
            {
                node.HasMoreChildren = true;
                result.Truncated = true;
                result.TruncationReason = result.TruncationReason ?? "depthLimit";
            }
        }

        if (rows.Count >= fetchCount)
        {
            result.Truncated = true;
            result.TruncationReason = "nodeBudget";
        }

        return result;
    }

    /// <summary>Single request using a nested $expand chain. Dataverse allows nested collection expands.</summary>
    private async Task<TreeResult> WalkExpandAsync(CatalogDefinition definition, TreeOptions options)
    {
        var result = new TreeResult { Strategy = "expand" };
        var rootId = RequireGuid(options.RootId, "id");
        var rootRelationship = definition.Repeating ? definition.Levels[0].Relationship : null;
        var rootSelect = BuildSelect(definition.Root, rootRelationship, options.Fields);
        var expand = BuildExpandClause(definition, options, options.Depth, 0);

        // A nested collection expand is rejected on a key-addressed URL, so the root is
        // selected from the collection with a filter on its primary key instead.
        var url = definition.Root.EntitySetName + "?$select=" + Uri.EscapeDataString(rootSelect)
            + "&$filter=" + Uri.EscapeDataString(definition.Root.PrimaryId + " eq " + rootId);

        if (!string.IsNullOrEmpty(expand))
        {
            url += "&$expand=" + expand;
        }

        var response = await GetJsonAsync(url).ConfigureAwait(false);
        var record = ((response["value"] as JArray) ?? new JArray()).OfType<JObject>().FirstOrDefault();

        if (record == null)
        {
            var notFound = new CatalogException("record_not_found",
                "No record with id " + rootId + " exists in '" + definition.Root.LogicalName + "'.");
            notFound.Table = definition.Root.LogicalName;
            notFound.ProvidedValue = rootId;
            notFound.Hint = "Use search_catalog to resolve a name to a real id.";
            throw notFound;
        }

        var rootNode = ToNode(record, definition.Root, 0, null, options.Fields);

        result.Roots.Add(rootNode);
        result.All.Add(rootNode);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootNode.Id };
        MapExpandedChildren(definition, options, result, record, rootNode, 1, visited);

        foreach (var node in result.All)
        {
            node.ChildCount = node.Children.Count;
        }

        return result;
    }

    private string BuildExpandClause(CatalogDefinition definition, TreeOptions options, int depthRemaining, int levelIndex)
    {
        if (depthRemaining <= 0)
        {
            return null;
        }

        var level = definition.Repeating
            ? definition.Levels[0]
            : (levelIndex < definition.Levels.Count ? definition.Levels[levelIndex] : null);

        if (level == null || string.IsNullOrEmpty(level.Relationship.ParentNavProperty))
        {
            return null;
        }

        var clause = level.Relationship.ParentNavProperty
            + "($select=" + BuildSelect(level.Table, level.Relationship, options.Fields);

        var inner = BuildExpandClause(definition, options, depthRemaining - 1, levelIndex + 1);

        if (!string.IsNullOrEmpty(inner))
        {
            clause += ";$expand=" + inner;
        }

        return clause + ")";
    }

    private void MapExpandedChildren(CatalogDefinition definition, TreeOptions options, TreeResult result,
        JObject record, Node parent, int depth, HashSet<string> visited)
    {
        var level = LevelAt(definition, depth);

        if (level == null || depth > options.Depth)
        {
            return;
        }

        var rows = record[level.Relationship.ParentNavProperty] as JArray;

        if (rows == null)
        {
            return;
        }

        foreach (var row in rows.OfType<JObject>())
        {
            if (result.All.Count >= options.MaxNodes)
            {
                result.Truncated = true;
                result.TruncationReason = "nodeBudget";
                parent.HasMoreChildren = true;
                return;
            }

            var childId = row.Value<string>(level.Table.PrimaryId);

            if (string.IsNullOrEmpty(childId) || !visited.Add(childId))
            {
                result.Cycles++;
                continue;
            }

            var node = ToNode(row, level.Table, depth, parent, options.Fields);
            parent.Children.Add(node);
            result.All.Add(node);

            if (depth > result.MaxDepthReached)
            {
                result.MaxDepthReached = depth;
            }

            MapExpandedChildren(definition, options, result, row, node, depth + 1, visited);
        }

        if (record[level.Relationship.ParentNavProperty + "@odata.nextLink"] != null)
        {
            parent.HasMoreChildren = true;
            result.Truncated = true;
            result.TruncationReason = result.TruncationReason ?? "pageLimit";
        }
    }

    // ------------------------------------------------------- path resolution

    private class PathCursor
    {
        public Node Node;
        public TableInfo PendingTable;
        public string PendingId;
        public List<string> Labels = new List<string>();
    }

    /// <summary>Resolves full root-down paths for a set of nodes using one batched query per level.</summary>
    private async Task ResolvePathsAsync(CatalogDefinition definition, List<Node> nodes,
        Dictionary<string, string> parentIdByNode)
    {
        var cursors = new List<PathCursor>();

        foreach (var node in nodes)
        {
            var cursor = new PathCursor { Node = node };
            var table = await GetTableAsync(node.Table).ConfigureAwait(false);
            var relationship = ParentRelationshipOf(definition, table);

            string parentId;
            parentIdByNode.TryGetValue(node.Id, out parentId);

            if (relationship != null && !string.IsNullOrEmpty(parentId))
            {
                cursor.PendingTable = await GetTableAsync(relationship.ParentTable).ConfigureAwait(false);
                cursor.PendingId = parentId;
                node.ParentId = parentId;
            }

            cursors.Add(cursor);
        }

        for (var step = 0; step < MaxDepth; step++)
        {
            var pending = cursors.Where(c => c.PendingTable != null && !string.IsNullOrEmpty(c.PendingId)).ToList();

            if (pending.Count == 0)
            {
                break;
            }

            foreach (var group in pending.GroupBy(c => c.PendingTable.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                var table = group.First().PendingTable;
                var relationship = ParentRelationshipOf(definition, table);
                var select = BuildSelect(table, relationship, null);
                var records = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                var ids = group.Select(c => c.PendingId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var chunk in Chunk(ids, IdChunkSize))
                {
                    var clause = "(" + string.Join(" or ",
                        chunk.Select(id => table.PrimaryId + " eq " + id)) + ")";

                    var response = await QueryRecordsAsync(table, clause, null, chunk.Count, select)
                        .ConfigureAwait(false);

                    foreach (var row in ((response["value"] as JArray) ?? new JArray()).OfType<JObject>())
                    {
                        var id = row.Value<string>(table.PrimaryId);

                        if (!string.IsNullOrEmpty(id))
                        {
                            records[id] = row;
                        }
                    }
                }

                foreach (var cursor in group)
                {
                    JObject record;

                    if (!records.TryGetValue(cursor.PendingId, out record))
                    {
                        cursor.PendingTable = null;
                        cursor.PendingId = null;
                        continue;
                    }

                    var label = string.IsNullOrEmpty(table.PrimaryName)
                        ? table.LogicalName
                        : record.Value<string>(table.PrimaryName);

                    cursor.Labels.Insert(0, label ?? table.LogicalName);

                    if (relationship == null)
                    {
                        cursor.PendingTable = null;
                        cursor.PendingId = null;
                        continue;
                    }

                    var nextId = record.Value<string>(relationship.LookupValueField);

                    if (string.IsNullOrEmpty(nextId) || cursor.Labels.Count >= MaxDepth)
                    {
                        cursor.PendingTable = null;
                        cursor.PendingId = null;
                        continue;
                    }

                    cursor.PendingTable = await GetTableAsync(relationship.ParentTable).ConfigureAwait(false);
                    cursor.PendingId = nextId;
                }
            }
        }

        foreach (var cursor in cursors)
        {
            cursor.Labels.Add(cursor.Node.Label);
            cursor.Node.Path = string.Join(" / ", cursor.Labels);
            cursor.Node.Depth = cursor.Labels.Count - 1;
        }
    }

    // --------------------------------------------------------- serialization

    private static JObject NodeToJson(Node node, bool includeChildren)
    {
        var payload = new JObject
        {
            ["id"] = node.Id,
            ["table"] = node.Table,
            ["entitySet"] = node.EntitySet,
            ["label"] = node.Label,
            ["depth"] = node.Depth,
            ["parentId"] = node.ParentId ?? string.Empty,
            ["path"] = node.Path,
            ["childCount"] = node.ChildCount ?? node.Children.Count,
            ["hasMoreChildren"] = node.HasMoreChildren
        };

        if (node.Fields != null && node.Fields.Count > 0)
        {
            payload["fields"] = node.Fields;
        }

        if (includeChildren)
        {
            var children = new JArray();

            foreach (var child in node.Children)
            {
                children.Add(NodeToJson(child, true));
            }

            payload["children"] = children;
        }

        return payload;
    }

    private static JArray NodesToJson(IEnumerable<Node> nodes, bool includeChildren)
    {
        var array = new JArray();

        foreach (var node in nodes)
        {
            array.Add(NodeToJson(node, includeChildren));
        }

        return array;
    }

    private static string BuildOutline(List<Node> roots)
    {
        var builder = new StringBuilder();

        foreach (var root in roots)
        {
            AppendOutline(builder, root);
        }

        return builder.ToString();
    }

    private static void AppendOutline(StringBuilder builder, Node node)
    {
        builder.Append(new string(' ', node.Depth * 2));
        builder.Append(node.Label);
        builder.Append(" (").Append(node.Table).Append(", ").Append(node.Id).Append(")");

        var known = node.ChildCount ?? node.Children.Count;

        if (node.HasMoreChildren || known > node.Children.Count)
        {
            builder.Append(" [").Append(known - node.Children.Count).Append(" more children not expanded]");
        }

        builder.Append("\n");

        foreach (var child in node.Children)
        {
            AppendOutline(builder, child);
        }
    }

    private JObject BuildTreeResponse(CatalogDefinition definition, TreeOptions options, TreeResult result)
    {
        var format = (options.Format ?? "tree").ToLowerInvariant();

        var payload = new JObject
        {
            ["definition"] = DefinitionToJson(definition, result.Strategy),
            ["stats"] = new JObject
            {
                ["nodeCount"] = result.All.Count,
                ["maxDepthReached"] = result.MaxDepthReached,
                ["depthRequested"] = options.Depth,
                ["truncated"] = result.Truncated,
                ["truncationReason"] = result.TruncationReason ?? string.Empty,
                ["cyclesDetected"] = result.Cycles,
                ["strategy"] = result.Strategy,
                ["dataverseRequests"] = _dataverseRequests,
                ["elapsedMs"] = (int)(DateTime.UtcNow - _startedUtc).TotalMilliseconds
            }
        };

        if (format == "tree" || format == "all")
        {
            payload["roots"] = NodesToJson(result.Roots, true);
        }

        if (format == "flat" || format == "all")
        {
            payload["flat"] = NodesToJson(result.All, false);
        }

        if (format == "outline" || format == "all")
        {
            payload["outline"] = BuildOutline(result.Roots);
        }

        return payload;
    }

    // ------------------------------------------------------ argument helpers

    private static string Str(JObject args, string key)
    {
        var token = args[key];
        return token == null || token.Type == JTokenType.Null ? null : token.ToString();
    }

    private static int Int(JObject args, string key, int fallback, int minimum, int maximum)
    {
        var raw = Str(args, key);
        int parsed;

        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out parsed))
        {
            return fallback;
        }

        return Math.Max(minimum, Math.Min(maximum, parsed));
    }

    private static bool Bool(JObject args, string key, bool fallback)
    {
        var raw = Str(args, key);
        bool parsed;
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out parsed) ? fallback : parsed;
    }

    private static List<string> ReadFields(JObject args)
    {
        var fields = new List<string>();
        var raw = Str(args, "fields");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fields;
        }

        foreach (var candidate in raw.Split(','))
        {
            var sanitized = SanitizeIdentifier(candidate);

            if (sanitized == null)
            {
                var invalid = new CatalogException("invalid_field", "'" + candidate.Trim() + "' is not a valid column name.");
                invalid.ProvidedValue = candidate.Trim();
                invalid.Hint = "Column names contain only letters, digits and underscores.";
                throw invalid;
            }

            fields.Add(sanitized);
        }

        return fields;
    }

    private static TreeOptions ReadTreeOptions(JObject args)
    {
        return new TreeOptions
        {
            RootId = Str(args, "id"),
            Depth = Int(args, "depth", DefaultDepth, 1, MaxDepth),
            MaxNodes = Int(args, "maxNodes", DefaultMaxNodes, 1, MaxNodeBudget),
            Fields = ReadFields(args),
            Filter = Str(args, "filter"),
            Format = Str(args, "format") ?? "tree",
            Strategy = Str(args, "strategy") ?? "auto",
            IncludeChildCounts = Bool(args, "includeChildCounts", true),
            RootTop = Int(args, "rootTop", 50, 1, 500)
        };
    }

    private JObject ArgsFromQuery()
    {
        var args = new JObject();

        foreach (var pair in ParseQuery())
        {
            args[pair.Key] = pair.Value;
        }

        return args;
    }

    // ----------------------------------------------------------- operations

    private async Task<JObject> OpDescribeAsync(JObject args)
    {
        var tableName = Str(args, "table");
        var levels = Str(args, "levels");
        var table = await GetTableAsync(tableName).ConfigureAwait(false);

        CatalogDefinition definition = null;
        string undetermined = null;

        try
        {
            definition = await ResolveDefinitionAsync(tableName, levels).ConfigureAwait(false);
        }
        catch (CatalogException cx) when (cx.Code == "levels_required" || cx.Code == "ambiguous_self_reference")
        {
            undetermined = cx.Message;
        }

        JObject payload;

        if (definition == null)
        {
            payload = new JObject
            {
                ["rootTable"] = table.LogicalName,
                ["rootEntitySet"] = table.EntitySetName,
                ["labelAttribute"] = table.PrimaryName,
                ["mode"] = "undetermined",
                ["repeating"] = false,
                ["hierarchical"] = false,
                ["recommendedStrategy"] = "levels",
                ["levelsPath"] = string.Empty,
                ["levels"] = new JArray(),
                ["notes"] = new JArray(undetermined)
            };
        }
        else
        {
            payload = DefinitionToJson(definition,
                definition.Repeating && definition.Hierarchical ? "hierarchy" : "levels");
        }

        var candidateTable = definition != null && definition.Levels.Count > 0
            ? definition.Levels[definition.Levels.Count - 1].Table
            : table;

        var candidates = await GetCandidateRelationshipsAsync(candidateTable, true).ConfigureAwait(false);
        var candidateJson = new JArray();

        foreach (var candidate in candidates.Take(MaxValidValues))
        {
            candidateJson.Add(await RelationshipToJsonAsync(candidate).ConfigureAwait(false));
        }

        payload["candidateRelationships"] = candidateJson;

        return payload;
    }

    private async Task<JObject> OpGetTreeAsync(JObject args)
    {
        var tableName = Str(args, "table");
        var levels = Str(args, "levels");
        var definition = await ResolveDefinitionAsync(tableName, levels).ConfigureAwait(false);
        var options = ReadTreeOptions(args);
        var strategy = ChooseStrategy(definition, options);
        var result = await WalkAsync(definition, options, strategy).ConfigureAwait(false);

        return BuildTreeResponse(definition, options, result);
    }

    private async Task<JObject> OpGetChildrenAsync(JObject args)
    {
        var table = await GetTableAsync(Str(args, "table")).ConfigureAwait(false);
        var parentId = RequireGuid(Str(args, "id"), "id");
        var relationship = await ResolveChildRelationshipAsync(table, Str(args, "relationship")).ConfigureAwait(false);
        var childTable = await GetTableAsync(relationship.ChildTable).ConfigureAwait(false);
        var fields = ReadFields(args);
        var top = Int(args, "top", 100, 1, 1000);
        var select = BuildSelect(childTable, relationship, fields);

        var filters = new List<string> { relationship.LookupValueField + " eq " + parentId };
        var extraFilter = Str(args, "filter");

        if (!string.IsNullOrWhiteSpace(extraFilter))
        {
            filters.Add("(" + extraFilter + ")");
        }

        var response = await QueryRecordsAsync(childTable, CombineFilters(filters),
            BuildOrderBy(childTable), top + 1, select).ConfigureAwait(false);

        var rows = (response["value"] as JArray) ?? new JArray();
        var hasMore = rows.Count > top;

        var parentSelect = BuildSelect(table, null, null);
        var parentRecord = await GetRecordAsync(table, parentId, parentSelect).ConfigureAwait(false);
        var parentNode = ToNode(parentRecord, table, 0, null, null);

        var nodes = new JArray();

        foreach (var row in rows.OfType<JObject>().Take(top))
        {
            nodes.Add(NodeToJson(ToNode(row, childTable, 1, parentNode, fields), false));
        }

        var definition = new CatalogDefinition
        {
            Root = table,
            Repeating = relationship.IsSelfReference,
            SelfReference = relationship.IsSelfReference,
            Hierarchical = relationship.IsHierarchical,
            LevelsPath = relationship.SchemaName
        };

        definition.Levels.Add(new CatalogLevel { Table = childTable, Relationship = relationship });

        return new JObject
        {
            ["definition"] = DefinitionToJson(definition, "levels"),
            ["totalCount"] = nodes.Count,
            ["hasMore"] = hasMore,
            ["value"] = nodes
        };
    }

    private async Task<JObject> OpGetAncestorsAsync(JObject args)
    {
        var tableName = Str(args, "table");
        var table = await GetTableAsync(tableName).ConfigureAwait(false);
        var id = RequireGuid(Str(args, "id"), "id");
        var rootTable = Str(args, "rootTable") ?? tableName;
        var definition = await ResolveDefinitionAsync(rootTable, Str(args, "levels")).ConfigureAwait(false);
        var chain = await GetAncestorChainAsync(definition, table, id).ConfigureAwait(false);
        var node = chain[chain.Count - 1];
        var ancestors = chain.Take(chain.Count - 1).ToList();

        return new JObject
        {
            ["node"] = NodeToJson(node, false),
            ["path"] = node.Path,
            ["depth"] = node.Depth,
            ["ancestors"] = NodesToJson(ancestors, false)
        };
    }

    private async Task<List<Node>> GetAncestorChainAsync(CatalogDefinition definition, TableInfo table, string id)
    {
        var chain = new List<Node>();
        var currentTable = table;
        var currentId = id;
        var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var step = 0; step <= MaxDepth; step++)
        {
            if (!guard.Add(currentId))
            {
                break;
            }

            var relationship = ParentRelationshipOf(definition, currentTable);
            var select = BuildSelect(currentTable, relationship, null);
            var record = await GetRecordAsync(currentTable, currentId, select).ConfigureAwait(false);
            var node = ToNode(record, currentTable, 0, null, null);

            chain.Insert(0, node);

            if (relationship == null)
            {
                break;
            }

            var parentId = record.Value<string>(relationship.LookupValueField);

            if (string.IsNullOrEmpty(parentId))
            {
                break;
            }

            node.ParentId = parentId;
            currentTable = await GetTableAsync(relationship.ParentTable).ConfigureAwait(false);
            currentId = parentId;
        }

        var labels = new List<string>();

        for (var index = 0; index < chain.Count; index++)
        {
            labels.Add(chain[index].Label);
            chain[index].Depth = index;
            chain[index].Path = string.Join(" / ", labels);
        }

        return chain;
    }

    private async Task<JObject> OpSearchAsync(JObject args)
    {
        var tableName = Str(args, "table");
        var term = Str(args, "search");

        if (string.IsNullOrWhiteSpace(term))
        {
            var missing = new CatalogException("search_required", "A search term is required.");
            missing.Hint = "Pass search=<text> to match against the primary name column.";
            throw missing;
        }

        var definition = await ResolveDefinitionAsync(tableName, Str(args, "levels")).ConfigureAwait(false);
        var depth = Int(args, "depth", 5, 1, MaxDepth);
        var top = Int(args, "top", 20, 1, 100);

        var tables = new List<TableInfo> { definition.Root };

        if (!definition.Repeating)
        {
            for (var index = 0; index < definition.Levels.Count && index < depth; index++)
            {
                tables.Add(definition.Levels[index].Table);
            }
        }

        var nodes = new List<Node>();
        var parentIdByNode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hasMore = false;

        foreach (var table in tables.GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            if (string.IsNullOrEmpty(table.PrimaryName))
            {
                continue;
            }

            var relationship = ParentRelationshipOf(definition, table);
            var select = BuildSelect(table, relationship, null);
            var filter = "contains(" + table.PrimaryName + ",'" + EscapeOData(term) + "')";

            var response = await QueryRecordsAsync(table, filter, BuildOrderBy(table), top + 1, select)
                .ConfigureAwait(false);

            var rows = (response["value"] as JArray) ?? new JArray();

            if (rows.Count > top)
            {
                hasMore = true;
            }

            foreach (var row in rows.OfType<JObject>().Take(top))
            {
                var node = ToNode(row, table, 0, null, null);
                nodes.Add(node);

                if (relationship != null)
                {
                    var parentId = row.Value<string>(relationship.LookupValueField);

                    if (!string.IsNullOrEmpty(parentId))
                    {
                        parentIdByNode[node.Id] = parentId;
                    }
                }
            }
        }

        if (nodes.Count > 0)
        {
            await ResolvePathsAsync(definition, nodes, parentIdByNode).ConfigureAwait(false);
        }

        var ordered = nodes.OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase).Take(top).ToList();

        return new JObject
        {
            ["definition"] = DefinitionToJson(definition, definition.Repeating && definition.Hierarchical ? "hierarchy" : "levels"),
            ["totalCount"] = ordered.Count,
            ["hasMore"] = hasMore,
            ["value"] = NodesToJson(ordered, false)
        };
    }

    private async Task<RelationshipInfo> ResolveChildRelationshipAsync(TableInfo parent, string relationshipName)
    {
        if (!string.IsNullOrWhiteSpace(relationshipName))
        {
            var match = parent.Children.FirstOrDefault(r =>
                string.Equals(r.SchemaName, relationshipName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.ParentNavProperty, relationshipName, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return match;
            }

            var candidates = await GetCandidateRelationshipsAsync(parent, true).ConfigureAwait(false);
            var unknown = new CatalogException("unknown_relationship",
                "'" + relationshipName + "' is not a relationship from '" + parent.LogicalName + "'.");
            unknown.Table = parent.LogicalName;
            unknown.ProvidedValue = relationshipName;
            unknown.ValidValues = new JArray(candidates.Take(MaxValidValues).Select(r => new JObject
            {
                ["value"] = r.SchemaName,
                ["description"] = "Children in " + r.ChildTable + " via " + r.LookupAttribute
            }));
            unknown.Hint = "Retry with one of these relationship schema names.";
            throw unknown;
        }

        var selfReferences = parent.Children.Where(r => r.IsSelfReference).ToList();
        var chosen = selfReferences.FirstOrDefault(r => r.IsHierarchical);

        if (chosen == null && selfReferences.Count == 1)
        {
            chosen = selfReferences[0];
        }

        if (chosen != null)
        {
            return chosen;
        }

        var options = await GetCandidateRelationshipsAsync(parent, true).ConfigureAwait(false);
        var required = new CatalogException("relationship_required",
            "'" + parent.LogicalName + "' has no single self-referencing parent lookup, so the child relationship must be named.");
        required.Table = parent.LogicalName;
        required.ValidValues = new JArray(options.Take(MaxValidValues).Select(r => new JObject
        {
            ["value"] = r.SchemaName,
            ["description"] = "Children in " + r.ChildTable + " via " + r.LookupAttribute
        }));
        required.Hint = "Pass relationship=<schemaName>.";
        throw required;
    }

    private async Task<JObject> OpCreateNodeAsync(JObject args)
    {
        var table = await GetTableAsync(Str(args, "table")).ConfigureAwait(false);
        var name = Str(args, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            var missing = new CatalogException("name_required", "A name is required to create a node.");
            missing.Table = table.LogicalName;
            missing.Hint = "Pass name=<text> for the " + table.PrimaryName + " column.";
            throw missing;
        }

        var record = new JObject();

        if (!string.IsNullOrEmpty(table.PrimaryName))
        {
            record[table.PrimaryName] = name;
        }

        var values = args["values"] as JObject;

        if (values != null)
        {
            foreach (var property in values.Properties())
            {
                var column = SanitizeIdentifier(property.Name);

                if (column == null)
                {
                    var invalid = new CatalogException("invalid_field", "'" + property.Name + "' is not a valid column name.");
                    invalid.Table = table.LogicalName;
                    invalid.ProvidedValue = property.Name;
                    throw invalid;
                }

                record[column] = property.Value;
            }
        }

        var parentId = Str(args, "parentId");
        RelationshipInfo relationship = null;

        if (!string.IsNullOrWhiteSpace(parentId))
        {
            parentId = RequireGuid(parentId, "parentId");
            var parentTable = await GetTableAsync(Str(args, "parentTable") ?? table.LogicalName).ConfigureAwait(false);
            relationship = await ResolveParentLinkAsync(parentTable, table, Str(args, "relationship")).ConfigureAwait(false);
            record[relationship.ChildNavProperty + "@odata.bind"] = "/" + parentTable.EntitySetName + "(" + parentId + ")";
        }

        var created = await SendDataverseAsync(HttpMethod.Post, table.EntitySetName, record, "return=representation")
            .ConfigureAwait(false);

        var node = ToNode(created, table, 0, null, null);

        if (relationship != null)
        {
            node.ParentId = parentId;
        }

        var definition = new CatalogDefinition
        {
            Root = table,
            Repeating = relationship != null && relationship.IsSelfReference,
            SelfReference = relationship != null && relationship.IsSelfReference,
            Hierarchical = relationship != null && relationship.IsHierarchical,
            LevelsPath = relationship != null ? relationship.SchemaName : string.Empty
        };

        if (relationship != null)
        {
            definition.Levels.Add(new CatalogLevel { Table = table, Relationship = relationship });
        }

        return new JObject
        {
            ["node"] = NodeToJson(node, false),
            ["definition"] = DefinitionToJson(definition, "levels")
        };
    }

    private async Task<JObject> OpMoveNodeAsync(JObject args)
    {
        var table = await GetTableAsync(Str(args, "table")).ConfigureAwait(false);
        var id = RequireGuid(Str(args, "id"), "id");
        var newParentId = Str(args, "newParentId");
        var parentTable = await GetTableAsync(Str(args, "parentTable") ?? table.LogicalName).ConfigureAwait(false);
        var relationship = await ResolveParentLinkAsync(parentTable, table, Str(args, "relationship")).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(newParentId))
        {
            await SendDataverseAsync(HttpMethod.Delete,
                table.EntitySetName + "(" + id + ")/" + relationship.ChildNavProperty + "/$ref", null, null)
                .ConfigureAwait(false);
        }
        else
        {
            newParentId = RequireGuid(newParentId, "newParentId");

            if (string.Equals(newParentId, id, StringComparison.OrdinalIgnoreCase))
            {
                var self = new CatalogException("cycle_detected", "A node cannot be its own parent.");
                self.Table = table.LogicalName;
                self.ProvidedValue = newParentId;
                throw self;
            }

            if (relationship.IsSelfReference)
            {
                var definitionForCheck = new CatalogDefinition
                {
                    Root = table,
                    Repeating = true,
                    SelfReference = true,
                    Hierarchical = relationship.IsHierarchical
                };

                definitionForCheck.Levels.Add(new CatalogLevel { Table = table, Relationship = relationship });

                var ancestors = await GetAncestorChainAsync(definitionForCheck, parentTable, newParentId)
                    .ConfigureAwait(false);

                if (ancestors.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    var cycle = new CatalogException("cycle_detected",
                        "The new parent sits below the node being moved, which would create a cycle.");
                    cycle.Table = table.LogicalName;
                    cycle.ProvidedValue = newParentId;
                    cycle.Hint = "Choose a parent that is not a descendant of this node.";
                    throw cycle;
                }
            }

            var patch = new JObject
            {
                [relationship.ChildNavProperty + "@odata.bind"] =
                    "/" + parentTable.EntitySetName + "(" + newParentId + ")"
            };

            await SendDataverseAsync(new HttpMethod("PATCH"), table.EntitySetName + "(" + id + ")", patch, null)
                .ConfigureAwait(false);
        }

        var select = BuildSelect(table, relationship, null);
        var updated = await GetRecordAsync(table, id, select).ConfigureAwait(false);
        var node = ToNode(updated, table, 0, null, null);
        node.ParentId = updated.Value<string>(relationship.LookupValueField);

        var definition = new CatalogDefinition
        {
            Root = parentTable,
            Repeating = relationship.IsSelfReference,
            SelfReference = relationship.IsSelfReference,
            Hierarchical = relationship.IsHierarchical,
            LevelsPath = relationship.SchemaName
        };

        definition.Levels.Add(new CatalogLevel { Table = table, Relationship = relationship });

        return new JObject
        {
            ["node"] = NodeToJson(node, false),
            ["definition"] = DefinitionToJson(definition, "levels")
        };
    }

    private async Task<JObject> OpUpdateNodeAsync(JObject args)
    {
        var table = await GetTableAsync(Str(args, "table")).ConfigureAwait(false);
        var id = RequireGuid(Str(args, "id"), "id");
        var name = Str(args, "name");
        var values = args["values"] as JObject;
        var patch = new JObject();

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrEmpty(table.PrimaryName))
        {
            patch[table.PrimaryName] = name;
        }

        if (values != null)
        {
            foreach (var property in values.Properties())
            {
                // The only way to set a lookup in a PATCH is a nav@odata.bind, so blocking
                // that here keeps every reparent on the path that has the cycle guard.
                if (property.Name.IndexOf('@') >= 0)
                {
                    var reparent = new CatalogException("use_move_instead",
                        "Parent lookups cannot be set through update_catalog_node.");
                    reparent.Table = table.LogicalName;
                    reparent.ProvidedValue = property.Name;
                    reparent.Hint = "Use move_catalog_node, which rejects moves that would create a cycle.";
                    throw reparent;
                }

                var column = SanitizeIdentifier(property.Name);

                if (column == null)
                {
                    var invalid = new CatalogException("invalid_field", "'" + property.Name + "' is not a valid column name.");
                    invalid.Table = table.LogicalName;
                    invalid.ProvidedValue = property.Name;
                    throw invalid;
                }

                patch[column] = property.Value;
            }
        }

        if (patch.Count == 0)
        {
            var empty = new CatalogException("nothing_to_update", "Supply a name, one or more values, or both.");
            empty.Table = table.LogicalName;
            empty.Hint = "Pass name=<text> to rename the node, or values={...} to set other columns.";
            throw empty;
        }

        await SendDataverseAsync(new HttpMethod("PATCH"), table.EntitySetName + "(" + id + ")", patch, null)
            .ConfigureAwait(false);

        var updated = await GetRecordAsync(table, id, BuildSelect(table, null, null)).ConfigureAwait(false);

        return new JObject
        {
            ["node"] = NodeToJson(ToNode(updated, table, 0, null, null), false),
            ["updatedColumns"] = new JArray(patch.Properties().Select(p => p.Name))
        };
    }

    private async Task<JObject> OpDeleteNodeAsync(JObject args)
    {
        var table = await GetTableAsync(Str(args, "table")).ConfigureAwait(false);
        var id = RequireGuid(Str(args, "id"), "id");
        var onChildren = (Str(args, "onChildren") ?? "refuse").ToLowerInvariant();
        var maxNodes = Int(args, "maxNodes", DefaultMaxNodes, 1, MaxNodeBudget);

        if (onChildren != "refuse" && onChildren != "reparent" && onChildren != "cascade")
        {
            var invalid = new CatalogException("invalid_on_children",
                "'" + onChildren + "' is not a supported child handling mode.");
            invalid.Table = table.LogicalName;
            invalid.ProvidedValue = onChildren;
            invalid.ValidValues = BuildChildModeValues();
            throw invalid;
        }

        var relationship = await ResolveChildRelationshipAsync(table, Str(args, "relationship")).ConfigureAwait(false);
        var childTable = await GetTableAsync(relationship.ChildTable).ConfigureAwait(false);

        var record = await GetRecordAsync(table, id,
            BuildSelect(table, relationship.IsSelfReference ? relationship : null, null)).ConfigureAwait(false);

        var node = ToNode(record, table, 0, null, null);

        var childResponse = await QueryRecordsAsync(childTable,
            relationship.LookupValueField + " eq " + id, BuildOrderBy(childTable), maxNodes + 1,
            BuildSelect(childTable, relationship, null)).ConfigureAwait(false);

        var children = ((childResponse["value"] as JArray) ?? new JArray()).OfType<JObject>().ToList();
        var reparented = 0;
        var descendantsDeleted = 0;

        if (children.Count > 0)
        {
            if (onChildren == "refuse")
            {
                var blocked = new CatalogException("node_has_children",
                    "'" + node.Label + "' has " + children.Count + " child record"
                    + (children.Count == 1 ? "" : "s") + " and was not deleted.");
                blocked.Table = table.LogicalName;
                blocked.ProvidedValue = id;
                blocked.ValidValues = BuildChildModeValues();
                blocked.Hint = "Re-send with onChildren set to reparent or cascade, after confirming with the user.";
                throw blocked;
            }

            if (!relationship.IsSelfReference)
            {
                var unsupported = new CatalogException("child_handling_unavailable",
                    "onChildren=" + onChildren + " is only supported for a self-referencing catalog, because the children of '"
                    + table.LogicalName + "' live in '" + childTable.LogicalName + "'.");
                unsupported.Table = table.LogicalName;
                unsupported.ProvidedValue = onChildren;
                unsupported.Hint = "Delete the deepest level first, then work upward.";
                throw unsupported;
            }

            if (onChildren == "reparent")
            {
                var newParentId = record.Value<string>(relationship.LookupValueField);
                EnsureDeleteBudget(children.Count + 1);

                foreach (var child in children)
                {
                    var childId = child.Value<string>(childTable.PrimaryId);

                    if (string.IsNullOrEmpty(newParentId))
                    {
                        await SendDataverseAsync(HttpMethod.Delete,
                            childTable.EntitySetName + "(" + childId + ")/" + relationship.ChildNavProperty + "/$ref",
                            null, null).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendDataverseAsync(new HttpMethod("PATCH"), childTable.EntitySetName + "(" + childId + ")",
                            new JObject
                            {
                                [relationship.ChildNavProperty + "@odata.bind"] =
                                    "/" + table.EntitySetName + "(" + newParentId + ")"
                            }, null).ConfigureAwait(false);
                    }

                    reparented++;
                }
            }
            else
            {
                var definition = new CatalogDefinition
                {
                    Root = table,
                    Repeating = true,
                    SelfReference = true,
                    Hierarchical = relationship.IsHierarchical
                };

                definition.Levels.Add(new CatalogLevel { Table = table, Relationship = relationship });

                var subtree = await WalkLevelsAsync(definition, new TreeOptions
                {
                    RootId = id,
                    Depth = MaxDepth,
                    MaxNodes = maxNodes,
                    IncludeChildCounts = false
                }).ConfigureAwait(false);

                if (subtree.Truncated)
                {
                    var tooLarge = new CatalogException("subtree_too_large",
                        "The subtree below '" + node.Label + "' exceeds the node budget, so it was not deleted. Nothing was changed.");
                    tooLarge.Table = table.LogicalName;
                    tooLarge.ProvidedValue = id;
                    tooLarge.Hint = "Raise maxNodes, or delete lower branches first.";
                    throw tooLarge;
                }

                var descendants = subtree.All
                    .Where(n => n.Depth > 0)
                    .OrderByDescending(n => n.Depth)
                    .ToList();

                EnsureDeleteBudget(descendants.Count + 1);

                foreach (var descendant in descendants)
                {
                    await SendDataverseAsync(HttpMethod.Delete,
                        table.EntitySetName + "(" + descendant.Id + ")", null, null).ConfigureAwait(false);

                    descendantsDeleted++;
                }
            }
        }

        await SendDataverseAsync(HttpMethod.Delete, table.EntitySetName + "(" + id + ")", null, null)
            .ConfigureAwait(false);

        return new JObject
        {
            ["deleted"] = true,
            ["node"] = NodeToJson(node, false),
            ["onChildren"] = onChildren,
            ["childrenReparented"] = reparented,
            ["descendantsDeleted"] = descendantsDeleted
        };
    }

    private static JArray BuildChildModeValues()
    {
        return new JArray(
            new JObject { ["value"] = "refuse", ["description"] = "Default. Refuse to delete a node that has children." },
            new JObject { ["value"] = "reparent", ["description"] = "Attach the children to the node's own parent, then delete the node." },
            new JObject { ["value"] = "cascade", ["description"] = "Delete the node and every descendant below it." });
    }

    /// <summary>Refuses a destructive run that could not finish, rather than deleting part of a subtree.</summary>
    private void EnsureDeleteBudget(int required)
    {
        var remaining = MaxDataverseRequests - _dataverseRequests;

        if (required > remaining)
        {
            var budget = new CatalogException("delete_budget_exceeded",
                "This delete needs " + required + " Dataverse calls but only " + Math.Max(0, remaining)
                + " remain in this invocation. Nothing was changed.");
            budget.Hint = "Delete lower branches first, so each call stays within the budget.";
            throw budget;
        }
    }

    private async Task<RelationshipInfo> ResolveParentLinkAsync(TableInfo parent, TableInfo child, string relationshipName)
    {
        var candidates = parent.Children
            .Where(r => string.Equals(r.ChildTable, child.LogicalName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(relationshipName))
        {
            candidates = candidates
                .Where(r => string.Equals(r.SchemaName, relationshipName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (candidates.Count > 1)
        {
            var preferred = candidates.FirstOrDefault(r => r.IsHierarchical);

            if (preferred != null)
            {
                return preferred;
            }

            var ambiguous = new CatalogException("ambiguous_relationship",
                "'" + parent.LogicalName + "' has " + candidates.Count + " relationships to '" + child.LogicalName + "'.");
            ambiguous.Table = parent.LogicalName;
            ambiguous.ProvidedValue = relationshipName;
            ambiguous.ValidValues = new JArray(candidates.Take(MaxValidValues).Select(r => new JObject
            {
                ["value"] = r.SchemaName,
                ["description"] = "Lookup " + r.LookupAttribute + " on " + r.ChildTable
            }));
            ambiguous.Hint = "Pass relationship=<schemaName>.";
            throw ambiguous;
        }

        var all = await GetCandidateRelationshipsAsync(parent, true).ConfigureAwait(false);
        var missing = new CatalogException("unknown_relationship",
            "No relationship links '" + parent.LogicalName + "' to '" + child.LogicalName + "'"
            + (string.IsNullOrWhiteSpace(relationshipName) ? "." : " with schema name '" + relationshipName + "'."));
        missing.Table = parent.LogicalName;
        missing.ProvidedValue = relationshipName;
        missing.ValidValues = new JArray(all.Take(MaxValidValues).Select(r => new JObject
        {
            ["value"] = r.SchemaName,
            ["description"] = "Children in " + r.ChildTable + " via " + r.LookupAttribute
        }));
        missing.Hint = "Call describe_catalog to see the real relationships.";
        throw missing;
    }

    // -------------------------------------------------------- REST handlers

    private async Task<HttpResponseMessage> HandleDescribeAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpDescribeAsync(ArgsFromQuery()).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleGetTreeAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpGetTreeAsync(ArgsFromQuery()).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleGetChildrenAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpGetChildrenAsync(ArgsFromQuery()).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleGetAncestorsAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpGetAncestorsAsync(ArgsFromQuery()).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleSearchAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpSearchAsync(ArgsFromQuery()).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleCreateNodeAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpCreateNodeAsync(await ReadBodyAsync().ConfigureAwait(false)).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleMoveNodeAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpMoveNodeAsync(await ReadBodyAsync().ConfigureAwait(false)).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleUpdateNodeAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpUpdateNodeAsync(await ReadBodyAsync().ConfigureAwait(false)).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleDeleteNodeAsync()
    {
        return JsonResponse(HttpStatusCode.OK, await OpDeleteNodeAsync(await ReadBodyAsync().ConfigureAwait(false)).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> HandleGetTablesAsync()
    {
        var query = ParseQuery();
        var search = query.ContainsKey("search") ? query["search"] : null;
        var customOnly = !query.ContainsKey("customOnly")
            || !string.Equals(query["customOnly"], "false", StringComparison.OrdinalIgnoreCase);

        var tables = await GetAllTablesAsync().ConfigureAwait(false);
        var results = new JArray();

        foreach (var table in tables.Values
            .Where(t => !customOnly || t.IsCustom)
            .Where(t => string.IsNullOrWhiteSpace(search)
                || t.LogicalName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                || (t.DisplayName ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new JObject
            {
                ["logicalName"] = table.LogicalName,
                ["entitySetName"] = table.EntitySetName,
                ["displayName"] = table.DisplayName,
                ["primaryIdAttribute"] = table.PrimaryId,
                ["primaryNameAttribute"] = table.PrimaryName
            });
        }

        return JsonResponse(HttpStatusCode.OK, results);
    }

    private async Task<HttpResponseMessage> HandleGetChildRelationshipsAsync()
    {
        var query = ParseQuery();
        var tableName = query.ContainsKey("table") ? query["table"] : null;
        var customOnly = !query.ContainsKey("customOnly")
            || !string.Equals(query["customOnly"], "false", StringComparison.OrdinalIgnoreCase);

        var table = await GetTableAsync(tableName).ConfigureAwait(false);
        var candidates = await GetCandidateRelationshipsAsync(table, customOnly).ConfigureAwait(false);
        var values = new JArray();

        foreach (var candidate in candidates)
        {
            values.Add(await RelationshipToJsonAsync(candidate).ConfigureAwait(false));
        }

        return JsonResponse(HttpStatusCode.OK, new JObject
        {
            ["table"] = table.LogicalName,
            ["totalCount"] = candidates.Count,
            ["value"] = values
        });
    }

    // ----------------------------------------------------------------- MCP

    private async Task<HttpResponseMessage> HandleMcpAsync()
    {
        var body = await ReadBodyAsync().ConfigureAwait(false);
        var method = body.Value<string>("method") ?? string.Empty;
        var id = body["id"] ?? JValue.CreateNull();

        // Notifications still get a valid JSON-RPC envelope; Copilot Studio requires it.
        if (method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
        {
            return JsonRpcResult(id, new JObject());
        }

        switch (method)
        {
            case "initialize":
                var requested = body["params"] != null ? body["params"].Value<string>("protocolVersion") : null;

                return JsonRpcResult(id, new JObject
                {
                    ["protocolVersion"] = string.IsNullOrWhiteSpace(requested) ? ProtocolVersion : requested,
                    ["capabilities"] = new JObject
                    {
                        ["tools"] = new JObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = "dataverse-catalog-tree",
                        ["version"] = "1.0.0"
                    }
                });

            case "ping":
                return JsonRpcResult(id, new JObject());

            case "tools/list":
                return JsonRpcResult(id, new JObject { ["tools"] = BuildToolList() });

            case "resources/list":
                return JsonRpcResult(id, new JObject { ["resources"] = new JArray() });

            case "prompts/list":
                return JsonRpcResult(id, new JObject { ["prompts"] = new JArray() });

            case "tools/call":
                return await HandleToolCallAsync(id, body["params"] as JObject).ConfigureAwait(false);

            default:
                return JsonRpcError(id, -32601, "Method '" + method + "' is not supported.");
        }
    }

    private async Task<HttpResponseMessage> HandleToolCallAsync(JToken id, JObject parameters)
    {
        var toolName = parameters != null ? parameters.Value<string>("name") : null;
        var arguments = parameters != null ? parameters["arguments"] as JObject : null;

        if (arguments == null)
        {
            arguments = new JObject();
        }

        await LogToAppInsightsAsync("McpToolCall", new Dictionary<string, string>
        {
            ["tool"] = toolName ?? string.Empty
        }).ConfigureAwait(false);

        try
        {
            JObject payload;

            switch (toolName)
            {
                case "describe_catalog":
                    payload = await OpDescribeAsync(arguments).ConfigureAwait(false);
                    break;
                case "get_catalog_tree":
                    payload = await OpGetTreeAsync(arguments).ConfigureAwait(false);
                    break;
                case "get_node_children":
                    payload = await OpGetChildrenAsync(arguments).ConfigureAwait(false);
                    break;
                case "get_node_ancestors":
                    payload = await OpGetAncestorsAsync(arguments).ConfigureAwait(false);
                    break;
                case "search_catalog":
                    payload = await OpSearchAsync(arguments).ConfigureAwait(false);
                    break;
                case "create_catalog_node":
                    payload = await OpCreateNodeAsync(arguments).ConfigureAwait(false);
                    break;
                case "move_catalog_node":
                    payload = await OpMoveNodeAsync(arguments).ConfigureAwait(false);
                    break;
                case "update_catalog_node":
                    payload = await OpUpdateNodeAsync(arguments).ConfigureAwait(false);
                    break;
                case "delete_catalog_node":
                    payload = await OpDeleteNodeAsync(arguments).ConfigureAwait(false);
                    break;
                default:
                    var unknown = new CatalogException("unknown_tool",
                        "'" + (toolName ?? "(none)") + "' is not a tool on this server.");
                    unknown.ValidValues = new JArray(BuildToolList()
                        .OfType<JObject>()
                        .Select(t => new JObject { ["value"] = t["name"] }));
                    unknown.Hint = "Call tools/list to see the available tools.";
                    return JsonRpcResult(id, ToolResult(unknown.ToJson(), true));
            }

            return JsonRpcResult(id, ToolResult(payload, false));
        }
        catch (CatalogException cx)
        {
            // Returned as a tool result rather than a protocol error so the model can read
            // validValues and retry with a real name instead of inventing one.
            return JsonRpcResult(id, ToolResult(cx.ToJson(), true));
        }
        catch (Exception ex)
        {
            await LogToAppInsightsAsync("McpToolError", new Dictionary<string, string>
            {
                ["tool"] = toolName ?? string.Empty,
                ["message"] = ex.Message,
                ["stack"] = ex.StackTrace ?? string.Empty
            }).ConfigureAwait(false);

            return JsonRpcResult(id, ToolResult(new JObject
            {
                ["error"] = "unhandled_exception",
                ["message"] = ex.Message
            }, true));
        }
    }

    private static JObject ToolResult(JObject payload, bool isError)
    {
        return new JObject
        {
            ["content"] = new JArray(new JObject
            {
                ["type"] = "text",
                ["text"] = payload.ToString(Newtonsoft.Json.Formatting.None)
            }),
            ["structuredContent"] = payload,
            ["isError"] = isError
        };
    }

    private HttpResponseMessage JsonRpcResult(JToken id, JObject result)
    {
        return JsonResponse(HttpStatusCode.OK, new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["result"] = result
        });
    }

    private HttpResponseMessage JsonRpcError(JToken id, int code, string message)
    {
        return JsonResponse(HttpStatusCode.OK, new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        });
    }

    private static JObject StringProperty(string description)
    {
        return new JObject { ["type"] = "string", ["description"] = description };
    }

    private static JObject IntegerProperty(string description, int defaultValue)
    {
        return new JObject { ["type"] = "integer", ["description"] = description, ["default"] = defaultValue };
    }

    private static JArray BuildToolList()
    {
        const string idRule = "Use only ids returned by a previous tool call. Never construct or guess a GUID, table name or relationship name. "
            + "If a call fails, read validValues in the response and retry with one of those values.";

        var tools = new JArray();

        tools.Add(new JObject
        {
            ["name"] = "describe_catalog",
            ["description"] = "Read the real shape of a catalog hierarchy from Dataverse metadata: whether the table points at itself, "
                + "which relationship forms each level, and the exact levels path to pass to the other tools. "
                + "Call this first whenever you are unsure how a catalog is structured. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Root table logical name, for example tst_category."),
                    ["levels"] = StringProperty("Optional relationship path to validate, separated by > .")
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "get_catalog_tree",
            ["description"] = "Return a whole catalog subtree in one call, already assembled. Handles recursion, cycle detection and limits server-side. "
                + "Read stats.truncated and each node's childCount before describing a branch: when hasMoreChildren is true there are children that are not in the response, "
                + "so report the count rather than listing or inventing them. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Root table logical name."),
                    ["id"] = StringProperty("Root record GUID. Omit to start from every top-level record."),
                    ["levels"] = StringProperty("Ordered relationship path separated by > . Omit for a self-referencing table."),
                    ["depth"] = IntegerProperty("Levels below the root to expand, 1 to 10.", DefaultDepth),
                    ["maxNodes"] = IntegerProperty("Node budget for the whole response, 1 to 5000.", DefaultMaxNodes),
                    ["fields"] = StringProperty("Extra comma-separated columns to return on every node."),
                    ["filter"] = StringProperty("OData filter applied to child records at every level."),
                    ["format"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "tree for nested children, flat for one row per node, outline for indented text, all for every shape.",
                        ["enum"] = new JArray("tree", "flat", "outline", "all"),
                        ["default"] = "tree"
                    },
                    ["includeChildCounts"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Count children of the deepest returned nodes so unexpanded branches can be reported accurately.",
                        ["default"] = true
                    },
                    ["rootTop"] = IntegerProperty("Maximum root records to return when no id is supplied, 1 to 500.", 50)
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "get_node_children",
            ["description"] = "Return one level of children for a node. Use this to drill into a branch that get_catalog_tree reported as having more children. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table", "id"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Table of the parent record."),
                    ["id"] = StringProperty("Parent record GUID, taken from an earlier result."),
                    ["relationship"] = StringProperty("Child relationship schema name. Omit for a self-referencing table."),
                    ["fields"] = StringProperty("Extra comma-separated columns to return."),
                    ["filter"] = StringProperty("OData filter applied to the child records."),
                    ["top"] = IntegerProperty("Maximum children to return, 1 to 1000.", 100)
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "get_node_ancestors",
            ["description"] = "Return the breadcrumb from a node up to its root. Use this to state where a record sits instead of reconstructing the path yourself. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table", "id"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Table of the record."),
                    ["id"] = StringProperty("Record GUID, taken from an earlier result."),
                    ["rootTable"] = StringProperty("Root table of the catalog when levels are heterogeneous."),
                    ["levels"] = StringProperty("Ordered relationship path from the root table, separated by > .")
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "search_catalog",
            ["description"] = "Find nodes by name across every level and return each match with its full path and id. "
                + "Always use this to turn a name the user typed into a real record before traversing or writing. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table", "search"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Root table logical name."),
                    ["search"] = StringProperty("Text to match against the primary name column of each level."),
                    ["levels"] = StringProperty("Ordered relationship path separated by > . Omit for a self-referencing table."),
                    ["depth"] = IntegerProperty("How many levels below the root to search, 1 to 10.", 5),
                    ["top"] = IntegerProperty("Maximum matches to return, 1 to 100.", 20)
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "create_catalog_node",
            ["description"] = "Create a record and attach it to a parent node, resolving the correct lookup from relationship metadata. "
                + "Confirm the parent with search_catalog or get_catalog_tree first. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table", "name"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Table to create the record in."),
                    ["name"] = StringProperty("Value for the primary name column."),
                    ["parentTable"] = StringProperty("Table of the parent record. Defaults to the same table."),
                    ["parentId"] = StringProperty("Parent record GUID. Omit to create a root node."),
                    ["relationship"] = StringProperty("Relationship schema name linking parent to child."),
                    ["values"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Additional column values to set on the new record."
                    }
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "move_catalog_node",
            ["description"] = "Reparent a node. The move is rejected when the new parent sits below the node, which would create a cycle. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table", "id"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Table of the record being moved."),
                    ["id"] = StringProperty("GUID of the record being moved."),
                    ["newParentId"] = StringProperty("GUID of the new parent. Omit to detach the node and make it a root."),
                    ["parentTable"] = StringProperty("Table of the new parent. Defaults to the same table."),
                    ["relationship"] = StringProperty("Relationship schema name linking parent to child.")
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "update_catalog_node",
            ["description"] = "Rename a node or set other columns on it. Parent lookups are rejected here — use move_catalog_node for those, "
                + "because that path carries the cycle guard. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table", "id"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Table of the record."),
                    ["id"] = StringProperty("Record GUID, taken from an earlier result."),
                    ["name"] = StringProperty("New value for the primary name column."),
                    ["values"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Other column values to set."
                    }
                }
            }
        });

        tools.Add(new JObject
        {
            ["name"] = "delete_catalog_node",
            ["description"] = "Delete a node. Refuses by default when the node has children and reports how many, so nothing is destroyed on a guess. "
                + "Confirm with the user before re-sending with onChildren set to reparent or cascade. A run that cannot finish within the call "
                + "budget is refused outright rather than half-deleting a subtree. " + idRule,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("table", "id"),
                ["properties"] = new JObject
                {
                    ["table"] = StringProperty("Table of the record."),
                    ["id"] = StringProperty("Record GUID, taken from an earlier result."),
                    ["onChildren"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "refuse stops when children exist, reparent attaches them to the node's parent, cascade deletes the whole subtree. reparent and cascade need a self-referencing catalog.",
                        ["enum"] = new JArray("refuse", "reparent", "cascade"),
                        ["default"] = "refuse"
                    },
                    ["relationship"] = StringProperty("Child relationship schema name. Omit for a self-referencing table."),
                    ["maxNodes"] = IntegerProperty("Ceiling on descendants a cascade may delete.", DefaultMaxNodes)
                }
            }
        });

        return tools;
    }

    // ----------------------------------------------------------- telemetry

    private async Task LogToAppInsightsAsync(string eventName, IDictionary<string, string> properties)
    {
        if (!APP_INSIGHTS_ENABLED
            || string.IsNullOrEmpty(APP_INSIGHTS_KEY)
            || APP_INSIGHTS_KEY.Contains("INSERT_YOUR"))
        {
            return;
        }

        try
        {
            var telemetry = new JObject
            {
                ["name"] = "Microsoft.ApplicationInsights.Event",
                ["time"] = DateTime.UtcNow.ToString("O"),
                ["iKey"] = APP_INSIGHTS_KEY,
                ["data"] = new JObject
                {
                    ["baseType"] = "EventData",
                    ["baseData"] = new JObject
                    {
                        ["ver"] = 2,
                        ["name"] = eventName,
                        ["properties"] = JObject.FromObject(properties ?? new Dictionary<string, string>())
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, APP_INSIGHTS_ENDPOINT)
            {
                Content = CreateJsonContent(telemetry.ToString(Newtonsoft.Json.Formatting.None))
            };

            await this.Context.SendAsync(request, this.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Telemetry must never fail the operation.
        }
    }
}
