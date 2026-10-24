//#region src/utils/handler.d.ts
/**
 * @module
 * Handler utility.
 */
export declare const isMiddleware: (handler: Function) => boolean;
export declare const findTargetHandler: (handler: Function) => Function;
//#endregion