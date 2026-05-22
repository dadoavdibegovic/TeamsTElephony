# Azure RBAC for the bot agent

Since both agents share Adnan's `az login` session, the bot agent
inherits Owner on the subscription. That's broader than necessary
but acceptable for a 2-agent setup with a known-trusted second
agent.

For when a real third-party human contractor takes this on later,
this is the minimal RBAC they'd actually need:

## Minimum scope for an external .NET contractor

If you later replace the bot agent with a real human:

### Subscription-level (none)

Don't grant subscription-wide roles to external contractors.

### Resource group `Call_Transkript_Infra`

| Role | Why |
|---|---|
| **Reader** | See existing resources, read App Service configs, view bot service registration |
| **App Service Contributor** scoped to their new `app-bot-calltranskript` resource | Create + deploy their own App Service |
| **Application Insights Contributor** scoped to `appi-calltranskript` | Wire telemetry from their bot |

### Key Vault `kv-calltranskript-prod`

| Role | Why |
|---|---|
| **Key Vault Secrets User** | Read `BotClientSecret` and `BackendIngestSecret` at runtime |

Note: this is RBAC mode (we checked earlier). The MI of their bot's
App Service needs `Key Vault Secrets User`, not the human contractor's
identity. Make this clear in the engagement.

### Azure Bot Service `bot-calltranskript-prod`

| Role | Why |
|---|---|
| **Reader** | View bot config |
| (no write needed — Adnan runs the one `az bot msteams create` to update the webhook) |

### Entra ID

No role needed. The bot's Entra app and its admin-consented permissions
are already done. The contractor doesn't manage Entra.

## How to grant (when needed)

Get the contractor's Entra user object ID:

```sh
az ad user show --id "<contractor-upn>" --query id -o tsv
```

Grant scoped roles:

```sh
RG="/subscriptions/0c3d1568-2bf4-4eb6-b037-1b94eb8b5061/resourceGroups/Call_Transkript_Infra"
USER="<their-object-id>"

az role assignment create --assignee $USER --role Reader --scope $RG
az role assignment create --assignee $USER --role "Key Vault Secrets User" \
  --scope "$RG/providers/Microsoft.KeyVault/vaults/kv-calltranskript-prod"
```

For the App Service the contractor will create themselves, they get
Contributor when they create it (creator gets Owner of the new
resource). No explicit role needed.

## Revocation

When the engagement ends:

```sh
az role assignment delete --assignee <their-object-id> --scope <RG-id>
```

And rotate `BackendIngestSecret` so they can't reuse it:

```sh
NEW=$(node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))")
az keyvault secret set --vault-name kv-calltranskript-prod --name BackendIngestSecret --value "$NEW"
az webapp restart -g Call_Transkript_Infra -n app-calltranskript-backend
unset NEW
```

## Current state (bot agent = Claude, no real RBAC needed)

The Claude bot agent uses Adnan's shared `az login` so nothing extra
to provision. The RBAC plan above is documentation for the future
human-contractor scenario.
