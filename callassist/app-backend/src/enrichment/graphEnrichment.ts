import { ClientSecretCredential } from "@azure/identity";
import { Client } from "@microsoft/microsoft-graph-client";
import { TokenCredentialAuthenticationProvider } from "@microsoft/microsoft-graph-client/authProviders/azureTokenCredentials";
import { EntraProfile } from "../../../shared/types/callerInfo";
import { NormalizedPhone } from "../../../shared/utils/phoneNormalizer";
import { config } from "../config/config";

let _client: Client | null = null;

function getGraphClient(): Client {
  if (!_client) {
    const cred = new ClientSecretCredential(
      config.entra.tenantId,
      config.entra.clientId,
      config.entra.clientSecret
    );
    const auth = new TokenCredentialAuthenticationProvider(cred, {
      scopes: ["https://graph.microsoft.com/.default"],
    });
    _client = Client.initWithMiddleware({ authProvider: auth });
  }
  return _client;
}

export async function lookupCallerInEntra(
  phone: NormalizedPhone
): Promise<EntraProfile | null> {
  const client = getGraphClient();
  const select = "id,displayName,mail,jobTitle,department,companyName,mobilePhone";
  const queries = [
    `mobilePhone eq '${phone.e164}'`,
    `mobilePhone eq '${phone.national}'`,
    `businessPhones/any(p:p eq '${phone.e164}')`,
  ];

  for (const filter of queries) {
    try {
      const r = await client
        .api("/users")
        .filter(filter)
        .select(select)
        .expand("manager($select=displayName)")
        .top(1)
        .get();

      if (r.value?.length) {
        const u = r.value[0];
        return {
          id:          u.id,
          displayName: u.displayName,
          mail:        u.mail ?? null,
          jobTitle:    u.jobTitle ?? null,
          department:  u.department ?? null,
          companyName: u.companyName ?? null,
          manager:     u.manager?.displayName ?? null,
        };
      }
    } catch {
      continue;
    }
  }
  return null;
}