// Gemini native image generation (models/<model>:generateContent). The image arrives as inlineData in a content part.
import { spriteConfig } from "../config.ts";
import { errorText, ProviderError, withRetry, type ImageProvider } from "./types.ts";

type Part = { text?: string; inlineData?: { mimeType?: string; data?: string } };

const gemini: ImageProvider = {
  name: "gemini",
  available: () => Boolean(process.env[spriteConfig().providers.gemini.keyEnv]),
  async generate({ prompt, signal }) {
    const config = spriteConfig().providers.gemini;
    const model = process.env.UKIYO_GEMINI_IMAGE_MODEL ?? config.model;
    const key = process.env[config.keyEnv];
    if (!key) throw new ProviderError("gemini", `${config.keyEnv} is not set`);
    return withRetry("gemini", async () => {
      const response = await fetch(config.endpoint.replace("{model}", encodeURIComponent(model)), {
        method: "POST",
        signal,
        headers: { "x-goog-api-key": key, "Content-Type": "application/json" },
        body: JSON.stringify({
          contents: [{ role: "user", parts: [{ text: prompt }] }],
          generationConfig: { responseModalities: ["TEXT", "IMAGE"] },
        }),
      });
      if (!response.ok) throw new ProviderError("gemini", `${response.status} ${await errorText(response)}`, response.status);
      const body = (await response.json()) as { candidates?: { content?: { parts?: Part[] }; finishReason?: string }[] };
      const parts = body.candidates?.[0]?.content?.parts ?? [];
      const image = parts.find((p) => p.inlineData?.data);
      if (!image?.inlineData?.data) {
        const said = parts.map((p) => p.text).filter(Boolean).join(" ").slice(0, 300);
        throw new ProviderError("gemini", `no image in the response (finishReason ${body.candidates?.[0]?.finishReason ?? "unknown"})${said ? `: ${said}` : ""}`, 422);
      }
      return { png: new Uint8Array(Buffer.from(image.inlineData.data, "base64")), provider: "gemini", model };
    }, signal);
  },
};

export default gemini;
