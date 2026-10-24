import { describe, expect, test } from "bun:test";
import { ConsoleBuffer, CONSOLE_LINE_CHARS } from "../src/console-log.ts";

const line = (text: string) => ({ source: "Game", stream: "stdout" as const, text });

describe("console ring buffer", () => {
  test("never holds more than its capacity and drops the oldest first", () => {
    const buffer = new ConsoleBuffer(5);
    for (let i = 1; i <= 12; i++) buffer.push(line(`l${i}`));
    expect(buffer.size).toBe(5);
    expect(buffer.after(0).lines.map((l) => l.text)).toEqual(["l8", "l9", "l10", "l11", "l12"]);
  });

  test("a client that polls with the last seq gets only what is new, and learns about dropped lines", () => {
    const buffer = new ConsoleBuffer(3);
    for (let i = 1; i <= 3; i++) buffer.push(line(`l${i}`));
    const first = buffer.after(0);
    expect(first.last).toBe(3);
    expect(buffer.after(first.last).lines).toEqual([]);
    for (let i = 4; i <= 9; i++) buffer.push(line(`l${i}`));
    const next = buffer.after(first.last);
    expect(next.lines.map((l) => l.text)).toEqual(["l7", "l8", "l9"]);
    expect(next.dropped).toBe(3);
  });

  test("seq keeps counting after clear and a very long line is cut", () => {
    const buffer = new ConsoleBuffer(3);
    buffer.push(line("a"));
    buffer.clear();
    const kept = buffer.push(line("x".repeat(CONSOLE_LINE_CHARS + 50)));
    expect(kept.seq).toBe(2);
    expect(kept.text.length).toBe(CONSOLE_LINE_CHARS + 1);
    expect(buffer.size).toBe(1);
  });

  test("rejects a capacity that is not a positive integer", () => {
    expect(() => new ConsoleBuffer(0)).toThrow(RangeError);
  });
});
