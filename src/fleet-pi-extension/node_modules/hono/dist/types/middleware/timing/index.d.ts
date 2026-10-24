import { TimingVariables, endTime, setMetric, startTime, timing, wrapTime } from "./timing.js";
//#region src/middleware/timing/index.d.ts
declare module '../..' {
  interface ContextVariableMap extends TimingVariables {}
}
//#endregion
export { type TimingVariables, endTime, setMetric, startTime, timing, wrapTime };
export {};
