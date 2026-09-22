using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private const string ServerName = "copilot-studio-agent-runtime";
    private const string ServerVersion = "1.0.0";
    private const string ProtocolVersion = "2025-11-25";

    // The Agentic Runtime "3p" (third-party) Direct-to-Engine route. This is how
    // CLI-authored agentic-loop agents — the GitHub Copilot harness — are served.
    // api-version is pinned to 1: that is the contract this path accepts, and the
    // published-bot preview version does not apply here.
    private const string RuntimeApiVersion = "1";
    private const string RuntimePathPrefix =
        "/copilotstudio/agenticruntime/3p/dataverse-backed/authenticated/bots";

    // The runtime answers with server-sent events. Power Platform cannot surface a
    // stream to a flow, so every turn is buffered here and returned as one JSON body.
    private const string SseMediaType = "text/event-stream";

    private const string ConversationIdHeader = "x-ms-conversationid";

    // Optional hardcoded telemetry
    private const bool APP_INSIGHTS_ENABLED = false;
    private const string APP_INSIGHTS_KEY = "[INSERT_YOUR_APP_INSIGHTS_INSTRUMENTATION_KEY]";
    private const string APP_INSIGHTS_ENDPOINT = "https://dc.applicationinsights.azure.com/v2/track";

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        try
        {
            switch (this.Context.OperationId)
            {
                case "InvokeMCP":
                    return await HandleMcpRequestAsync().ConfigureAwait(false);

                case "AskAgent":
                    return await HandleAskAgentAsync().ConfigureAwait(false);

                case "StartConversation":
                    return await HandleStartConversationAsync().ConfigureAwait(false);

                case "SendMessage":
                    return await HandleSendMessageAsync().ConfigureAwait(false);

                default:
                    return await this.Context
                        .SendAsync(this.Context.Request, this.CancellationToken)
                        .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await LogExceptionToAppInsightsAsync(ex, "ExecuteAsync").ConfigureAwait(false);
            return ErrorResponse(HttpStatusCode.InternalServerError, ex.Message, null);
        }
    }

    // ---------------------------------------------------------------------
    // REST operations
    // ---------------------------------------------------------------------

    private async Task<HttpResponseMessage> HandleStartConversationAsync()
    {
        var schemaName = SchemaNameFromRequest();
        if (string.IsNullOrEmpty(schemaName))
        {
            return ErrorResponse(HttpStatusCode.BadRequest, "The agent schema name is missing from the request path.", null);
        }

        var body = await ReadRequestBodyAsync().ConfigureAwait(false);
        var emitStart = body["emitStartConversationEvent"] == null
            || ToBoolean(body["emitStartConversationEvent"], true);

        var turn = await RunTurnAsync(schemaName, null, StartBody(emitStart)).ConfigureAwait(false);
        return TurnResponse(turn);
    }

    private async Task<HttpResponseMessage> HandleSendMessageAsync()
    {
        var schemaName = SchemaNameFromRequest();
        var conversationId = ConversationIdFromRequest();

        if (string.IsNullOrEmpty(schemaName))
        {
            return ErrorResponse(HttpStatusCode.BadRequest, "The agent schema name is missing from the request path.", null);
        }

        if (string.IsNullOrEmpty(conversationId))
        {
            return ErrorResponse(HttpStatusCode.BadRequest, "The conversation ID is missing from the request path.", null);
        }

        var body = await ReadRequestBodyAsync().ConfigureAwait(false);
        var activity = BuildOutboundActivity(body, conversationId);
        if (activity == null)
        {
            return ErrorResponse(HttpStatusCode.BadRequest, "Provide either a message in 'text' or a raw activity in 'activity'.", null);
        }

        var turn = await RunTurnAsync(schemaName, conversationId, new JObject { ["activity"] = activity })
            .ConfigureAwait(false);
        return TurnResponse(turn);
    }

    /// <summary>
    /// One-shot ask: open a conversation, send the message, and return the reply.
    /// The declared path ends in /ask so it does not collide with the real
    /// /conversations routes; the outbound URL is rebuilt here either way.
    /// </summary>
    private async Task<HttpResponseMessage> HandleAskAgentAsync()
    {
        var schemaName = SchemaNameFromRequest();
        if (string.IsNullOrEmpty(schemaName))
        {
            return ErrorResponse(HttpStatusCode.BadRequest, "The agent schema name is missing from the request path.", null);
        }

        var body = await ReadRequestBodyAsync().ConfigureAwait(false);
        var text = body.Value<string>("text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return ErrorResponse(HttpStatusCode.BadRequest, "A message is required. Set 'text' to the question you want to ask the agent.", null);
        }

        var turn = await AskAsync(schemaName, text, body.Value<string>("locale")).ConfigureAwait(false);
        return TurnResponse(turn);
    }

    /// <summary>
    /// Opens a conversation and sends one message through it. Returns the send
    /// turn on success, or whichever step failed.
    /// </summary>
    private async Task<Tuple<HttpStatusCode, JObject>> AskAsync(string schemaName, string text, string locale)
    {
        var opened = await RunTurnAsync(schemaName, null, StartBody(true)).ConfigureAwait(false);
        if (opened.Item1 != HttpStatusCode.OK)
        {
            return opened;
        }

        var conversationId = opened.Item2.Value<string>("conversationId");
        if (string.IsNullOrEmpty(conversationId))
        {
            return Failure(
                HttpStatusCode.BadGateway,
                "The runtime opened a conversation but did not return a conversation ID.",
                opened.Item2.ToString(Newtonsoft.Json.Formatting.None));
        }

        var activity = new JObject
        {
            ["type"] = "message",
            ["text"] = text,
            ["conversation"] = new JObject { ["id"] = conversationId }
        };

        if (!string.IsNullOrWhiteSpace(locale))
        {
            activity["locale"] = locale;
        }

        var answered = await RunTurnAsync(schemaName, conversationId, new JObject { ["activity"] = activity })
            .ConfigureAwait(false);

        // The send turn carries the answer, but the greeting activities from the
        // open turn are still useful context, so keep them alongside it.
        if (answered.Item1 == HttpStatusCode.OK)
        {
            var greeting = opened.Item2["activities"] as JArray;
            if (greeting != null && greeting.Count > 0)
            {
                answered.Item2["greetingActivities"] = greeting;
            }

            if (string.IsNullOrEmpty(answered.Item2.Value<string>("conversationId")))
            {
                answered.Item2["conversationId"] = conversationId;
            }
        }

        return answered;
    }

    // ---------------------------------------------------------------------
    // Runtime call
    // ---------------------------------------------------------------------

    /// <summary>
    /// Posts one turn to the Agentic Runtime and folds the server-sent event
    /// stream into a single result object.
    /// </summary>
    private async Task<Tuple<HttpStatusCode, JObject>> RunTurnAsync(
        string schemaName, string conversationId, JObject body)
    {
        var url = ConversationsUrl(schemaName, conversationId);

        var outbound = new HttpRequestMessage(HttpMethod.Post, url);
        outbound.Headers.TryAddWithoutValidation("Accept", SseMediaType);

        if (this.Context.Request.Headers.Contains("Authorization"))
        {
            outbound.Headers.TryAddWithoutValidation(
                "Authorization",
                this.Context.Request.Headers.GetValues("Authorization").FirstOrDefault());
        }

        outbound.Content = new StringContent(
            (body ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None),
            Encoding.UTF8,
            "application/json");

        var response = await this.Context.SendAsync(outbound, this.CancellationToken).ConfigureAwait(false);

        var payload = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            await LogToAppInsightsAsync("RuntimeCallFailed", new Dictionary<string, string>
            {
                { "status", ((int)response.StatusCode).ToString() },
                { "schemaName", schemaName ?? string.Empty }
            }).ConfigureAwait(false);

            return Failure(response.StatusCode, ExplainStatus(response.StatusCode), payload);
        }

        string headerConversationId = null;
        IEnumerable<string> headerValues;
        if (response.Headers.TryGetValues(ConversationIdHeader, out headerValues))
        {
            headerConversationId = headerValues.FirstOrDefault();
        }

        var result = Consolidate(payload, headerConversationId ?? conversationId);
        return Tuple.Create(HttpStatusCode.OK, result);
    }

    private static JObject StartBody(bool emitStartConversationEvent)
    {
        return new JObject { ["emitStartConversationEvent"] = emitStartConversationEvent };
    }

    /// <summary>
    /// Rebuilds the canonical runtime URL. The environment host comes from the
    /// incoming request, which the connection's dynamic host policy has already
    /// resolved, so this works for every cloud without hardcoding a suffix.
    /// </summary>
    private string ConversationsUrl(string schemaName, string conversationId)
    {
        var uri = this.Context.Request.RequestUri;

        var url = new StringBuilder()
            .Append(uri.Scheme)
            .Append("://")
            .Append(uri.Authority)
            .Append(RuntimePathPrefix)
            .Append('/')
            .Append(Uri.EscapeDataString(schemaName))
            .Append("/conversations");

        if (!string.IsNullOrEmpty(conversationId))
        {
            url.Append('/').Append(Uri.EscapeDataString(conversationId));
        }

        url.Append("?api-version=").Append(RuntimeApiVersion);
        return url.ToString();
    }

    private string SchemaNameFromRequest()
    {
        return SegmentAfter(this.Context.Request.RequestUri, "bots");
    }

    private string ConversationIdFromRequest()
    {
        return SegmentAfter(this.Context.Request.RequestUri, "conversations");
    }

    private static string SegmentAfter(Uri uri, string marker)
    {
        if (uri == null) return null;

        var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], marker, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[i + 1]);
            }
        }

        return null;
    }

    /// <summary>
    /// Turns the caller's body into the activity to send. A raw activity wins over
    /// plain text, and the conversation binding is always forced to the target
    /// conversation so a caller cannot address a different one.
    /// </summary>
    private static JObject BuildOutboundActivity(JObject body, string conversationId)
    {
        var raw = body["activity"] as JObject;
        if (raw != null && raw.HasValues)
        {
            var activity = (JObject)raw.DeepClone();
            if (string.IsNullOrWhiteSpace(activity.Value<string>("type")))
            {
                activity["type"] = "message";
            }

            activity["conversation"] = new JObject { ["id"] = conversationId };
            return activity;
        }

        var text = body.Value<string>("text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var message = new JObject
        {
            ["type"] = "message",
            ["text"] = text,
            ["conversation"] = new JObject { ["id"] = conversationId }
        };

        var locale = body.Value<string>("locale");
        if (!string.IsNullOrWhiteSpace(locale))
        {
            message["locale"] = locale;
        }

        return message;
    }

    // ---------------------------------------------------------------------
    // Server-sent event handling
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parses an SSE body into (event, data) pairs. Falls back to treating the
    /// whole payload as one frame when the runtime answers with plain JSON.
    /// </summary>
    private static List<KeyValuePair<string, string>> ParseSse(string payload)
    {
        var frames = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return frames;
        }

        var normalized = payload.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        string eventName = null;
        var data = new StringBuilder();
        var sawField = false;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (sawField)
                {
                    frames.Add(new KeyValuePair<string, string>(eventName ?? string.Empty, data.ToString()));
                    eventName = null;
                    data.Clear();
                    sawField = false;
                }

                continue;
            }

            // A leading colon marks a comment, which the runtime uses as a heartbeat.
            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            string field;
            string value;

            if (separator < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line.Substring(0, separator);
                value = line.Substring(separator + 1);
                if (value.Length > 0 && value[0] == ' ')
                {
                    value = value.Substring(1);
                }
            }

            if (string.Equals(field, "event", StringComparison.OrdinalIgnoreCase))
            {
                eventName = value;
                sawField = true;
            }
            else if (string.Equals(field, "data", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
                sawField = true;
            }
        }

        if (sawField)
        {
            frames.Add(new KeyValuePair<string, string>(eventName ?? string.Empty, data.ToString()));
        }

        // Not an SSE body after all — hand the raw payload through as a single frame.
        if (frames.Count == 0)
        {
            frames.Add(new KeyValuePair<string, string>(string.Empty, payload));
        }

        return frames;
    }

    /// <summary>
    /// Folds the streamed activities into one answer.
    /// </summary>
    private static JObject Consolidate(string payload, string conversationId)
    {
        var activities = new JArray();
        var messageTexts = new List<string>();
        var typingTexts = new List<string>();

        string finalStreamText = null;
        JObject lastBotMessage = null;
        var isComplete = false;

        foreach (var frame in ParseSse(payload))
        {
            var eventName = frame.Key;
            var data = frame.Value;

            if (string.Equals(eventName, "end", StringComparison.OrdinalIgnoreCase))
            {
                isComplete = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            if (string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                isComplete = true;
                continue;
            }

            foreach (var activity in ExtractActivities(data))
            {
                activities.Add(activity);

                if (string.IsNullOrEmpty(conversationId))
                {
                    var scoped = activity["conversation"] as JObject;
                    if (scoped != null)
                    {
                        var scopedId = scoped.Value<string>("id");
                        if (!string.IsNullOrEmpty(scopedId))
                        {
                            conversationId = scopedId;
                        }
                    }
                }

                var type = activity.Value<string>("type") ?? string.Empty;
                var text = activity.Value<string>("text");
                var streamType = activity.Value<string>("streamType") ?? string.Empty;

                if (string.Equals(type, "event", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(activity.Value<string>("name"), "turn.complete", StringComparison.OrdinalIgnoreCase))
                {
                    isComplete = true;
                    continue;
                }

                if (IsFromUser(activity))
                {
                    continue;
                }

                if (string.Equals(streamType, "final", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(text))
                {
                    finalStreamText = text;
                    lastBotMessage = activity;
                    continue;
                }

                if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        messageTexts.Add(text);
                    }

                    lastBotMessage = activity;
                }
                else if (string.Equals(type, "typing", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(text))
                {
                    typingTexts.Add(text);
                }
            }
        }

        var answer = finalStreamText;

        if (string.IsNullOrEmpty(answer) && messageTexts.Count > 0)
        {
            answer = string.Join("\n\n", messageTexts);
        }

        if (string.IsNullOrEmpty(answer) && typingTexts.Count > 0)
        {
            answer = JoinStreamedText(typingTexts);
        }

        var result = new JObject
        {
            ["conversationId"] = conversationId ?? string.Empty,
            ["text"] = answer ?? string.Empty,
            ["isComplete"] = isComplete,
            ["activityCount"] = activities.Count,
            ["attachments"] = lastBotMessage != null && lastBotMessage["attachments"] is JArray
                ? (JArray)lastBotMessage["attachments"].DeepClone()
                : new JArray(),
            ["suggestedActions"] = ExtractSuggestedActions(lastBotMessage),
            ["citations"] = ExtractCitations(lastBotMessage),
            ["activities"] = activities
        };

        return result;
    }

    private static IEnumerable<JObject> ExtractActivities(string data)
    {
        JToken parsed;
        try
        {
            parsed = JToken.Parse(data);
        }
        catch
        {
            yield break;
        }

        var array = parsed as JArray;
        if (array != null)
        {
            foreach (var item in array)
            {
                var activity = item as JObject;
                if (activity != null)
                {
                    yield return activity;
                }
            }

            yield break;
        }

        var single = parsed as JObject;
        if (single == null)
        {
            yield break;
        }

        // Some responses wrap the batch as { "activities": [ ... ] }.
        var wrapped = single["activities"] as JArray;
        if (wrapped != null)
        {
            foreach (var item in wrapped)
            {
                var activity = item as JObject;
                if (activity != null)
                {
                    yield return activity;
                }
            }

            yield break;
        }

        yield return single;
    }

    /// <summary>
    /// The runtime streams either cumulative snapshots or delta fragments,
    /// depending on the agent. Detect which and join accordingly, so a cumulative
    /// stream is not repeated and a delta stream is not truncated.
    /// </summary>
    private static string JoinStreamedText(List<string> chunks)
    {
        if (chunks.Count == 1)
        {
            return chunks[0];
        }

        var cumulative = true;
        for (var i = 1; i < chunks.Count; i++)
        {
            if (!chunks[i].StartsWith(chunks[i - 1], StringComparison.Ordinal))
            {
                cumulative = false;
                break;
            }
        }

        return cumulative ? chunks[chunks.Count - 1] : string.Concat(chunks);
    }

    private static bool IsFromUser(JObject activity)
    {
        var from = activity["from"] as JObject;
        if (from == null)
        {
            return false;
        }

        return string.Equals(from.Value<string>("role"), "user", StringComparison.OrdinalIgnoreCase)
            || string.Equals(from.Value<string>("id"), "user", StringComparison.OrdinalIgnoreCase);
    }

    private static JArray ExtractSuggestedActions(JObject activity)
    {
        if (activity == null)
        {
            return new JArray();
        }

        var suggested = activity["suggestedActions"] as JObject;
        var actions = suggested == null ? null : suggested["actions"] as JArray;
        return actions == null ? new JArray() : (JArray)actions.DeepClone();
    }

    private static JArray ExtractCitations(JObject activity)
    {
        var citations = new JArray();
        if (activity == null)
        {
            return citations;
        }

        var entities = activity["entities"] as JArray;
        if (entities == null)
        {
            return citations;
        }

        foreach (var entity in entities)
        {
            var entityObject = entity as JObject;
            if (entityObject == null)
            {
                continue;
            }

            var entityCitations = entityObject["citation"] as JArray;
            if (entityCitations == null)
            {
                continue;
            }

            foreach (var citation in entityCitations)
            {
                citations.Add(citation.DeepClone());
            }
        }

        return citations;
    }

    // ---------------------------------------------------------------------
    // MCP
    // ---------------------------------------------------------------------

    private async Task<HttpResponseMessage> HandleMcpRequestAsync()
    {
        var body = await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return CreateJsonRpcErrorResponse(null, -32700, "Parse error: empty request body");
        }

        JObject request;
        try
        {
            request = JObject.Parse(body);
        }
        catch
        {
            return CreateJsonRpcErrorResponse(null, -32700, "Parse error: invalid JSON");
        }

        var method = request.Value<string>("method");
        var requestId = request["id"];

        await LogToAppInsightsAsync("MCP_Request", new Dictionary<string, string>
        {
            { "method", method ?? string.Empty },
            { "requestId", requestId?.ToString() ?? "null" }
        }).ConfigureAwait(false);

        // JSON-RPC notification: no response body required
        if (requestId == null || requestId.Type == JTokenType.Null)
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        switch (method)
        {
            case "initialize":
                return CreateJsonRpcSuccessResponse(requestId, new JObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JObject
                    {
                        ["tools"] = new JObject { ["listChanged"] = false },
                        ["resources"] = new JObject { ["listChanged"] = false },
                        ["prompts"] = new JObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion
                    }
                });

            case "ping":
                return CreateJsonRpcSuccessResponse(requestId, new JObject());

            case "tools/list":
                return HandleToolsList(requestId);

            case "tools/call":
                return await HandleToolsCallAsync(request, requestId).ConfigureAwait(false);

            case "resources/list":
                return CreateJsonRpcSuccessResponse(requestId, new JObject { ["resources"] = new JArray() });

            case "prompts/list":
                return CreateJsonRpcSuccessResponse(requestId, new JObject { ["prompts"] = new JArray() });

            default:
                return CreateJsonRpcErrorResponse(requestId, -32601, $"Method not found: {method}");
        }
    }

    private HttpResponseMessage HandleToolsList(JToken requestId)
    {
        var tools = new JArray
        {
            McpTool(
                "ask_agent",
                "Ask a published Copilot Studio agent a single question and get the completed answer. Opens a conversation, sends the message, and waits for the turn to finish. Use this when you do not need to keep history between calls.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["schemaName"] = Prop("Schema name of the published agent, for example cr1a2_myAgent."),
                        ["text"] = Prop("The question or instruction to send to the agent."),
                        ["locale"] = Prop("Optional BCP 47 locale for the turn, for example en-US.")
                    },
                    ["required"] = new JArray { "schemaName", "text" }
                }),

            McpTool(
                "start_conversation",
                "Open a conversation with a published Copilot Studio agent and return the conversation ID plus any greeting the agent sends. Use send_message with that ID to continue the conversation.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["schemaName"] = Prop("Schema name of the published agent, for example cr1a2_myAgent."),
                        ["emitStartConversationEvent"] = Prop(
                            "Whether to raise the conversation start event so the agent greets the user. Defaults to true.",
                            "boolean")
                    },
                    ["required"] = new JArray { "schemaName" }
                }),

            McpTool(
                "send_message",
                "Send a message to an existing conversation with a published Copilot Studio agent and return the completed reply. Requires a conversation ID from start_conversation.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["schemaName"] = Prop("Schema name of the published agent, for example cr1a2_myAgent."),
                        ["conversationId"] = Prop("Conversation ID returned by start_conversation."),
                        ["text"] = Prop("The message to send to the agent."),
                        ["locale"] = Prop("Optional BCP 47 locale for the turn, for example en-US.")
                    },
                    ["required"] = new JArray { "schemaName", "conversationId", "text" }
                })
        };

        return CreateJsonRpcSuccessResponse(requestId, new JObject { ["tools"] = tools });
    }

    private async Task<HttpResponseMessage> HandleToolsCallAsync(JObject request, JToken requestId)
    {
        var paramsObj = request["params"] as JObject;
        if (paramsObj == null)
        {
            return CreateJsonRpcErrorResponse(requestId, -32602, "Invalid params");
        }

        var toolName = paramsObj.Value<string>("name");
        var args = paramsObj["arguments"] as JObject ?? new JObject();

        try
        {
            var schemaName = args.Value<string>("schemaName");
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                return ToolResult(requestId, toolName, ErrorPayload(
                    HttpStatusCode.BadRequest, "schemaName is required.", null), true);
            }

            Tuple<HttpStatusCode, JObject> turn;

            switch ((toolName ?? string.Empty).ToLowerInvariant())
            {
                case "ask_agent":
                    {
                        var text = args.Value<string>("text");
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            return ToolResult(requestId, toolName, ErrorPayload(
                                HttpStatusCode.BadRequest, "text is required.", null), true);
                        }

                        turn = await AskAsync(schemaName, text, args.Value<string>("locale")).ConfigureAwait(false);
                        break;
                    }

                case "start_conversation":
                    {
                        var emitStart = args["emitStartConversationEvent"] == null
                            || ToBoolean(args["emitStartConversationEvent"], true);

                        turn = await RunTurnAsync(schemaName, null, StartBody(emitStart)).ConfigureAwait(false);
                        break;
                    }

                case "send_message":
                    {
                        var conversationId = args.Value<string>("conversationId");
                        var text = args.Value<string>("text");

                        if (string.IsNullOrWhiteSpace(conversationId))
                        {
                            return ToolResult(requestId, toolName, ErrorPayload(
                                HttpStatusCode.BadRequest, "conversationId is required.", null), true);
                        }

                        if (string.IsNullOrWhiteSpace(text))
                        {
                            return ToolResult(requestId, toolName, ErrorPayload(
                                HttpStatusCode.BadRequest, "text is required.", null), true);
                        }

                        var activity = BuildOutboundActivity(
                            new JObject { ["text"] = text, ["locale"] = args.Value<string>("locale") },
                            conversationId);

                        turn = await RunTurnAsync(schemaName, conversationId, new JObject { ["activity"] = activity })
                            .ConfigureAwait(false);
                        break;
                    }

                default:
                    return CreateJsonRpcErrorResponse(requestId, -32601, $"Unknown tool: {toolName}");
            }

            return ToolResult(requestId, toolName, turn.Item2, turn.Item1 != HttpStatusCode.OK);
        }
        catch (Exception ex)
        {
            await LogExceptionToAppInsightsAsync(ex, toolName ?? "tools/call").ConfigureAwait(false);
            return ToolResult(requestId, toolName, ErrorPayload(
                HttpStatusCode.InternalServerError, ex.Message, null), true);
        }
    }

    private HttpResponseMessage ToolResult(JToken requestId, string toolName, JObject payload, bool isError)
    {
        return CreateJsonRpcSuccessResponse(requestId, new JObject
        {
            ["content"] = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToString(Newtonsoft.Json.Formatting.Indented)
                }
            },
            ["isError"] = isError
        });
    }

    private static JObject Prop(string description, string type = "string")
    {
        return new JObject { ["type"] = type, ["description"] = description };
    }

    private JObject McpTool(string name, string description, JObject inputSchema)
    {
        return new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema
        };
    }

    // ---------------------------------------------------------------------
    // Responses and helpers
    // ---------------------------------------------------------------------

    private async Task<JObject> ReadRequestBodyAsync()
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
        catch
        {
            return new JObject();
        }
    }

    private static bool ToBoolean(JToken token, bool fallback)
    {
        if (token == null)
        {
            return fallback;
        }

        if (token.Type == JTokenType.Boolean)
        {
            return token.Value<bool>();
        }

        bool parsed;
        return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
    }

    private HttpResponseMessage TurnResponse(Tuple<HttpStatusCode, JObject> turn)
    {
        return new HttpResponseMessage(turn.Item1)
        {
            Content = new StringContent(
                turn.Item2.ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static Tuple<HttpStatusCode, JObject> Failure(HttpStatusCode status, string message, string details)
    {
        return Tuple.Create(status, ErrorPayload(status, message, details));
    }

    private static JObject ErrorPayload(HttpStatusCode status, string message, string details)
    {
        return new JObject
        {
            ["status"] = (int)status,
            ["message"] = message ?? "The Agentic Runtime returned an error.",
            ["details"] = details ?? string.Empty
        };
    }

    private HttpResponseMessage ErrorResponse(HttpStatusCode status, string message, string details)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                ErrorPayload(status, message, details).ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8,
                "application/json")
        };
    }

    /// <summary>
    /// The runtime returns bare status codes for the common misconfigurations,
    /// so each one is translated into the specific thing to go and check.
    /// </summary>
    private static string ExplainStatus(HttpStatusCode status)
    {
        switch ((int)status)
        {
            case 401:
                return "Unauthorized. The access token must be issued for the https://api.powerplatform.com audience, "
                    + "and the app registration needs the delegated Power Platform API permission CopilotStudio.Copilots.Invoke.";
            case 403:
                return "Forbidden. Share the agent with the signed-in user, and confirm admin consent was granted for "
                    + "CopilotStudio.Copilots.Invoke. This endpoint rejects app-only (service principal) tokens: the "
                    + "connection must be a delegated user sign-in.";
            case 404:
                return "Not found. Check the Environment Host on the connection and the agent schema name, and confirm "
                    + "the agent is published. Agents that are not served by the agentic runtime will not resolve here.";
            case 408:
            case 504:
                return "The agent did not finish the turn in time. Agentic loop turns can outlast the Power Platform "
                    + "request timeout of roughly 120 seconds.";
            case 429:
                return "Throttled by the Agentic Runtime. Retry after a short delay.";
            default:
                return "The Agentic Runtime returned HTTP " + (int)status + ".";
        }
    }

    private HttpResponseMessage CreateJsonRpcSuccessResponse(JToken id, JObject result)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = result
                }.ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8,
                "application/json")
        };
    }

    private HttpResponseMessage CreateJsonRpcErrorResponse(JToken id, int code, string message)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JObject
                    {
                        ["code"] = code,
                        ["message"] = message
                    }
                }.ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8,
                "application/json")
        };
    }

    // ---------------------------------------------------------------------
    // Telemetry
    // ---------------------------------------------------------------------

    private static bool IsAppInsightsConfigured()
    {
        return APP_INSIGHTS_ENABLED
            && !string.IsNullOrEmpty(APP_INSIGHTS_KEY)
            && !APP_INSIGHTS_KEY.Contains("INSERT_YOUR");
    }

    private async Task LogToAppInsightsAsync(string eventName, IDictionary<string, string> properties = null)
    {
        if (!IsAppInsightsConfigured())
        {
            return;
        }

        try
        {
            var payload = new
            {
                name = "Microsoft.ApplicationInsights.Event",
                time = DateTime.UtcNow.ToString("O"),
                iKey = APP_INSIGHTS_KEY,
                data = new
                {
                    baseType = "EventData",
                    baseData = new
                    {
                        ver = 2,
                        name = eventName,
                        properties = properties ?? new Dictionary<string, string>()
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, APP_INSIGHTS_ENDPOINT)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json")
            };

            await this.Context.SendAsync(request, this.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Telemetry must never break the operation.
        }
    }

    private async Task LogExceptionToAppInsightsAsync(Exception ex, string operation)
    {
        if (!IsAppInsightsConfigured())
        {
            return;
        }

        await LogToAppInsightsAsync("Exception", new Dictionary<string, string>
        {
            { "operation", operation ?? string.Empty },
            { "message", ex.Message ?? string.Empty },
            { "stackTrace", ex.StackTrace ?? string.Empty }
        }).ConfigureAwait(false);
    }
}
