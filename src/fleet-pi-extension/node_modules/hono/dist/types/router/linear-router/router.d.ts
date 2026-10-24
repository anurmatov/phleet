import { Result, Router } from "../../router.js";
//#region src/router/linear-router/router.d.ts
export declare class LinearRouter<T> implements Router<T> {
  
  name: string;
  add(method: string, path: string, handler: T): void;
  match(method: string, path: string): Result<T>;
}
//#endregion