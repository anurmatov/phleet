import { HtmlEscapedString } from "../utils/html.js";
import { html } from "../helper/html/index.js";
import { Fragment, JSX } from "./base.js";
import { jsxDEV } from "./jsx-dev-runtime.js";
//#region src/jsx/jsx-runtime.d.ts
export declare const jsxAttr: (key: string, v: string | Promise<string> | Record<string, string | number | null | undefined | boolean>) => HtmlEscapedString | Promise<HtmlEscapedString>;
export declare const jsxEscape: (value: string) => string;
//#endregion
export { Fragment, type JSX, jsxDEV as jsx, html as jsxTemplate, jsxDEV as jsxs };