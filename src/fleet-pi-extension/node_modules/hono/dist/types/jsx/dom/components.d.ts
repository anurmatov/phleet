import { ErrorHandler, FallbackRender } from "../components.js";
import { PropsWithChildren } from "../types.js";
import { Child, FC } from "../base.js";
//#region src/jsx/dom/components.d.ts
export declare const ErrorBoundary: FC<PropsWithChildren<{
  fallback?: Child;
  fallbackRender?: FallbackRender;
  onError?: ErrorHandler;
}>>;
export declare const Suspense: FC<PropsWithChildren<{
  fallback: any;
}>>;
//#endregion