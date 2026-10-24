import { Result, Router } from "../../router.js";
//#region src/router/trie-router/router.d.ts
export declare class TrieRouter<T> implements Router<T> {
  
  name: string;
  add(method: string, path: string, handler: T): void;
  match(method: string, path: string): Result<T>;
}
//#endregion