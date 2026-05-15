import { CallAutomationClient } from "@azure/communication-call-automation";
import { config } from "../config/config";

let _client: CallAutomationClient | null = null;

export function getAcsClient(): CallAutomationClient {
  return _client ??= new CallAutomationClient(config.acs.connectionString);
}