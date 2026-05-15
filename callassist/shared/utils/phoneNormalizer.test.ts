import { describe, it, expect } from "vitest";
import { normalizePhone } from "./phoneNormalizer";

describe("normalizePhone", () => {
  it("normalizes a German E.164 number", () => {
    const r = normalizePhone("+493411234567");
    expect(r).not.toBeNull();
    expect(r?.e164).toBe("+493411234567");
  });

  it("normalizes a German national number with formatting", () => {
    const r = normalizePhone("0341 1234567");
    expect(r?.e164).toBe("+493411234567");
  });

  it("strips spaces, dashes, parens, dots", () => {
    const r = normalizePhone("(0341) 123-4567.0");
    expect(r?.e164).toBe("+4934112345670");
  });

  it("returns null for invalid input", () => {
    expect(normalizePhone("not a phone")).toBeNull();
    expect(normalizePhone("")).toBeNull();
    expect(normalizePhone("12")).toBeNull();
  });

  it("supports AT country override", () => {
    const r = normalizePhone("06641234567", "AT");
    expect(r?.e164.startsWith("+43")).toBe(true);
  });

  it("supports CH country override", () => {
    const r = normalizePhone("0791234567", "CH");
    expect(r?.e164.startsWith("+41")).toBe(true);
  });

  it("returns national and significant alongside e164", () => {
    const r = normalizePhone("+493411234567");
    expect(r?.national).toMatch(/0341/);
    expect(r?.significant).toBe("3411234567");
  });
});
