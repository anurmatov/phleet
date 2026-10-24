import { ClassNameSlug, CssClassName as CssClassName$1, CssVariableType, OnInvalidSlug, rawCssString } from "./common.js";
import { HtmlEscapedString } from "../../utils/html.js";
//#region src/helper/css/index.d.ts
type CssClassName = HtmlEscapedString & CssClassName$1;
interface CssType {
  (strings: TemplateStringsArray, ...values: CssVariableType[]): Promise<string>;
}
interface CxType {
  (...args: (CssClassName | Promise<string> | string | boolean | null | undefined)[]): Promise<string>;
}
interface KeyframesType {
  (strings: TemplateStringsArray, ...values: CssVariableType[]): CssClassName$1;
}
interface ViewTransitionType {
  (strings: TemplateStringsArray, ...values: CssVariableType[]): Promise<string>;
  (content: Promise<string>): Promise<string>;
  (): Promise<string>;
}
interface StyleType {
  (args?: {
    children?: Promise<string>;
    nonce?: string;
  }): HtmlEscapedString;
}
/**
 * @experimental
 * `createCssContext` is an experimental feature.
 * The API might be changed.
 *
 * @param options.id - The ID for the style element
 * @param options.classNameSlug - Optional function to customize generated CSS class names
 * @param options.onInvalidSlug - Optional callback function called when an invalid slug is returned from ClassNameSlug
 */
export declare const createCssContext: ({ id, classNameSlug, onInvalidSlug }: {
  id: Readonly<string>;
  classNameSlug?: ClassNameSlug;
  onInvalidSlug?: OnInvalidSlug;
}) => DefaultContextType;
interface DefaultContextType {
  css: CssType;
  cx: CxType;
  keyframes: KeyframesType;
  viewTransition: ViewTransitionType;
  Style: StyleType;
}
/**
 * @experimental
 * `css` is an experimental feature.
 * The API might be changed.
 */
export declare const css: CssType;
/**
 * @experimental
 * `cx` is an experimental feature.
 * The API might be changed.
 */
export declare const cx: CxType;
/**
 * @experimental
 * `keyframes` is an experimental feature.
 * The API might be changed.
 */
export declare const keyframes: KeyframesType;
/**
 * @experimental
 * `viewTransition` is an experimental feature.
 * The API might be changed.
 */
export declare const viewTransition: ViewTransitionType;
/**
 * @experimental
 * `Style` is an experimental feature.
 * The API might be changed.
 */
export declare const Style: StyleType;
//#endregion
export { type ClassNameSlug, type OnInvalidSlug, rawCssString };
export {};
