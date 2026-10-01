// @ts-check
// Client of the Studio server API. Every call carries the page's token (see studio/src/guard.ts).

export class StudioError extends Error {
  /** @param {string} message @param {number} status */
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

/** @param {string} token */
export function createApi(token) {
  /** @param {string} path @param {unknown} [body] */
  async function request(path, body) {
    const response = await fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: { "x-ukiyo-token": token, ...(body === undefined ? {} : { "content-type": "application/json" }) },
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: "no-store",
    });
    let payload;
    try {
      payload = await response.json();
    } catch {
      throw new StudioError(`${path}: ${response.status} with no JSON body`, response.status);
    }
    if (!response.ok) throw new StudioError(payload?.error ?? `${path}: ${response.status}`, response.status);
    return payload;
  }

  return {
    get: (/** @type {string} */ path) => request(path),
    post: (/** @type {string} */ path, /** @type {unknown} */ body) => request(path, body ?? {}),
    events: () => new EventSource(`/api/events?token=${encodeURIComponent(token)}`),
  };
}
