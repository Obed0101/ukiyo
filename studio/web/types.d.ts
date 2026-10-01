// Types for panel authors. Panels are plain JS modules checked with // @ts-check and JSDoc against these.

export type LiveSession = { pid: number; port: number; game: string; target: string; startedAt: string };

export type GameStatus = {
  game: string;
  target: string;
  tick: number;
  paused: boolean;
  droppedTicks: number;
  renderer: string;
  backend: string;
  capture: boolean;
  width: number;
  height: number;
};

export type EntitySnapshot = { position: [number, number, number]; rotation: [number, number, number, number]; scale: [number, number, number] };

export type GameSnapshot = {
  tick: number;
  renderer: string;
  backend: string;
  source: Record<string, string>;
  entities: Record<string, EntitySnapshot>;
  values?: Record<string, number>;
};

/** A real frame read back from the game's renderer (GPU on desktop). */
export type Frame = { tick: number; width: number; height: number; backend: string; url: string; pid: number };

export type LogEntry = { at: string; level: "info" | "success" | "warn" | "error"; source: string; text: string };

export type Notice = { text: string; level: "info" | "success" | "warn" | "error"; from: string; at: string };

export type StudioEvents = {
  session: LiveSession | null;
  sessions: LiveSession[];
  status: GameStatus | null;
  snapshot: GameSnapshot | null;
  frame: Frame | null;
  log: LogEntry;
  notice: Notice;
};

export type BridgeCommand = "status" | "pause" | "resume" | "step" | "snapshot" | "capture" | "input" | "record";

export type InputEvent = { key?: string; button?: "Left" | "Right" | "Middle"; down?: boolean; pointer?: [number, number] };

export type CompositionPatch = {
  layout?: string;
  theme?: string;
  hide?: string[];
  show?: string[];
  panels?: Record<string, Record<string, unknown> | null>;
};

/** What every panel receives. Everything a panel does goes through here: it never talks to the server directly. */
export type Studio = {
  /** This panel's id (its file name) and its options from studio.json → panels.<id>. */
  readonly id: string;
  readonly options: Record<string, unknown>;
  /** Header slot for this panel's buttons. */
  readonly actions: HTMLElement;
  readonly h: typeof import("./dom.js").h;
  readonly icon: typeof import("./icons.js").icon;
  readonly session: LiveSession | null;
  readonly sessions: LiveSession[];
  readonly status: GameStatus | null;
  readonly snapshot: GameSnapshot | null;
  readonly frame: Frame | null;
  /** Sends one dev bridge command to the selected game. Rejects with the bridge's error message. */
  call(cmd: BridgeCommand, args?: { ticks?: number; events?: InputEvent[]; on?: boolean }): Promise<unknown>;
  /** Pause/resume/step through these so every panel sees the new status and snapshot. */
  control(cmd: "pause" | "resume" | "step", ticks?: number): Promise<void>;
  capture(): Promise<Frame | null>;
  refreshSnapshot(): Promise<void>;
  selectSession(pid: number | null): void;
  on<K extends keyof StudioEvents>(event: K, handler: (value: StudioEvents[K]) => void): () => void;
  /** Tells agents what the person is looking at in this panel (merged into ukiyo_studio view). */
  view(patch: Record<string, unknown>): void;
  /** Changes studio.json (layout, theme, panels shown, panel options). Agents see the same file. */
  compose(patch: CompositionPatch): Promise<void>;
  log(level: LogEntry["level"], text: string): void;
  /** Log entries since the page opened (last 500), oldest first. */
  readonly history: LogEntry[];
  readonly manifest: { panels: { id: string }[]; layouts: Record<string, { description: string; panels: string[] }>; themes: Record<string, { description: string }>; composition: Required<CompositionPatch> };
  /** Titles of every loaded panel module, for menus. */
  panelTitle(id: string): string;
};

export type PanelModule = {
  /** Must equal the file name: panels/<id>.js. */
  id: string;
  title: string;
  /** Name from studio/web/icons.js. */
  icon: string;
  description: string;
  /** Builds the panel inside root. Returns a cleanup that undoes timers and listeners not registered through studio.on. */
  mount(root: HTMLElement, studio: Studio): void | (() => void);
};
