import { Child } from "./base.js";
//#region src/jsx/children.d.ts
export declare const toArray: (children: Child) => Child[];
export declare const Children: {
  map: (children: Child[], fn: (child: Child, index: number) => Child) => Child[];
  forEach: (children: Child[], fn: (child: Child, index: number) => void) => void;
  count: (children: Child[]) => number;
  only: (_children: Child[]) => Child;
  toArray: (children: Child) => Child[];
};
//#endregion