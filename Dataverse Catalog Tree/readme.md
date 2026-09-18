# Dataverse Catalog Tree

A dual-purpose Power Platform custom connector for **recursive 1:N hierarchies in Dataverse** — the "tree" shape where a parent has many children and each child can have many children of its own.

It serves two consumers from one traversal engine:

- **MCP endpoint** (`/mcp`) — Copilot Studio agents get nine catalog tools over JSON-RPC 2.0.
- **Typed operations** — Power Automate and Power Apps get first-class actions with IntelliSense and dynamic dropdowns for building and maintaining the catalog.

## The problem this solves

Agents hallucinate on hierarchical data for a specific, mechanical reason: traversing a tree through a generic query tool requires the model to chain calls, and at every hop it must supply a relationship name, a lookup column and a GUID. Each of those is an opportunity to invent something plausible. By the fourth hop the model is reasoning about records it never actually read, and a tree with an unexpanded branch is indistinguishable from a tree that ends there.

This connector removes the chaining. One call returns the finished subtree, assembled server-side, with explicit accounting for anything that was left out.

## Two shapes, both supported

| Shape | Example | How it is configured |
|-------|---------|----------------------|
| **Self-referencing** — one table pointing at itself | `Category` with a `Parent Category` lookup | Nothing to configure. The connector finds the self-referencing relationship in metadata. |
| **Heterogeneous levels** — a different table per level | `Category` → `Subcategory` → `Product` | Pass `levels` as an ordered relationship path, for example `tst_category_subcategories>tst_subcategory_products`. |

Call `describe_catalog` once and it returns the exact `levelsPath` string to use. Paste that into the agent instructions and the value is fixed from then on.

**If you have a choice, model the catalog as a self-referencing table and mark the relationship hierarchical.** That unlocks the single-query strategy below and makes arbitrary depth free.

## Traversal strategies

`strategy=auto` picks the cheapest correct engine. You can force one for testing.

| Strategy | Requests | When `auto` picks it | Trade-off |
|----------|----------|----------------------|-----------|
| `hierarchy` | **1** data call for the entire subtree, any depth | Self-referencing relationship marked hierarchical, and a root id was supplied | Cannot apply a `filter`. Dataverse caps hierarchical recursion at 100 levels. |
| `levels` | One batched call per level | Everything else — the general case | Costs a call per level, but keeps full control of ordering, paging and limits. |
| `expand` | **1** call | Never chosen automatically; opt in | Dataverse forbids `$top` and `$orderby` anywhere in a query containing a nested one-to-many `$expand`, so result size cannot be bounded. |

The `levels` engine batches by level, not by node: all parents at a depth are queried in one request using a chunked `or` filter. A 500-node tree four levels deep costs about five calls, not 500.

## What stops the hallucinations

1. **One call, no chaining.** `get_catalog_tree` returns the whole subtree. The model never assembles a path across turns.
2. **Errors name the valid values.** An unknown relationship returns `validValues` listing the real ones, so the model corrects itself from data rather than guessing again:

   ```json
   {
     "error": "unknown_relationship",
     "message": "'product_lines' is not a relationship from 'tst_category'.",
     "providedValue": "product_lines",
     "validValues": [
       { "value": "tst_category_subcategories", "description": "Children in tst_subcategory via tst_parentcategoryid" }
     ],
     "hint": "Retry with one of these relationship schema names."
   }
   ```

3. **Unexpanded branches are counted, not hidden.** Every node carries `childCount` and `hasMoreChildren`. When depth runs out, one extra batched query counts the children at the boundary, so the agent says "12 more below Seattle" instead of inferring a leaf or inventing the contents.
4. **Truncation is explicit.** `stats.truncated` and `stats.truncationReason` (`depthLimit`, `nodeBudget`, `pageLimit`, `rootLimit`) mean an incomplete tree can never be mistaken for a complete one. Exhausting the per-invocation Dataverse call budget is deliberately *not* a truncation: it raises a `request_budget_exceeded` error, because a tree that stopped growing for an internal reason should never be presented as data.
5. **Identity flows one way.** Every node returns `id`, `table` and `path`. Write operations validate GUIDs and reject anything malformed with `invalid_id`, so a fabricated identifier fails loudly instead of silently targeting nothing.
6. **`search_catalog` grounds names in records.** It resolves user-typed text to real ids with full paths, which is the step that should precede any traversal or write.
7. **The `outline` format is cheap to read.** Indented text with ids costs a fraction of the tokens of nested JSON and gives the model nothing to reassemble.
8. **Cycles cannot run away.** A visited-set guards traversal, `move_catalog_node` refuses a parent that sits below the node, and `cyclesDetected` reports anything skipped.
9. **Destructive work refuses rather than improvises.** `delete_catalog_node` stops on a node with children and reports the count, and rejects any cascade it could not finish before deleting anything.

## Operations

| MCP tool | Typed operation | Purpose |
|----------|-----------------|---------|
| `describe_catalog` | `GET /catalog/describe` | Read the real hierarchy shape from metadata and get the exact `levelsPath`. |
| `get_catalog_tree` | `GET /catalog/tree` | Return a whole subtree. The main operation. |
| `get_node_children` | `GET /catalog/children` | One level of children, for drilling into a truncated branch. |
| `get_node_ancestors` | `GET /catalog/ancestors` | Breadcrumb from a node up to its root. |
| `search_catalog` | `GET /catalog/search` | Find nodes by name across levels, with full paths. |
| `create_catalog_node` | `POST /catalog/node` | Create a record and attach it to a parent. |
| `update_catalog_node` | `POST /catalog/node/update` | Rename a node or set other columns. Parent lookups are rejected here. |
| `move_catalog_node` | `POST /catalog/node/move` | Reparent a node, with a cycle guard. |
| `delete_catalog_node` | `POST /catalog/node/delete` | Delete a node, with explicit child handling. |
| — | `GET /metadata/tables` | Populates table pickers. Internal. |
| — | `GET /metadata/childrelationships` | Populates relationship pickers. Internal. |

### Deleting safely

`delete_catalog_node` refuses by default rather than guessing, because a destructive operation is the worst place for an agent to improvise. `onChildren` controls what happens when the node is not a leaf:

| Mode | Behaviour |
|------|-----------|
| `refuse` (default) | Fails with `node_has_children`, reporting the exact count and offering the other two modes in `validValues`. Nothing is changed. |
| `reparent` | Attaches the children to the node's own parent — or detaches them to become roots, if the node was a root — then deletes the node. |
| `cascade` | Deletes the node and every descendant, deepest first. |

`reparent` and `cascade` require a self-referencing catalog; for heterogeneous levels they return `child_handling_unavailable`, since the children of a node live in a different table from that node's parent.

Two refusals protect against half-finished destruction: a cascade whose subtree exceeds `maxNodes` raises `subtree_too_large`, and one that would run past the 80-call budget raises `delete_budget_exceeded`. Both are checked **before** any record is deleted.

### Response shape

```json
{
  "definition": { "mode": "selfReference", "hierarchical": true, "levelsPath": "tst_category_parent", "levels": [ ... ] },
  "stats": {
    "nodeCount": 7, "maxDepthReached": 3, "depthRequested": 5,
    "truncated": false, "truncationReason": "", "cyclesDetected": 0,
    "strategy": "hierarchy", "dataverseRequests": 2, "elapsedMs": 380
  },
  "roots": [ { "id": "...", "label": "Contoso Catalog Root", "childCount": 2, "hasMoreChildren": false, "children": [ ... ] } ]
}
```

Set `format` to `tree` (default), `flat`, `outline` or `all`. The `outline` form is the one to prefer in agent instructions:

```
Contoso Catalog Root (account, bdce4b86-dcb2-f111-aaae-70a8a5b2ff44)
  Contoso East (account, c3ce4b86-dcb2-f111-aaae-70a8a5b2ff44)
    Contoso East - Boston (account, 99005488-dcb2-f111-aaae-6045bd061c5d)
  Contoso West (account, cda82982-dcb2-f111-aaae-6045bd061c5d)
    Contoso West - Seattle (account, c8ce4b86-dcb2-f111-aaae-70a8a5b2ff44) [3 more children not expanded]
```

## Limits

Defaults are conservative; every one is a parameter.

| Limit | Default | Maximum |
|-------|---------|---------|
| `depth` | 3 | 10 |
| `maxNodes` | 500 | 5000 |
| `rootTop` — roots returned when no `id` is given | 50 | 500 |
| Children per request | — | 500 |
| Dataverse calls per invocation | — | 80 |
| Parent ids per batched filter | 25 | — |

Custom connector code must finish within **2 minutes** and the script must stay under **1 MB**. Both are comfortable here: the `hierarchy` strategy costs two calls regardless of tree size.

## Setup

1. Set `host` in `apiDefinition.swagger.json` to your environment, for example `contoso.crm.dynamics.com`. It is shipped as the placeholder `org.crm.dynamics.com`.
2. Set `AzureActiveDirectoryResourceId` and `resourceUri` in `apiProperties.json` to the same `https://<org>.crm.dynamics.com` value.
3. Replace `[YOUR_CLIENT_ID]` with your Entra app registration client id. Leave `[YOUR_REDIRECT_URL]` alone — the platform generates and overwrites it on deploy.
4. Deploy:

   ```powershell
   pac connector create `
     --api-definition-file .\apiDefinition.swagger.json `
     --api-properties-file .\apiProperties.json `
     --script-file .\script.csx
   ```

5. Read back the generated redirect URL and register it on the app registration, otherwise OAuth consent fails:

   ```powershell
   pac connector download --connector-id <id> --outputDirectory ./verify
   # properties.connectionParameters.token.oAuthSettings.redirectUrl
   ```

   The generated URL encodes both the connector name and the environment, so a connector deployed to dev and prod produces two different URLs and **both** must be registered.

### Copilot Studio

Add the connector to the agent as an MCP server. Copilot Studio lists the seven tools automatically.

> **Add it once.** If the same connector is added both as an MCP server *and* as individual connector actions, the agent sees two overlapping sets of the same capability, and wrong-tool selection is exactly the failure mode this design exists to prevent.

Suggested agent instruction:

```
The catalog is stored in Dataverse. Use search_catalog to turn any name the user mentions
into a record id before doing anything else. Use get_catalog_tree with format=outline to
read structure. Never invent ids, table names or relationship names. If a tool returns
validValues, retry with one of them. If a node reports hasMoreChildren, say how many
children were not shown rather than listing or guessing them.
```

### Power Automate

The typed operations appear as ordinary actions. `GetTree` with `format=flat` gives an array that feeds straight into Apply to each.

## Verified behavior

Built and tested against a live Dataverse environment using a four-level self-referencing account hierarchy and a three-level `account → contact → task` chain: 76 assertions covering both shapes, all three strategies, depth and node-budget truncation, boundary child counts, search path resolution, ancestor breadcrumbs, create, rename, reparent, all three delete modes, cycle rejection on move, MCP protocol handling, and every structured error path. The connector was then deployed with `pac connector create` to confirm it compiles in the connector runtime.

Three Dataverse Web API behaviors worth recording, each confirmed by request rather than by documentation:

- **A nested `$expand` on a collection-valued 1:N does work**, to at least three nested levels. The documented restriction to many-to-one applies to N:N relationships.
- **It is rejected on a key-addressed URL.** `accounts(<id>)?$expand=children($expand=children(...))` returns *"Only many-to-one relationships are supported for nested expansion"*, while the same expand against `accounts?$filter=accountid eq <id>` succeeds. The `expand` strategy uses the collection form for this reason.
- **`$top` and `$orderby` are forbidden anywhere in a query containing a nested one-to-many `$expand`**, including at the top level. This is why `expand` is never the automatic choice: the result size cannot be bounded.

## Local testing

Copy `script.csx` to a console project as `Script.cs`, stub `ScriptBase` and `IScriptContext` in the global namespace, and point the fake context's `SendAsync` at your environment with a bearer token. Compile byte-identical to the shipped file so the harness keeps testing the real artifact. See the `connector-testing` skill in `Connector-Code/Invoke Deployed Connector/`.

## Application Insights

Telemetry is off by default. Set `APP_INSIGHTS_ENABLED` to `true` and fill `APP_INSIGHTS_KEY` in `script.csx`. Events cover MCP tool calls, structured catalog errors and unhandled exceptions. Telemetry failures are swallowed and never affect the operation.

---

**Version**: 1.0.0
**Brand Color**: #da3b01
**MCP Tools**: 9
**Author**: Troy Taylor · troy@troystaylor.com · https://github.com/troystaylor
