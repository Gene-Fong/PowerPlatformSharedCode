# Copilot Studio Agent Runtime

Call a published Microsoft Copilot Studio agent from Power Automate or from another Copilot Studio agent — including agents running on the **GitHub Copilot (agentic loop) harness**, which the supported Copilot Studio client library does not yet cover. The connector buffers the server-sent event stream the runtime returns and hands back a single completed JSON answer, so a flow gets a finished reply instead of a stream it cannot consume.

## Overview

Every other Copilot Studio connector in this repository works on the **control plane** — it manages, inventories, or reports on agents. This one works on the **data plane**: it actually runs a turn.

| Connector | Plane | What it does |
|---|---|---|
| [Copilot Studio Bots](../Copilot%20Studio%20Bots) | Control | Evaluate, quarantine, inventory, migrate identity |
| [Copilot Studio Analytics](../Copilot%20Studio%20Analytics) | Reporting | Read conversation transcripts and session records after the fact |
| **Copilot Studio Agent Runtime** | **Data** | **Open a conversation and get an answer** |

The pairing with Copilot Studio Bots is deliberate. Its `List Agents` operation returns both `schemaName` and `isCLIAgent`, which are exactly the two fields you need to decide *which* agent to call here and *whether* it runs on the expensive GitHub Copilot harness.

## The endpoint

```text
POST https://{environment-host}/copilotstudio/agenticruntime/3p/dataverse-backed/authenticated/bots/{schemaName}/conversations?api-version=1
POST https://{environment-host}/copilotstudio/agenticruntime/3p/dataverse-backed/authenticated/bots/{schemaName}/conversations/{conversationId}?api-version=1
```

> **This endpoint is undocumented.** It does not appear on Microsoft Learn, and it carries no compatibility guarantee. Microsoft's position as of this writing is that the Copilot Studio client library "can only be used with agents created by using the standard harness" and that "agents using the GitHub Copilot harness aren't yet officially supported." The `/3p` agentic runtime route is how Microsoft's own experimental Copilot Studio plugin reaches those agents. Treat it as experimental and keep a fallback.

The `agenticruntime` prefix and the `3p` (third-party) segment are what distinguish this from the classic Direct-to-Engine route (`/copilotstudio/dataverse-backed/...`, `api-version=2022-03-01-preview`) used elsewhere in this repository by [Power Agent Desktop](../Power%20Agent%20Desktop) and [Power Agent Tray](../Power%20Agent%20Tray).

## Prerequisites

**The agent must be:**

- **Published.** Draft agents do not resolve.
- Configured with **Authenticate with Microsoft** — this is the `/authenticated/` segment of the path.
- **Shared with the signed-in user.** An unshared agent returns `403`.

**The app registration must have:**

- The **Power Platform API** delegated permission `CopilotStudio.Copilots.Invoke`, with admin consent granted.
- A web redirect URI of the connector's generated consent URL (see [Deployment](#deployment)).

> **Delegated sign-in only.** The `/authenticated/` route rejects app-only (client credentials) tokens. Service-to-service Direct-to-Engine exists but is a private preview, applies only to **No Authentication** agents, and returns `S2SDirectEngineRequiresNoAuthentication` against this route. There is no service principal option here — every connection is a user sign-in.

## Connection Setup

| Parameter | Description |
|-----------|-------------|
| **OAuth Connection** | Sign in as a user who has access to the agent |
| **Environment Host** | The environment-specific hostname that serves your agent |

### Deriving the Environment Host

The host is your environment GUID with the hyphens removed, split after the 30th character:

```text
{30 hex characters}.{2 hex characters}.environment.api.powerplatform.com
```

```powershell
$environmentId = "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e809"
$id = $environmentId.Replace("-", "").ToLower()
"$($id.Substring(0, 30)).$($id.Substring(30)).environment.api.powerplatform.com"

# 0a1b2c3d4e5f60718293a4b5c6d7e8.09.environment.api.powerplatform.com
```

Enter the hostname only, with no `https://` and no trailing slash.

Non-production clouds use a different suffix — `api.test.`, `api.preprod.`, `api.dev.` and so on — and the connector reads the host from the connection rather than hardcoding it, so those work too. Sovereign clouds (GCC, GCC High, DoD, China) have **not** been verified against this endpoint; the agentic runtime may not be deployed there at all.

### Finding the Agent Schema Name

The schema name looks like `cr1a2_myAgent`. You can get it from:

- The agent's **Settings** page in Copilot Studio
- The **Direct connection** URL shown in the agent's channel settings
- `List Agents` in the [Copilot Studio Bots](../Copilot%20Studio%20Bots) connector, which also tells you whether the agent is on the GitHub Copilot harness

## Capabilities

### REST Operations (Power Automate)

| Operation | Description |
|-----------|-------------|
| **Ask Agent** | One-shot: opens a conversation, sends a message, returns the finished answer |
| **Start Conversation** | Opens a conversation and returns the conversation ID plus any greeting |
| **Send Message** | Sends a turn to an existing conversation and returns the finished reply |
| **Invoke Copilot Studio Agent Runtime MCP** | The JSON-RPC endpoint — it appears in the action list, but ignore it in a flow |

Use **Ask Agent** for stateless work. Use **Start Conversation** followed by **Send Message** when the agent needs to remember earlier turns.

Both **Ask Agent** and **Send Message** accept an optional **Locale** (BCP 47, for example `en-GB`) to set the language of the turn.

#### Sending a raw activity (advanced)

**Send Message** also exposes a **Raw Activity** field. Supply it instead of **Message** to send something other than a plain user message — an `event` or `invoke` activity that triggers a topic directly, for example:

```json
{
  "activity": {
    "type": "event",
    "name": "StartOrderLookup",
    "value": { "orderId": "SO-4417" }
  }
}
```

When **Raw Activity** is present, **Message** is ignored. The `conversation` property is always overwritten with the conversation in the request path, so a caller cannot post into a conversation it does not own.

### MCP Tools (Copilot Studio)

| Tool | Arguments | Description |
|------|-----------|-------------|
| `ask_agent` | `schemaName`\*, `text`\*, `locale` | Ask a published agent one question and get the completed answer |
| `start_conversation` | `schemaName`\*, `emitStartConversationEvent` | Open a conversation and return its ID. Set `emitStartConversationEvent` to `false` to suppress the greeting; it defaults to `true`. |
| `send_message` | `schemaName`\*, `conversationId`\*, `text`\*, `locale` | Continue an existing conversation |

\* required

This is what makes agent-to-agent invocation possible: a Copilot Studio agent can call a GitHub Copilot harness agent as a tool, which no supported channel currently offers.

## How the streaming is handled

The runtime replies with `text/event-stream`, not JSON. Power Platform cannot surface a stream to a flow, so the connector reads the whole stream and consolidates it.

The consolidation is not a naive concatenation, because the runtime streams in two different shapes depending on the agent and client:

| Shape | What arrives | How it is handled |
|---|---|---|
| **Cumulative** | Each `typing` activity repeats everything so far | Keep only the last one |
| **Delta** | Each `typing` activity carries a new fragment | Concatenate them in order |
| **Final** | A `message` activity with `streamType: "final"` | Authoritative — wins over both |

The connector detects which shape it received by testing whether each chunk begins with the previous one. Getting this wrong in either direction is visible: treating cumulative text as deltas repeats the answer several times over, and treating deltas as cumulative truncates it to the last fragment.

The response returns the consolidated `text` plus the full `activities` array, so nothing is lost if you need the raw stream.

```json
{
  "conversationId": "abc123",
  "text": "Paris is the capital of France.",
  "isComplete": true,
  "activityCount": 4,
  "attachments": [],
  "suggestedActions": [ { "type": "imBack", "title": "Tell me more", "value": "more" } ],
  "citations": [ { "@type": "Claim", "position": 1, "appearance": { "name": "France", "url": "https://example.com" } } ],
  "activities": [ "..." ]
}
```

| Field | Notes |
|---|---|
| `text` | The answer. This is the field you want in almost every flow. |
| `isComplete` | `false` means the runtime never signalled turn completion — treat `text` as partial. |
| `activityCount` | Activities in this turn only, not the whole conversation. |
| `activities` | The raw stream, in order, if you need to inspect it. |
| `greetingActivities` | **Ask Agent only.** What the agent said when the conversation opened, kept separate so it does not pollute `text`. |

Conversation binding is forced server-side: whatever conversation a caller names in a raw activity, the connector rebinds it to the conversation in the request path, so one caller cannot post into another's conversation.

## Errors

The runtime returns bare status codes for the common misconfigurations, so the connector translates each one into the specific thing to check. Failures come back in a consistent shape:

```json
{
  "status": 403,
  "message": "Forbidden. Share the agent with the signed-in user, and confirm admin consent was granted for CopilotStudio.Copilots.Invoke. This endpoint rejects app-only (service principal) tokens: the connection must be a delegated user sign-in.",
  "details": "<raw response body from the runtime>"
}
```

| Status | Meaning |
|---|---|
| `400` | Missing `schemaName`, `conversationId`, or message text — raised by the connector before any call is made |
| `401` | Wrong token audience, or the app registration is missing `CopilotStudio.Copilots.Invoke` |
| `403` | Agent not shared with the user, admin consent not granted, or an app-only token was used |
| `404` | Wrong environment host or schema name, agent not published, or not served by the agentic runtime |
| `408` / `504` | The agent did not finish the turn in time — see **Turn length** under [Limitations](#limitations) |
| `429` | Throttled — retry after a delay |

In MCP, the same payload is returned as the tool result with `isError: true`, so the calling agent can read the explanation rather than just seeing a failure.

## Limitations

- **Turn length.** Power Platform cuts a connector request off at roughly 120 seconds. Agentic loop turns routinely run longer, and there is no polling or resume route on this endpoint — a long turn is simply lost. Keep prompts tightly scoped, and do not build on this for deep research tasks.
- **No streaming to the caller.** The answer arrives all at once when the turn completes. Progressive rendering is not possible through a custom connector.
- **Delegated identity only.** Every call runs as the signed-in user. There is no unattended or service-account option.
- **Undocumented and unversioned in practice.** `api-version=1` is pinned because it is what the route accepts today. Microsoft can change or withdraw this endpoint without notice.
- **Cost.** Agents on the GitHub Copilot harness bill 100–500+ Copilot Credits per task regardless of M365 Copilot licensing. Calling one in a loop from a flow gets expensive quickly. Use `List Agents` in the Copilot Studio Bots connector to confirm which harness an agent runs on before wiring it into automation.

## Deployment

```powershell
pac auth create --environment <environment-id>
pac connector create `
    --api-definition-file apiDefinition.swagger.json `
    --api-properties-file apiProperties.json `
    --script-file script.csx
```

Then:

1. Replace `[YOUR_CLIENT_ID]` in `apiProperties.json` with your app registration's client ID, and set the client secret in the portal **Security** tab.
2. Read back the generated redirect URL and register it on the app registration:

   ```powershell
   pac connector download --connector-id <id> --outputDirectory ./verify
   # properties.connectionParameters.token.oAuthSettings.redirectUrl

   az ad app update --id <appId> --web-redirect-uris `
       "https://global.consent.azure-apim.net/redirect" "<per-connector-url>"
   ```

3. Create a connection, supplying the **Environment Host** derived above.

Validate before deploying:

```powershell
npx ppcv "./Copilot Studio Agent Runtime"
```

## Example scenarios

### Ask an agent from a flow

Use **Ask Agent** when each call is independent — a triggered flow that needs one answer.

| Field | Value |
|---|---|
| Agent Schema Name | `cr1a2_contractReviewer` |
| Message | `Summarise the risks in contract @{triggerBody()?['contractId']}` |

Read `text` from the output. That is the finished answer; you do not need to touch `activities`.

### Hold a multi-turn conversation

When later turns depend on earlier ones, keep the conversation open:

1. **Start Conversation** → store `conversationId` in a variable.
2. **Send Message** with that `conversationId` → read `text`.
3. Repeat step 2 for each follow-up. The agent retains context across the turns.

Do not call **Ask Agent** in a loop for this — each call opens a fresh conversation and the agent forgets everything.

### Let one Copilot Studio agent call another

This is the scenario the MCP endpoint exists for, and it is the only way to reach a GitHub Copilot harness agent as a tool:

1. In Copilot Studio, open the calling agent and go to **Tools** → **Add a tool** → **Model Context Protocol**.
2. Select this connector and create or pick a connection.
3. The three tools appear. Add `ask_agent` for one-shot delegation.
4. In the calling agent's instructions, tell it when to delegate and which `schemaName` to pass — the model will not know the schema name otherwise:

   > When the user asks a contract question, call `ask_agent` with `schemaName` set to `cr1a2_contractReviewer` and the user's question as `text`.

Give the calling agent `start_conversation` and `send_message` as well only if it genuinely needs a multi-turn side conversation. For most delegation, `ask_agent` alone produces better behaviour.

## Verifying a connection

If a call fails, confirm each layer in this order — it isolates the problem faster than reading the error alone:

1. **Host** — does the Environment Host match the `{30}.{2}` split of your environment GUID? A wrong host gives `404`, not a connection error.
2. **Schema name** — run `List Agents` in the [Copilot Studio Bots](../Copilot%20Studio%20Bots) connector and copy `schemaName` exactly. It is case-sensitive.
3. **Published** — republish the agent if in doubt. A draft-only agent gives `404`.
4. **Sharing** — open the agent in Copilot Studio and confirm the signed-in user is on the share list. Missing sharing gives `403`.
5. **Permission** — check the app registration has `CopilotStudio.Copilots.Invoke` with admin consent granted. Missing consent also gives `403`.

A `401` is almost always the app registration rather than the agent: wrong audience, or the Power Platform API permission was never added.

## Application Insights

Telemetry is hardcoded and disabled by default. To enable it, set `APP_INSIGHTS_ENABLED` to `true` and replace `APP_INSIGHTS_KEY` in `script.csx`. Failures are swallowed silently so telemetry can never break a turn.

## Author

Troy Taylor — [troy@troystaylor.com](mailto:troy@troystaylor.com) — [github.com/troystaylor](https://github.com/troystaylor)
