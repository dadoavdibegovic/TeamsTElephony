import { parsePhoneNumber, isValidPhoneNumber } from "libphonenumber-js";

export interface NormalizedPhone {
  e164:        string;
  national:    string;
  significant: string;
}

export function normalizePhone(
  raw: string,
  country: "DE" | "AT" | "CH" = "DE"
): NormalizedPhone | null {
  try {
    const cleaned = raw.replace(/[\s\-\(\)\.]/g, "");
    if (!isValidPhoneNumber(cleaned, country)) return null;
    const p = parsePhoneNumber(cleaned, country);
    return {
      e164:        p.format("E.164"),
      national:    p.formatNational(),
      significant: p.nationalNumber,
    };
  } catch {
    return null;
  }
}