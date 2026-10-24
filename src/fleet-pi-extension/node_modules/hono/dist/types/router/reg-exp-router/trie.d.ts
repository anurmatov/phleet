import { ParamAssocArray } from "./node.js";
//#region src/router/reg-exp-router/trie.d.ts
export type ReplacementMap = number[];
export declare class Trie {
  
  paths: Record<string, [number, ParamAssocArray]>;
  insert(path: string, isStatic: boolean): void;
  buildRegExp(): [RegExp, ReplacementMap, ReplacementMap];
}
//#endregion