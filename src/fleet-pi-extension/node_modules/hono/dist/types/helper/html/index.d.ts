import { HtmlEscapedString, raw } from "../../utils/html.js";
//#region src/helper/html/index.d.ts
export declare const html: (strings: TemplateStringsArray, ...values: unknown[]) => HtmlEscapedString | Promise<HtmlEscapedString>;
//#endregion
export { raw };