// SPIKE (UKIYO-15, docs/decisions/webgpu-browser.md): wraps either adapter with timers so both paths are measured
// the same way. scripts/spike-webgpu.ts maps the shared host's "./ukiyo-three.js" here with ?impl=webgl|webgpu.
// "?orig" gives the shipped adapter a URL the import map does not remap.
const impl = new URL(import.meta.url).searchParams.get("impl");
const started = performance.now();
const adapter = impl === "webgpu" ? await import("/spike/ukiyo-three-webgpu.js") : await import("/lib/ukiyo-three.js?orig");
const timings = (globalThis.__ukiyoSpikeTimings = { impl, moduleLoadMs: performance.now() - started, initializeMs: 0, firstRenderMs: 0, renders: 0, renderTotalMs: 0, captures: 0, captureTotalMs: 0 });

const timed = (fn, record) => (...args) => {
  const t = performance.now();
  const result = fn(...args);
  record(performance.now() - t);
  return result;
};

export const initialize = timed(adapter.initialize, (ms) => { timings.initializeMs = ms; });
export const applyResources = adapter.applyResources;
export const resize = adapter.resize;
export const render = timed(adapter.render, (ms) => {
  if (timings.renders === 0) timings.firstRenderMs = ms;
  timings.renders++;
  timings.renderTotalMs += ms;
});
export const capture = timed(adapter.capture, (ms) => { timings.captures++; timings.captureTotalMs += ms; });
export const dispose = adapter.dispose;
