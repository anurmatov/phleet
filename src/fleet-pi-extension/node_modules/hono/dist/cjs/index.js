Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_context = require("./context.js");
const require_hono = require("./hono.js");
//#region src/index.ts
/**
* @module
*
* Hono - Web Framework built on Web Standards
*
* @example
* ```ts
* import { Hono } from 'hono'
* const app = new Hono()
*
* app.get('/', (c) => c.text('Hono!'))
*
* export default app
* ```
*/
//#endregion
exports.Context = require_context.Context;
exports.Hono = require_hono.Hono;
