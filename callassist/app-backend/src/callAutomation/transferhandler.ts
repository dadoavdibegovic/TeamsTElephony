import { getAcsClient } from "./acsClient";
import { callStore } from "./callStore";
import { MicrosoftTeamsUserIdentifier } from "@azure/communication-common";

export async function transferCallToTeams(
  correlationId: string,
  teamsUserId:   string
): Promise<void> {
  const state = callStore.get(correlationId);
  if (!state?.callConnectionId) {
    throw new Error(`No active call found for correlationId: ${correlationId}`);
  }

  const client = getAcsClient();
  const conn   = client.getCallConnection(state.callConnectionId);

  const target: MicrosoftTeamsUserIdentifier = {
    microsoftTeamsUserId: teamsUserId,
    cloud:                "public",
    isAnonymous:          false,
  };

  await conn.transferCallToParticipant(target);

  callStore.update(correlationId, {
    phase:           "transferring",
    assignedAgentId: teamsUserId,
  });

  console.log("Transfer initiated", { correlationId, teamsUserId });
}