// What games the Studio launched print (dotnet build output, the game's stdout and stderr), kept for the Console panel.
// A fixed-size ring: old lines fall off, so a game that logs every tick cannot grow the Studio's memory.

export type ConsoleLine = {
  /** Increases by one per line for the life of the buffer; clients ask for what came after the last one they saw. */
  seq: number;
  at: string;
  /** The game's name ("LanternRun"). */
  source: string;
  stream: "stdout" | "stderr";
  text: string;
};

export type ConsolePage = {
  lines: ConsoleLine[];
  /** The newest seq in the buffer (0 when nothing was ever written). Pass it back as `since`. */
  last: number;
  /** Lines the client asked for that had already fallen off the ring. */
  dropped: number;
};

export const CONSOLE_CAPACITY = 2000;
export const CONSOLE_LINE_CHARS = 2000;

export class ConsoleBuffer {
  private readonly lines: ConsoleLine[] = [];
  private seq = 0;

  constructor(private readonly capacity = CONSOLE_CAPACITY) {
    if (!Number.isInteger(capacity) || capacity < 1) throw new RangeError("[CONSOLE]: capacity must be a positive integer");
  }

  push({ source, stream, text, at = new Date().toISOString() }: { source: string; stream: ConsoleLine["stream"]; text: string; at?: string }): ConsoleLine {
    const line: ConsoleLine = { seq: ++this.seq, at, source, stream, text: text.length > CONSOLE_LINE_CHARS ? `${text.slice(0, CONSOLE_LINE_CHARS)}…` : text };
    this.lines.push(line);
    if (this.lines.length > this.capacity) this.lines.splice(0, this.lines.length - this.capacity);
    return line;
  }

  get size(): number {
    return this.lines.length;
  }

  /** Lines with seq greater than `since`, oldest first, at most `limit` (the newest ones when more are waiting). */
  after(since = 0, limit = this.capacity): ConsolePage {
    const fresh = this.lines.filter((line) => line.seq > since);
    const oldest = this.lines[0]?.seq ?? this.seq + 1;
    const dropped = Math.max(0, oldest - since - 1);
    return { lines: fresh.slice(-Math.max(1, limit)), last: this.seq, dropped: since > 0 ? dropped : 0 };
  }

  clear(): void {
    this.lines.length = 0;
  }
}
