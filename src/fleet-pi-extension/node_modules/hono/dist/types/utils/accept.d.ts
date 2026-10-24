//#region src/utils/accept.d.ts
export interface Accept {
  type: string;
  params: Record<string, string>;
  q: number;
}
export declare const parseAccept: (acceptHeader: string) => Accept[];
//#endregion