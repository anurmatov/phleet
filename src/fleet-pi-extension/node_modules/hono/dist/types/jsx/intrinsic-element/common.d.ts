import { Props } from "../base.js";
//#region src/jsx/intrinsic-element/common.d.ts
export declare const deDupeKeyMap: Record<string, string[]>;
export declare const domRenderers: Record<string, Function>;
export declare const dataPrecedenceAttr = "data-precedence";
export declare const isStylesheetLinkWithPrecedence: (props: Props) => boolean;
export declare const shouldDeDupeByKey: (tagName: string, supportSort: boolean) => boolean;
//#endregion