import { CacheType, DetectorOptions, DetectorType, LanguageVariables, detectFromCookie, detectFromHeader, detectFromPath, detectFromQuery, languageDetector } from "./language.js";
//#region src/middleware/language/index.d.ts
declare module '../..' {
  interface ContextVariableMap extends LanguageVariables {}
}
//#endregion
export { type CacheType, type DetectorOptions, type DetectorType, type LanguageVariables, detectFromCookie, detectFromHeader, detectFromPath, detectFromQuery, languageDetector };
export {};
