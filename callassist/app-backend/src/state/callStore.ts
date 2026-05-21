import { CallState } from "../../../shared/types/callState";

const store = new Map<string, CallState>();

export const callStore = {
  set:    (id: string, s: CallState) => store.set(id, s),
  get:    (id: string) => store.get(id),
  update: (id: string, patch: Partial<CallState>) => {
    const s = store.get(id);
    if (s) store.set(id, { ...s, ...patch });
  },
  delete: (id: string) => store.delete(id),
  size:   () => {
    let n = 0;
    for (const s of store.values()) if (s.phase !== "ended") n += 1;
    return n;
  },
  total:  () => store.size,
};
