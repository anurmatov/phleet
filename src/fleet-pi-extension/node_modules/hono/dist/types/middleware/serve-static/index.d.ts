import { Env, MiddlewareHandler } from "../../types.js";
import { Context, Data } from "../../context.js";
//#region src/middleware/serve-static/index.d.ts
export type ServeStaticOptions<E extends Env = Env> = {
  root?: string;
  path?: string;
  precompressed?: boolean;
  /** Allow percent signs in the routed request path. Defaults to false. */
  allowPercentInPath?: boolean;
  mimes?: Record<string, string>;
  rewriteRequestPath?: (path: string) => string;
  onFound?: (path: string, c: Context<E>) => void | Promise<void>;
  onNotFound?: (path: string, c: Context<E>) => void | Promise<void>;
};
/**
 * This middleware is not directly used by the user. Create a wrapper specifying `getContent()` by the environment such as Deno or Bun.
 */
export declare const serveStatic: <E extends Env = Env>(options: ServeStaticOptions<E> & {
  getContent: (path: string, c: Context<E>) => Promise<Data | Response | null>;
  /**
   *
   * `join` option according to the runtime. Example `import { join } from 'node:path`. If not specified, it will fall back to the default join function.`
   */
  join?: (...paths: string[]) => string;
  /**
   * @deprecated Currently, `pathResolve` is no longer used.
   */
  pathResolve?: (path: string) => string;
  isDir?: (path: string) => boolean | undefined | Promise<boolean | undefined>;
}) => MiddlewareHandler;
//#endregion