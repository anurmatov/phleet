import { JSONValue } from "./types.js";
//#region src/utils/crypto.d.ts
type Algorithm = {
  name: string;
  alias: string;
};
type Data = string | boolean | number | JSONValue | ArrayBufferView | ArrayBuffer;
export declare const sha256: (data: Data) => Promise<string | null>;
export declare const sha1: (data: Data) => Promise<string | null>;
export declare const md5: (data: Data) => Promise<string | null>;
export declare const createHash: (data: Data, algorithm: Algorithm) => Promise<string | null>;
//#endregion
export {};
