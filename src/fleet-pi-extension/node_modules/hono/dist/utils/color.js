//#region src/utils/color.ts
/**
* @module
* Color utility.
*/
/**
* Get whether color change on terminal is enabled or disabled.
* If `NO_COLOR` environment variable is set, this function returns `false`.
* Unlike getColorEnabledAsync(), this cannot check Cloudflare environment variables.
* @see {@link https://no-color.org/}
*
* @returns {boolean}
*/
function getColorEnabled() {
	const { process, Deno } = globalThis;
	return !(typeof Deno?.noColor === "boolean" ? Deno.noColor : process !== void 0 ? "NO_COLOR" in process?.env : false);
}
/**
* Get whether color change on terminal is enabled or disabled.
* If `NO_COLOR` environment variable is set, this function returns `false`.
* @see {@link https://no-color.org/}
*
* @returns {boolean}
*/
async function getColorEnabledAsync() {
	const { navigator } = globalThis;
	const cfWorkers = "cloudflare:workers";
	return !(navigator !== void 0 && navigator.userAgent === "Cloudflare-Workers" ? await (async () => {
		try {
			return "NO_COLOR" in ((await import(cfWorkers)).env ?? {});
		} catch {
			return false;
		}
	})() : !getColorEnabled());
}
//#endregion
export { getColorEnabled, getColorEnabledAsync };
