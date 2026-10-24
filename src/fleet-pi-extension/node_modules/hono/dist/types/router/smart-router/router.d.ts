import { Result, Router } from "../../router.js";
//#region src/router/smart-router/router.d.ts
export declare class SmartRouter<T> implements Router<T> {
  
  name: string;
  constructor(init: {
    routers: Router<T>[];
  });
  add(method: string, path: string, handler: T): void;
  match(method: string, path: string): Result<T>;
  get activeRouter(): Router<T>;
}
//#endregion