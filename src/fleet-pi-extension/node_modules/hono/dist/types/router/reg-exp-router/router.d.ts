import { Router } from "../../router.js";
import { MatcherMap, match } from "./matcher.js";
//#region src/router/reg-exp-router/router.d.ts
export declare class RegExpRouter<T> implements Router<T> {
  
  name: string;
  constructor();
  add(method: string, path: string, handler: T): void;
  match: typeof match<Router<T>, T>;
  protected buildAllMatchers(): MatcherMap<T>;
}
//#endregion