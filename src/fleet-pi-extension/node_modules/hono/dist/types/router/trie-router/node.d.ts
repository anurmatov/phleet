import { Params } from "../../router.js";
//#region src/router/trie-router/node.d.ts
export declare class Node<T> {
  
  insert(method: string, path: string, handler: T): void;
  search(method: string, path: string): [[T, Params][]];
}
//#endregion