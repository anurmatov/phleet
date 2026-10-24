import { ParamIndexMap, Router } from "../../router.js";
import { MatcherMap, match } from "./matcher.js";
//#region src/router/reg-exp-router/prepared-router.d.ts
type RelocateMap = Record<string, ([(number | string)[], ParamIndexMap] | [(number | string)[]])[]>;
export declare class PreparedRegExpRouter<T> implements Router<T> {
  
  name: string;
  constructor(matchers: MatcherMap<T>, relocateMap: RelocateMap);
  add(method: string, path: string, handler: T): void;
  protected buildAllMatchers(): MatcherMap<T>;
  match: typeof match<Router<T>, T>;
}
export declare const buildInitParams: (params: {
  paths: string[];
}) => ConstructorParameters<typeof PreparedRegExpRouter>;
export declare const serializeInitParams: (params: ConstructorParameters<typeof PreparedRegExpRouter>) => string;
//#endregion
export {};
