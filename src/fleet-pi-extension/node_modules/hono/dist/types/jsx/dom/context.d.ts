import { Context } from "../context.js";
//#region src/jsx/dom/context.d.ts
export declare const createContextProviderFunction: <T>(values: T[]) => Function;
export declare const createContext: <T>(defaultValue: T) => Context<T>;
//#endregion