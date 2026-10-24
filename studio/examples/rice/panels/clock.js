// @ts-check
/** @type {import("../../../web/types").PanelModule} */
export default {
  id: "clock",
  title: "Clock",
  icon: "activity",
  description: "Wall-clock time. Option: panels.clock.format = \"24h\" | \"12h\".",
  mount(root, studio) {
    const { h } = studio;
    const readout = h("p", { class: "readout" });
    root.append(readout);
    const tick = () => {
      const twelve = studio.options.format === "12h";
      readout.textContent = new Date().toLocaleTimeString([], { hour12: twelve });
    };
    tick();
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  },
};
