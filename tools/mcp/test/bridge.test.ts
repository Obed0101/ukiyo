import { expect, test } from "bun:test";
import { createServer } from "node:net";
import { bridgeRequest, type LiveSession } from "../src/bridge.ts";

test("bridge rejects an early disconnect without waiting for its timeout", async () => {
  const server = createServer(socket => socket.end('{"ok":'));
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  try {
    const address = server.address() as { port: number };
    const session = { port: address.port, pid: process.pid, game: "fixture", token: "test-only" } as LiveSession;
    await expect(bridgeRequest(session, { cmd: "status" }, 30000)).rejects.toThrow("connection closed before a complete response");
  } finally { server.close(); }
}, 2000);

test("bridge accepts a response split across TCP writes", async () => {
  const server = createServer(socket => socket.once("data", () => {
    socket.write('{"ok":true,');
    socket.end('"result":{"tick":42}}\n');
  }));
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  try {
    const address = server.address() as { port: number };
    const session = { port: address.port, pid: process.pid, game: "fixture", token: "test-only" } as LiveSession;
    expect(await bridgeRequest(session, { cmd: "status" })).toEqual({ ok: true, result: { tick: 42 } });
  } finally { server.close(); }
});
