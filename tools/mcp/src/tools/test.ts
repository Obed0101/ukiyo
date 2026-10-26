import { join } from "node:path";
import { dotnetCommand, runProcess, tail } from "../engine.ts";
import { text, type Tool } from "../protocol.ts";

const test: Tool = {
  name: "ukiyo_test",
  title: "Run tests",
  description:
    "Runs the engine's C# tests (xunit, tests/Ukiyo.Tests) and/or the JS adapter tests (bun, web/three-adapter). Use a filter for the area you changed, e.g. \"FullyQualifiedName~Physics\". Reports pass/fail with the tail of the log.",
  inputSchema: {
    type: "object",
    additionalProperties: false,
    properties: {
      suite: { type: "string", enum: ["dotnet", "js", "all"], default: "dotnet" },
      filter: { type: "string", maxLength: 300, description: "dotnet test --filter expression (dotnet suite only)." },
      timeoutSeconds: { type: "integer", minimum: 10, maximum: 1800, default: 600 },
    },
  },
  async run(args, { root, signal, log }) {
    const results: { suite: string; passed: boolean; exitCode: number; ms: number; tail: string }[] = [];
    const timeoutMs = Number(args.timeoutSeconds) * 1000;
    if (args.suite === "dotnet" || args.suite === "all") {
      const { command, env } = dotnetCommand();
      const cli = ["test", join(root, "tests/Ukiyo.Tests"), "--nologo"];
      if (typeof args.filter === "string" && args.filter) cli.push("--filter", args.filter);
      log("info", "test.dotnet", { filter: args.filter });
      const r = await runProcess(command, cli, { cwd: root, env, timeoutMs, signal });
      results.push({ suite: "dotnet", passed: r.code === 0 && !r.timedOut, exitCode: r.code, ms: r.ms, tail: tail(`${r.stdout}\n${r.stderr}`, 30) });
    }
    if (args.suite === "js" || args.suite === "all") {
      const r = await runProcess("bun", ["test"], { cwd: join(root, "web/three-adapter"), timeoutMs, signal });
      results.push({ suite: "js", passed: r.code === 0 && !r.timedOut, exitCode: r.code, ms: r.ms, tail: tail(`${r.stdout}\n${r.stderr}`, 30) });
    }
    const passed = results.every((r) => r.passed);
    return {
      isError: !passed,
      content: results.map((r) => text(`[${r.suite}] ${r.passed ? "PASS" : "FAIL"} exit=${r.exitCode} ${r.ms}ms\n${r.tail}`)),
      structuredContent: { passed, results: results.map(({ tail: _tail, ...rest }) => rest) },
    };
  },
};

export default test;
