# Brightspace

A dual-purpose Power Platform custom connector for the **D2L Brightspace (Valence) Developer Platform API**.

One connector definition serves two audiences:

| Audience | Surface | What they get |
|---|---|---|
| Power Automate, Power Apps | 30 typed REST actions | Named actions with real schemas, IntelliSense, and field pickers |
| Copilot Studio | MCP endpoint at `/mcp` | `scan` / `launch` / `sequence` over the *entire* Brightspace API, plus version and paging helpers |

The REST half covers the operations people build flows against. The MCP half is not
limited to those — `launch_brightspace` can call any Brightspace route, so an agent is
never blocked by an operation nobody thought to declare in the Swagger.

---

## Before you start

You need three things from your institution's Brightspace tenant:

1. **The host name**, for example `school.brightspace.com`. Brightspace has no fixed
   tenant host pattern; it is whatever hostname the institution is deployed on.
2. **An OAuth 2.0 application registration**, created in the Manage Extensibility admin tool.
3. **The scopes** the registration is allowed to request.

### Register the OAuth application

In Brightspace, go to **Admin Tools → Manage Extensibility → OAuth 2.0 → Register an app**, then:

| Field | Value |
|---|---|
| Application Name | Whatever users should see on the consent page |
| Authentication workflow | **Authorization grant** — this connector uses the authorization code flow |
| Redirect URI | The per-connector URL Power Platform generates (see [Redirect URI](#redirect-uri) below) |
| Scopes | Space-delimited list — see [Scopes](#scopes) |
| Prompt for user consent | Enabled |
| **Enable refresh tokens** | **Enabled** — required, or connections break when the access token expires |

Brightspace then issues a **Client ID** and **Client Secret**.

> Brightspace's documentation never mentions PKCE, so do not assume it is supported.
> This connector uses a confidential client with a client secret, which is what the
> documented Authorization Code Grant describes.

### Redirect URI

This is a chicken-and-egg step. Power Platform generates the redirect URL when the
connector is created, so:

1. Deploy the connector with `redirectUrl` left as the `[YOUR_REDIRECT_URL]` placeholder.
   The platform discards whatever you supply and generates its own.
2. Read the generated value back:

   ```powershell
   pac connector download --connector-id <id> --outputDirectory ./verify
   # properties.connectionParameters.token.oAuthSettings.redirectUrl
   ```

3. Register that exact URL on the Brightspace application registration.

The generated URL encodes both the connector name and the environment, so **a connector
deployed to dev and prod produces two different redirect URLs and both must be registered.**

---

## Configure

### 1. Set the host

`apiDefinition.swagger.json` ships with a placeholder host. Replace it with your institution's:

```jsonc
"host": "[YOUR_INSTITUTION].brightspace.com"   // -> "school.brightspace.com"
```

This is the only place the host appears. The script derives the API base URL from the
incoming request, so the MCP tools automatically target the same host.

**You cannot deploy without replacing it.** Square brackets are not legal in a hostname,
so Power Platform rejects the definition:

```
Error: ApiHubsRequestFailed
Invalid Api definition object. Please specify a valid Swagger 2.0 Url and valid list of ServiceUrls.
```

That is the intended behaviour — the deployment fails loudly rather than silently
publishing a connector pointed at nothing.

### 2. Set the client ID

In `apiProperties.json`, replace `[YOUR_CLIENT_ID]` with the Client ID from the
Brightspace registration.

Supply the **client secret** at deploy time or in the portal Security tab — never commit it.

### 3. Set the scopes

The `scopes` array in `apiProperties.json` ships with a read-focused set covering the
connector's actions, plus write access to grades and announcements. Adjust it to match
what your integration actually needs, and register the **same** scopes on the Brightspace
application — a token can only request scopes the registration already holds.

---

## Scopes

Brightspace scopes are `<resource-group>:<resource>:<action>`, space-delimited, with `*`
and comma wildcards (`users:userdata:create,read,update` — no spaces around the commas).

> **`core:*:*` is not a master key.** It is the *general fallback* scope, and it only
> covers API actions that have **no specific scope assigned yet**. Any action that does
> have a specific scope still needs that scope explicitly. Ship both: the fallback for
> the unscoped remainder, and the specific scopes for everything else.

The shipped default covers reading across all areas, plus writing grades and
announcements:

| Area | Read | Write |
|---|---|---|
| Users | `users:userdata:read` `users:profile:read` `users:own_profile:read` | `users:userdata:create,update,delete` |
| Organization | `organizations:organization:read` | — |
| Courses, org structure | `orgunits:course:read` `managecourses:courses:read` | `orgunits:course:create,update,delete` `managecourses:courses:write` |
| Enrollments | `enrollment:own_enrollment:read` `enrollment:orgunit:read` | `enrollment:orgunit:create,delete` |
| Grades | `grades:gradeobjects:read` `grades:gradevalues:read` `grades:gradecategories:read` | `grades:gradevalues:write` `grades:gradeobjects:write` |
| Content | `content:toc:read` `content:modules:readonly` `content:topics:readonly` | `content:topics:manage` `content:modules:manage` |
| Assignments | `dropbox:folders:read` | `dropbox:folders:write` |
| Announcements | `news:newsitems:read` | `news:newsitems:manage` |
| Discussions | `discussions:forums:readonly` `discussions:topics:readonly` `discussions:posts:readonly` | `discussions:posts:manage` |
| Quizzes | `quizzing:quizzes:read` `quizzing:attempts:read` | `quizzing:quizzes:write` |

The write scopes above are **not** in the shipped default except for grades and
announcements. Add the ones you need to both `apiProperties.json` and the Brightspace
registration. The full list lives in the [scopes index](https://docs.valence.desire2learn.com/http-scopestable.html).

Scopes gate which API *actions* the token may call. They do **not** widen what the user
can do — the signed-in Brightspace user's role in each org unit still decides that. A
`403` almost always means a permissions problem, not a malformed request.

---

## Deploy

```powershell
pac auth create --environment <environment-id>
pac connector create `
    --api-definition-file ./apiDefinition.swagger.json `
    --api-properties-file ./apiProperties.json `
    --script-file ./script.csx
```

Validate before deploying:

```powershell
ppcv ./Brightspace
```

> ppcv reports *"Unbalanced braces"* on `script.csx`. This is a false positive — its
> brace counter does not understand C# verbatim strings, and it reports the same depth
> for the unmodified Power Mission Control template. The script compiles cleanly, both
> locally and on Power Platform's own compiler.
>
> ppcv also warns that most operations are absent from `scriptOperations`. That is
> deliberate — see [Which operations are scripted](#which-operations-are-scripted).

### Deployment verified

Deployed to a live Power Platform environment on **PAC CLI 2.11.2**, then downloaded and
compared. Confirmed:

| Check | Result |
|---|---|
| Operations deployed | 31 of 31 |
| `x-ms-agentic-protocol` on `/mcp` | survived as `mcp-streamable-1.0` |
| `script.csx` round-trip | byte-for-byte identical |
| `scriptOperations` | all three preserved |
| OAuth scopes | all 24 preserved |
| Authorization and token URLs | preserved verbatim |
| `clientId` | preserved verbatim — a placeholder deploys as-is, so replace it |
| `redirectMode` | rewritten `Global` → `GlobalPerConnector` by the platform |
| `redirectUrl` | replaced by the platform with a generated per-connector URL |

The generated redirect URL looked like this, confirming the shape described above:

```
https://global.consent.azure-apim.net/redirect/new-5fbrightspace-5ff5dc2f63c87a6469
```

---

## API versions

Brightspace versions each product component separately, and the version is a path
segment: `/d2l/api/{component}/{version}/{path}`.

| Component | Covers | Pinned version | Status |
|---|---|---|---|
| `lp` | Learning Platform — users, org structure, courses, enrollments | **1.49** | Oldest non-deprecated (1.46–1.48 deprecated, 1.45 and older obsolete) |
| `le` | Learning Environment — grades, content, assignments, discussions, quizzes, news | **1.82** | Oldest non-deprecated (1.75–1.81 deprecated, 1.74 and older obsolete) |

Both are pinned to the **oldest version D2L still supports**, so the connector works on
any current tenant. Later contract versions are supersets, so raise a version only when
you need a route that was added later.

- **REST actions** expose `version` as an advanced path parameter, pre-filled with the
  pinned default. Override it per action if you need to.
- **MCP tools** take the version from the `LP_VERSION` and `LE_VERSION` constants at the
  top of `script.csx`. Change them there to shift every tool at once.

To find out what your tenant supports, call the **Get API Versions** action, or have the
agent call `check_brightspace_versions`.

---

## Paging

Brightspace uses **two** paging schemes, and they are not interchangeable.

### Bookmark paging — `PagedResultSet`

```json
{
  "PagingInfo": { "Bookmark": "...", "HasMoreItems": true },
  "Items": [ ... ]
}
```

Pass `PagingInfo.Bookmark` back as the `bookmark` query parameter. Stop when
`HasMoreItems` is `false`. Used by List Users, Get My Enrollments, Get Org Unit
Enrollments, Get Org Unit Children, and Get Org Unit Descendants.

### URL paging — `ObjectListPage`

```json
{
  "Next": "https://...",
  "Objects": [ ... ]
}
```

Follow the `Next` URL verbatim; it already carries the filters you used. Stop when `Next`
is empty. Used by Get Quizzes, the paged classlist, and per-grade-item values.

Agents should use `follow_brightspace_page` for the second scheme. It rejects any URL
pointing at a different host, so a returned URL cannot redirect the bearer token elsewhere.

> **Get Classlist is unbounded** — it returns every member of a course in one response.
> For large courses prefer Get Org Unit Enrollments, which is bookmark-paged.

---

## Typed REST actions

| Action | Route |
|---|---|
| Get API Versions | `GET /d2l/api/versions/` |
| Get Current User | `GET /lp/{version}/users/whoami` |
| Get User | `GET /lp/{version}/users/{userId}` |
| List Users | `GET /lp/{version}/users/` |
| Find User By Username | `GET /lp/{version}/users/?userName=` |
| Find Users By Org Defined ID | `GET /lp/{version}/users/?orgDefinedId=` |
| Create User | `POST /lp/{version}/users/` |
| Get Organization Info | `GET /lp/{version}/organization/info` |
| Get Org Unit Children | `GET /lp/{version}/orgstructure/{orgUnitId}/children/paged/` |
| Get Org Unit Descendants | `GET /lp/{version}/orgstructure/{orgUnitId}/descendants/paged/` |
| Get Course Offering | `GET /lp/{version}/courses/{orgUnitId}` |
| Create Course Offering | `POST /lp/{version}/courses/` |
| Update Course Offering | `PUT /lp/{version}/courses/{orgUnitId}` |
| Get My Enrollments | `GET /lp/{version}/enrollments/myenrollments/` |
| Get Org Unit Enrollments | `GET /lp/{version}/enrollments/orgUnits/{orgUnitId}/users/` |
| Enroll User | `POST /lp/{version}/enrollments/` |
| Unenroll User | `DELETE /lp/{version}/enrollments/orgUnits/{orgUnitId}/users/{userId}` |
| Get Classlist | `GET /le/{version}/{orgUnitId}/classlist/` |
| Get Grade Objects | `GET /le/{version}/{orgUnitId}/grades/` |
| Get User Grades | `GET /le/{version}/{orgUnitId}/grades/values/{userId}/` |
| Get My Grades | `GET /le/{version}/{orgUnitId}/grades/values/myGradeValues/` |
| Set Grade Value | `PUT /le/{version}/{orgUnitId}/grades/{gradeObjectId}/values/{userId}` |
| Get Course Content | `GET /le/{version}/{orgUnitId}/content/toc` |
| Get Assignment Folders | `GET /le/{version}/{orgUnitId}/dropbox/folders/` |
| Get Assignment Submissions | `GET /le/{version}/{orgUnitId}/dropbox/folders/{folderId}/submissions/` |
| Get Announcements | `GET /le/{version}/{orgUnitId}/news/` |
| Create Announcement | `POST /le/{version}/{orgUnitId}/news/` |
| Get Discussion Forums | `GET /le/{version}/{orgUnitId}/discussions/forums/` |
| Get Discussion Topics | `GET /le/{version}/{orgUnitId}/discussions/forums/{forumId}/topics/` |
| Get Quizzes | `GET /le/{version}/{orgUnitId}/quizzes/` |

### Which operations are scripted

Only three operations run through `script.csx`:

| Operation | Why |
|---|---|
| `InvokeMCP` | The whole MCP server lives in the script |
| `FindUserByUsername` | Rewrites a synthetic path onto `/users/?userName=` |
| `FindUsersByOrgDefinedId` | Rewrites a synthetic path onto `/users/?orgDefinedId=` |

Every other action passes straight through to Brightspace untouched, because the Swagger
paths are the real Brightspace paths. There is nothing to transform, and adding a script
hop would only add a failure mode.

The two user-lookup actions exist because **`GET /users/` changes its response shape
depending on which query parameter it receives**:

| Parameter | Response |
|---|---|
| `userName` | A single user object |
| `orgDefinedId` or `externalEmail` | A plain array of users |
| none, or `bookmark` | A bookmark-paged result set |

Precedence is `orgDefinedId` → `userName` → `externalEmail` → `bookmark`, regardless of
the order in the URL. One Swagger operation cannot describe three shapes honestly, so the
connector declares three, each with an accurate response schema, and the script maps the
lookup variants back onto the real route.

---

## MCP tools

Add the connector to a Copilot Studio agent as an MCP tool. It exposes five tools:

| Tool | Purpose |
|---|---|
| `scan_brightspace` | Find the right operation from a natural-language intent. Always call first. |
| `launch_brightspace` | Execute any Brightspace route, with `{placeholder}` segments filled in. |
| `sequence_brightspace` | Run up to 20 operations in one call. |
| `check_brightspace_versions` | Compare the pinned versions against what the tenant supports. |
| `follow_brightspace_page` | Fetch the next page of a `Next`/`Objects` response. |

The capability index embedded in the script describes **57 operations** across ten
domains: `framework`, `users`, `orgstructure`, `courses`, `enrollments`, `grades`,
`content`, `assignments`, `announcements`, `discussions`, and `quizzes`. Scanning costs a
few hundred tokens instead of the tens of thousands that declaring 57 typed tools would.

`launch_brightspace` is not restricted to the index — it will call any route, warning
when the endpoint is unrecognized. The index exists for discovery, not enforcement.

### Example agent flow

> *"What's the average grade on the midterm in BIO-101?"*

1. `scan_brightspace("find a course by name")` → `get_org_unit_descendants`
2. `launch_brightspace` on `/lp/1.49/orgstructure/6606/descendants/paged/?ouTypeId=3` → BIO-101 is org unit `7421`
3. `scan_brightspace("grade items in a course")` → `list_grade_objects`
4. `launch_brightspace` on `/le/1.82/7421/grades/` → midterm is grade object `19`
5. `launch_brightspace` on `/le/1.82/7421/grades/19/values/` → grades, paged by `Next`
6. `follow_brightspace_page` until `Next` is empty

---

## Conventions and gotchas

| Topic | Detail |
|---|---|
| **Identifiers** | `D2LID` values are positive 64-bit integers. Passing a non-numeric value into a route segment can make Brightspace match the *wrong route handler*, not just fail. |
| **Org units** | A course is an org unit. So is a department, a semester, and the organization itself. `orgUnitId` is used throughout. |
| **Dates** | UTC ISO 8601 with milliseconds: `yyyy-MM-ddTHH:mm:ss.fffZ`. Every element is zero-padded. Some fields are Unix timestamps instead. |
| **Rich text in** | `{ "Content": "...", "Type": "Text" }` where `Type` is either `Text` or `Html` |
| **Rich text out** | `{ "Text": "...", "Html": "..." }` — note the different shape |
| **Rate limits** | Token-bucket. Watch `X-Rate-Limit-Remaining`, `X-Request-Cost`, and `Retry-After`. A `429` means the bucket is empty. The MCP layer retries `429` automatically. |
| **Errors** | RFC 7807 problem details: `{ "type", "status", "title", "detail", "instance" }` |
| **404 ≠ missing** | A `404` can also mean the tool is not enabled for that tenant. Not every Brightspace deployment has quizzes or ePortfolio. |
| **403 vs empty** | Most routes return `403` when the user cannot see anything; `GET /quizzes/` returns an empty page instead. Handle both. |
| **Unknown query params** | Silently ignored rather than rejected, so a typo fails quietly. |
| **Forward compatibility** | D2L adds fields within the same contract version. Parse tolerantly and ignore unexpected fields. |

---

## Application Insights

Telemetry is off by default. To enable it, set the connection string near the top of
`script.csx`:

```csharp
private const string APP_INSIGHTS_CONNECTION_STRING = "InstrumentationKey=...;IngestionEndpoint=https://...";
```

Telemetry is sent through `Context.SendAsync` and awaited. Failures are swallowed so
telemetry never breaks a call.

---

## Files

| File | Purpose |
|---|---|
| `apiDefinition.swagger.json` | 31 operations — 30 typed REST actions plus the MCP endpoint |
| `apiProperties.json` | OAuth 2.0 settings and the scripted-operation list |
| `script.csx` | MCP server, capability index, and the two user-lookup rewrites |
| `readme.md` | This file |

---

## Reference

- [Brightspace API reference](https://docs.valence.desire2learn.com/reference.html)
- [Calling conventions, paging, rate limits](https://docs.valence.desire2learn.com/basic/apicall.html)
- [OAuth 2.0 and scopes](https://docs.valence.desire2learn.com/basic/oauth2.html)
- [Scopes index](https://docs.valence.desire2learn.com/http-scopestable.html)
- [API versioning](https://docs.valence.desire2learn.com/basic/version.html)
- [Data type conventions](https://docs.valence.desire2learn.com/basic/conventions.html)
