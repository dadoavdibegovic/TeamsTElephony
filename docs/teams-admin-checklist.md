# Teams admin checklist — Phase 2

Click-by-click runbook for the Teams admin work needed to route SGB's
Direct Routing PSTN calls through `acs-calltranskript-prod`. Designed
to be done in one sitting (~60-90 min total, mostly waiting for
license/replication propagation).

**Prerequisite roles** (Entra/Microsoft 365):
- **Teams Administrator** (or Global Administrator) — required for all
  Teams admin center operations
- **Application Administrator** (or Global) — required for granting
  admin consent to ACS Graph permissions
- **License Administrator** (or Global) — required for assigning the
  Phone Resource Account license

If you don't have all three, identify who does and have them on standby.

---

## Prerequisites checklist (do first)

- [ ] **License availability** — confirm at least one *Microsoft Teams
  Phone Resource Account* license is available in the tenant. It's
  free in most M365 E5 plans but must be present in the SKU list.
  Check: M365 Admin Center → Billing → Licenses → look for
  "Microsoft Teams Phone Resource Account" with available count ≥ 1.

- [ ] **PSTN trunk** — confirm the SBC trunk is healthy and an inbound
  DID is reachable. We'll route this DID through ACS later.

- [ ] **Choose the test DID** — pick a low-volume number for the first
  routing test. Avoid a number that's currently routed to a busy
  user/queue. *Number selected: _____________________*

- [ ] **Identify the Teams agent who will be the transfer target** —
  after ACS screens/enriches the call, `transferhandler.ts` hands it
  to this user. Get their UPN and Object ID.
  *Agent UPN: _____________________*
  *Agent Object ID: _____________________*

---

## Step 1 — Register ACS as an Application Instance in Teams

This makes the ACS resource visible to Teams as a "calling app" that
can receive calls routed from Teams.

### 1a. Get the ACS immutable Object ID

The ACS resource has a unique GUID Teams uses to address it. Pull it via:

```sh
az resource show \
  --resource-group Call_Transkript_Infra \
  --resource-type Microsoft.Communication/CommunicationServices \
  --name acs-calltranskript-prod \
  --query "properties.immutableResourceId" -o tsv
```

- [x] **Already retrieved**: `3c686b4b-6aa0-4255-9799-8533e59605f3`
  (acs-calltranskript-prod, dataLocation=Europe, hostName=acs-calltranskript-prod.europe.communication.azure.com)

Use this value as `<ACS_IMMUTABLE_RESOURCE_ID_FROM_1A>` in step 1b.

### 1b. Create the Application Instance via PowerShell

This step **requires the Microsoft Teams PowerShell module** (not Azure CLI):

```powershell
# Install once (admin elevation required)
Install-Module MicrosoftTeams -Force -AllowClobber

# Connect with Teams Admin credentials
Connect-MicrosoftTeams

# Create the application instance
New-CsOnlineApplicationInstance `
  -UserPrincipalName "acs-calltranskript@sgb-energie.de" `
  -ApplicationId "<ACS_IMMUTABLE_RESOURCE_ID_FROM_1A>" `
  -DisplayName "CallTranskript ACS"
```

- [ ] App instance created (note the returned ObjectId).
  *ObjectId: _____________________*

> **Gotcha:** the `UserPrincipalName` must be unique across your
> tenant and must use a verified domain (`sgb-energie.de`).

### 1c. Sync the app instance to Teams

> **Microsoft Teams PowerShell ~6.x requires `-ApplicationId`.** Older
> docs show `-ObjectId` only, which errors out with "At least one of
> ApplicationId or AcsResourceId must be provided."

```powershell
Sync-CsOnlineApplicationInstance `
  -ObjectId "<OBJECT_ID_FROM_1B>" `
  -ApplicationId "<ACS_IMMUTABLE_RESOURCE_ID_FROM_1A>"
```

- [ ] Sync completed without error.
- [ ] Wait ~10-15 minutes for propagation before the next step.

---

## Step 2 — Create the Resource Account in Teams admin center

### 2a. In Teams Admin Center

Navigate to **admin.teams.microsoft.com** → **Voice** → **Resource accounts**.

- [ ] Click **+ Add**.
- [ ] **Display name**: `CallTranskript Routing`
- [ ] **Username**: `acs-calltranskript@sgb-energie.de` (must match the
  UPN from step 1b — this links the resource account to the app instance)
- [ ] **Resource account type**: choose **Auto attendant** *or*
  **Call queue** depending on whether you want IVR-style routing or
  queue-style routing. For this pilot, **Call queue** is simpler.
- [ ] Save. The resource account appears in the list.

### 2b. Assign the license

- [ ] Open M365 Admin Center → **Users** → **Active users**.
- [ ] Find `acs-calltranskript@sgb-energie.de` (it appears as a user).
- [ ] Open the user, **Licenses and apps** tab.
- [ ] Tick **Microsoft Teams Phone Resource Account**.
- [ ] Save. Wait ~10 minutes for the license to activate.

### 2c. Assign a phone number to the resource account

Back in Teams Admin Center → Voice → Resource accounts:

- [ ] Select the `CallTranskript Routing` resource account.
- [ ] Click **Assign / unassign**.
- [ ] Select **Phone number type**: *Direct Routing* (since SGB uses DR).
- [ ] **Assigned phone number**: enter the test DID from prerequisites
  in E.164 format (e.g., `+493411234567`).
- [ ] Save.

---

## Step 3 — Create the Call Queue

### 3a. New call queue

In Teams Admin Center → Voice → **Call queues** → **+ Add**.

- [ ] **Name**: `CallTranskript Inbound`
- [ ] **Resource accounts**: add `CallTranskript Routing` (this links
  the inbound DID to the queue)
- [ ] **Language**: German (de-DE)

### 3b. Greeting + music

- [ ] Greeting: skip / default for the pilot
- [ ] Music on hold: default

### 3c. Call answering

- [ ] **Call agents**: leave **empty** — we don't want Teams users
  to answer first. The whole point is to send the call to ACS, which
  will then transfer back to a Teams agent via `transferhandler.ts`.

  Wait — this is the tricky bit. Call queues normally need agents.
  If "no agents" isn't accepted, you have two options:
  
  - **Option A**: add the target agent as the sole agent, with
    `Routing method = Attendant routing`, and set the resource account
    as a "call routing" step that pre-processes before the agent.
    This may not work directly — Microsoft's docs on call queues
    routing to an Application Instance are sparse.
  - **Option B**: skip the call queue and use **Auto Attendant** with
    "Redirect call → External application" pointing at the ACS
    application instance directly. This is the cleaner path.

  **Recommended**: use **Auto Attendant** instead of Call Queue for
  this pilot (see Step 3 alternative below).

### 3 alternative — Auto Attendant: usually unnecessary

For a basic "screen + enrich every call" deployment, you **don't need
an AA**. The Resource Account with the bound App Instance is itself
the application endpoint. Skip this step and let calls flow directly
from the Resource Account's DID to ACS.

**You only need an AA if** you want greetings, business-hours menus,
IVR digit options, or after-hours fallback. In that case the topology
requires **two** Resource Accounts (one to front the AA, one bound to
the ACS App Instance) — pointing an AA at its own Resource Account
creates a routing loop.

The two-RA AA pattern:

- [ ] Create a *second* Resource Account, e.g., `aa-reception@sgb-energie.de`,
  with its own license + DID.
- [ ] Create Auto Attendant (Voice → Auto attendants → + Add):
  - **Resource accounts**: the second one (`aa-reception`)
  - **Call routing** → first action: *Redirect call*
  - **Redirect to**: *Voice app* or *Person in your organization*
    (depending on current UI label)
  - **Search**: `acs-calltranskript@sgb-energie.de` (the App Instance one)
- [ ] Save.

---

## Step 4 — Voice Routing (verify, don't break)

If the DID was previously routed to a real Teams user or another
queue, you've now stolen it. Verify nothing else upstream is racing
for the same number.

- [ ] In Teams Admin Center → Voice → Direct Routing → **SBCs** tab,
  confirm the SBC reachability is *Active*.
- [ ] In Voice → **Voice routing policies**, check if any policy has
  a rule matching this DID. If yes, the routing precedence determines
  what wins.
- [ ] In Voice → **Phone numbers**, search for the DID and confirm
  it's now assigned to the resource account (and nothing else).

---

## Step 5 — Wait + propagate

Microsoft 365 changes take time to propagate. **Wait 30-60 minutes**
after completing steps 1-4 before testing. Common symptom of testing
too early: call rings then drops with no app-side activity.

- [ ] 30 minutes elapsed since the last admin center save.

---

## Step 6 — End-to-end test

### 6a. Dry run from this side

Confirm the Azure side is still healthy:

```sh
curl https://app-calltranskript-backend.azurewebsites.net/health
# expect: 200 {"status":"healthy","activeCalls":0,...}
```

- [ ] `/health` returns 200.

### 6b. Place the test call

From any phone (not the resource account itself), dial the test DID.

- [ ] Call connects (you hear silence or the default greeting briefly).
- [ ] Call is answered automatically by ACS.

### 6c. Watch the pipeline (KQL queries)

In Azure portal → Log Analytics → `LogAnaliticsWorkspace` → Logs,
paste from `ops/dashboard.kql`. Within 60-90s of placing the call:

Query #1 (call volume) should show **incoming = 1, answered = 1**.

Query #2 (success rate) should show **successPct = 100**.

Query #4 (enrichment) should show **n = 1**, and if the caller is in
Entra or CRM, **entraHits ≥ 1** or **crmHits ≥ 1**.

- [ ] Events visible in App Insights with correct counts.

### 6d. Inspect a single call's full trace

Get the correlationId from the latest call_incoming event:

```kusto
AppEvents
| where TimeGenerated > ago(15m)
| where Name == "call_incoming"
| project correlationId = tostring(Properties.correlationId), TimeGenerated
| order by TimeGenerated desc
| take 1
```

Then paste the GUID into query #10 (per-call trace). You should see
the full lifecycle: `call_incoming` → `enrichment_completed` →
`call_answered` → (if there was speech) `suggestion_generated`...

- [ ] Full call trace visible.

### 6e. Verify dead-letter is empty

```sh
az storage blob list \
  --account-name stcallassist \
  --container-name eg-deadletter \
  --auth-mode login \
  --query "length(@)" -o tsv
# expect: 0
```

- [ ] Dead-letter container empty (no failed Event Grid deliveries).

---

## Step 7 — Rollback plan (if something goes wrong)

If a real call breaks or you need to revert:

1. **Quick rollback** — in Teams Admin Center → Voice → Resource accounts,
   unassign the phone number from `CallTranskript Routing`. The DID
   reverts to "unassigned" within ~5 minutes; route it back to its
   previous destination (user or queue).

2. **Full rollback** — additionally:
   - Delete the Auto Attendant `CallTranskript Routing` (Teams admin
     center).
   - Delete the Resource Account `acs-calltranskript@sgb-energie.de`.
   - Remove the application instance:
     ```powershell
     Remove-CsOnlineApplicationInstance -Identity "<OBJECT_ID>"
     ```

3. **Azure side**: untouched. Event Grid subscription stays but
   simply receives no events.

---

## Step 8 — Production rollout

Once the pilot DID works:

- [ ] Document the routing change in your change log.
- [ ] Repeat steps 2c (assign number) and 6 (test) for additional DIDs
  one at a time. Don't bulk-migrate.
- [ ] Set up Azure Monitor **alerts** on the KQL queries:
  - `call_answer_failed` rate > 10% over 15min
  - `enrichment_completed` p95 latency > 8s
  - Dead-letter container has new blobs
- [ ] Capacity check: ACS Call Automation has per-region concurrent
  call limits. Verify with `az communication regenerate-key`'s
  `quota` info or open a support case for SGB's expected volume.

---

## Open uncertainties (worth confirming with current Microsoft docs)

These bits change frequently and what's written here is best-effort
as of 2026-05-18:

1. **App Instance ↔ ACS linking via UPN match** — verify this is still
   how Teams links the resource account to the ACS app. Recent guidance
   sometimes uses ObjectId-based linking instead.

2. **Auto Attendant "Redirect to External application" step** — this
   menu item exists but its appearance/path varies by Teams admin
   center version. If you don't see it, look for "Redirect call" →
   "External application" or "Person in your organization" with the
   resource account UPN.

3. **ACS app permissions** — some flows require granting Graph
   permissions (`Calls.AccessMedia.All`, `Calls.JoinGroupCall.All`,
   `Calls.InitiateGroupCall.All`) to the ACS service principal. Our
   Entra app `79df50a8-...` (CallTranskript-GraphAPI) currently has
   `User.Read.All` and `Directory.Read.All` only. The Call Automation
   docs are inconsistent about whether *additional* permissions are
   needed for the Teams routing path vs the call answering itself.

   If you see permission errors during testing, add those three
   `Calls.*` permissions and re-grant admin consent.

4. **Resource Account license vs ACS app credit model** — some
   Microsoft docs imply you don't need the Phone Resource Account
   license when using ACS-direct, only when routing through Teams
   admin objects. We're definitely doing the latter, so the license
   is required.
