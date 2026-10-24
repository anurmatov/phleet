import { serveStatic } from "./serve-static.js";
//#region src/adapter/cloudflare-workers/serve-static-module.ts
const module = (options) => {
	return serveStatic(options);
};
//#endregion
export { module as serveStatic };
