import { normalizePhone } from "../../../shared/utils/phoneNormalizer";
import { lookupCallerInEntra } from "./graphEnrichment";
import { lookupCallerInCrm } from "./crmEnrichment";
import { CallerInfo } from "../../../shared/types/callerInfo";

export interface EnrichmentResult {
  success:    boolean;
  callerInfo: CallerInfo;
  durationMs: number;
}

export async function enrichCaller(rawPhone: string): Promise<EnrichmentResult> {
  const start = Date.now();
  const phone = normalizePhone(rawPhone);

  if (!phone) {
    return {
      success: false,
      callerInfo: {
        phoneNumber: rawPhone,
        displayName: null,
        entra:       null,
        crm:         null,
        enrichedAt:  new Date(),
      },
      durationMs: 0,
    };
  }

  // Both run simultaneously — one failing never blocks the other
  const [entraR, crmR] = await Promise.allSettled([
    lookupCallerInEntra(phone),
    lookupCallerInCrm(phone),
  ]);

  const entra = entraR.status === "fulfilled" ? entraR.value : null;
  const crm   = crmR.status   === "fulfilled" ? crmR.value   : null;

  return {
    success: true,
    callerInfo: {
      phoneNumber: phone.e164,
      displayName: entra?.displayName ?? crm?.fullName ?? null,
      entra,
      crm,
      enrichedAt:  new Date(),
    },
    durationMs: Date.now() - start,
  };
}