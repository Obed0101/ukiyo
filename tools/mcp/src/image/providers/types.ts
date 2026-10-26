export type GenerateRequest = { prompt: string; seed: number; signal: AbortSignal };

export type GeneratedImage = { png: Uint8Array; provider: string; model: string; revisedPrompt?: string };

/** One file per provider in this folder. The key is read from the environment and never logged or returned. */
export type ImageProvider = {
  name: string;
  available: () => boolean;
  generate: (request: GenerateRequest) => Promise<GeneratedImage>;
};

export class ProviderError extends Error {
  constructor(provider: string, message: string, readonly status?: number) {
    super(`[IMAGE:${provider}]: ${message}`);
  }
}

/** Retries rate limits and server errors with exponential backoff and jitter; client errors fail at once. */
export async function withRetry<T>(provider: string, attempt: () => Promise<T>, signal: AbortSignal, tries = 4): Promise<T> {
  let delay = 1500;
  for (let i = 1; ; i++) {
    try {
      return await attempt();
    } catch (error) {
      const status = error instanceof ProviderError ? error.status : undefined;
      const retriable = status === undefined || status === 429 || status >= 500;
      if (!retriable || i >= tries || signal.aborted) throw error;
      await new Promise((resolve) => setTimeout(resolve, delay + Math.floor(Math.random() * 500)));
      delay *= 2;
    }
  }
}

/** Error text from a provider, trimmed, with anything that looks like a key removed. */
export async function errorText(response: Response): Promise<string> {
  const body = await response.text().catch(() => "");
  return body.replace(/(sk-|AIza)[A-Za-z0-9_-]{8,}/g, "[redacted]").slice(0, 600);
}
