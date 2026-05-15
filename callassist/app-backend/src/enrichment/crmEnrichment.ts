// crmEnrichment.ts — SGB CRM Integration
// Endpoint: GET https://crm.sgb-energie.de/api/public-partner/lookup-by-phone
// Auth:     Authorization: Bearer <jwt>  (Scope: partner.lookup)
// API key:  https://crm.sgb-energie.de/verwaltung/api-keys
//
// Key facts from API owner (Konrad):
// - Phone format does NOT matter — API normalizes internally
//   +49 341 1234567, 0341-1234567, +493411234567 all match the same record
// - NOT FOUND returns 200 + { data: [] } — never 404
// - INVALID NUMBER returns 400
// - Only send: phone + optional includeMobile — extra params cause 400
// - Rate limit: 60 req/min per IP
// - includeMobile default: true (matches TELEFON + MOBIL)
// - type field: "partner" (has partner_number) or "kunde" (has customer_number)

import axios, { AxiosError } from "axios";
import { CrmProfile } from "../../../shared/types/callerInfo";
import { config } from "../config/config";
import { NormalizedPhone } from "../../../shared/utils/phoneNormalizer";
import { withRetry } from "../utils/retry";

// SGB CRM raw response shape
interface SgbPartner {
  type:            "partner" | "kunde";
  db_id:           number;
  partner_number:  string | null;
  customer_number: string | null;
  first_name:      string | null;
  last_name:       string | null;
  company:         string | null;
  street:          string | null;
  street_number:   string | null;
  zip:             string | null;
  city:            string | null;
  email:           string | null;
  matched_phone:   string;
}

interface SgbSearchResponse {
  data: SgbPartner[];
  pagination: {
    page:        number;
    limit:       number;
    total:       number;
    total_pages: number;
  };
}

export async function lookupCallerInCrm(
  phone: NormalizedPhone
): Promise<CrmProfile | null> {

  try {
    const response = await withRetry(
      () => axios.get<SgbSearchResponse>(
        `${config.crm.baseUrl}/api/public-partner/lookup-by-phone`,
        {
          params: { phone: phone.e164 },
          headers: {
            Authorization: `Bearer ${config.crm.apiKey}`,
            Accept: "application/json",
          },
          timeout: 3000, // 3s max — must not delay call answer
        },
      ),
      {
        attempts:    2,
        baseDelayMs: 200,
        shouldRetry: (err) => {
          if (!(err instanceof AxiosError)) return false;
          if (!err.response) return true; // network/timeout
          return err.response.status >= 500 && err.response.status < 600;
        },
      },
    );

    const { data, pagination } = response.data;

    // Not found — API returns 200 + empty array
    if (!data || pagination.total === 0 || data.length === 0) {
      console.log("CRM lookup — no match", { e164: phone.e164 });
      return null;
    }

    const partner = data[0];
    console.log("CRM lookup hit", {
      e164:          phone.e164,
      matched_phone: partner.matched_phone,
      db_id:         partner.db_id,
      type:          partner.type,
    });

    return mapPartnerToCrmProfile(partner);

  } catch (err) {
    if (err instanceof AxiosError) {
      if (err.code === "ECONNABORTED") {
        console.warn("CRM lookup timeout", { e164: phone.e164 });
        return null;
      }
      if (err.response?.status === 400) {
        console.warn("CRM lookup — invalid phone number (400)", { e164: phone.e164 });
        return null;
      }
      if (err.response?.status === 401 || err.response?.status === 403) {
        console.error("CRM auth failure — check API key scope (partner.lookup) in Key Vault", {
          status: err.response.status,
        });
        return null;
      }
      if (err.response?.status === 429) {
        console.warn("CRM rate limit hit (60 req/min)", { e164: phone.e164 });
        return null;
      }
    }
    console.error("CRM lookup unexpected error", {
      e164:  phone.e164,
      error: err instanceof Error ? err.message : String(err),
    });
    return null;
  }
}

function mapPartnerToCrmProfile(p: SgbPartner): CrmProfile {
  const fullName = [p.first_name, p.last_name].filter(Boolean).join(" ") || null;

  const addressParts = [
    p.street && p.street_number ? `${p.street} ${p.street_number}` : p.street,
    [p.zip, p.city].filter(Boolean).join(" "),
  ].filter(Boolean);

  const externalId = p.type === "partner"
    ? p.partner_number
    : p.customer_number;

  return {
    customerId:      String(p.db_id),
    fullName:        fullName || p.company || null,
    accountName:     p.company || null,
    openTickets:     null,
    lastContactDate: null,
    notes: addressParts.length
      ? `${p.type === "partner" ? "Partner" : "Kunde"} · ${externalId ?? ""} · ${addressParts.join(", ")}`
      : null,
    rawData: p as unknown as Record<string, unknown>,
  };
}