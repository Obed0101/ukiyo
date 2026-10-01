// OpenAI Images API (POST /v1/images/generations). GPT image models always return base64 (b64_json); no URL mode.
import { spriteConfig } from "../config.ts";
import { errorText, ProviderError, withRetry, type ImageProvider } from "./types.ts";

const openai: ImageProvider = {
  name: "openai",
  available: () => Boolean(process.env[spriteConfig().providers.openai.keyEnv]),
  async generate({ prompt, signal }) {
    const config = spriteConfig().providers.openai;
    const model = process.env.UKIYO_OPENAI_IMAGE_MODEL ?? config.model;
    const key = process.env[config.keyEnv];
    if (!key) throw new ProviderError("openai", `${config.keyEnv} is not set`);
    return withRetry("openai", async () => {
      const response = await fetch(config.endpoint, {
        method: "POST",
        signal,
        headers: { Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
        body: JSON.stringify({ model, prompt, size: config.size ?? "1024x1024", n: 1, output_format: "png" }),
      });
      if (!response.ok) throw new ProviderError("openai", `${response.status} ${await errorText(response)}`, response.status);
      const body = (await response.json()) as { data?: { b64_json?: string; revised_prompt?: string }[] };
      const image = body.data?.[0];
      if (!image?.b64_json) throw new ProviderError("openai", "response has no image data");
      return { png: new Uint8Array(Buffer.from(image.b64_json, "base64")), provider: "openai", model, revisedPrompt: image.revised_prompt };
    }, signal);
  },
};

export default openai;
