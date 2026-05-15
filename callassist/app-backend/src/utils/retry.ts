export interface RetryOptions {
  attempts:   number;
  baseDelayMs: number;
  shouldRetry?: (err: unknown) => boolean;
}

export async function withRetry<T>(
  fn:  () => Promise<T>,
  opts: RetryOptions,
): Promise<T> {
  const { attempts, baseDelayMs, shouldRetry } = opts;
  let lastErr: unknown;
  for (let i = 0; i < attempts; i += 1) {
    try {
      return await fn();
    } catch (err) {
      lastErr = err;
      if (i === attempts - 1) break;
      if (shouldRetry && !shouldRetry(err)) break;
      const delay = baseDelayMs * Math.pow(2, i);
      await new Promise((r) => setTimeout(r, delay));
    }
  }
  throw lastErr;
}
