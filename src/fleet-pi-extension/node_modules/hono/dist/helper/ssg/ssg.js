import { replaceUrlParam } from "../../client/utils.js";
import { createPool } from "../../utils/concurrent.js";
import { getExtension } from "../../utils/mime.js";
import { dirname, ensureWithinOutDir, filterStaticGenerateRoutes, isDynamicRoute, joinPaths } from "./utils.js";
import { SSG_CONTEXT } from "./middleware.js";
import { defaultPlugin } from "./plugins.js";
//#region src/helper/ssg/ssg.ts
const DEFAULT_CONCURRENCY = 2;
const DEFAULT_CONTENT_TYPE = "text/plain";
const DEFAULT_OUTPUT_DIR = "./static";
const generateFilePath = (routePath, outDir, mimeType, extensionMap) => {
	const extension = determineExtension(mimeType, extensionMap);
	let filePath;
	if (routePath.endsWith(`.${extension}`)) filePath = joinPaths(outDir, routePath);
	else if (routePath === "/") filePath = joinPaths(outDir, `index.${extension}`);
	else if (routePath.endsWith("/")) filePath = joinPaths(outDir, routePath, `index.${extension}`);
	else filePath = joinPaths(outDir, `${routePath}.${extension}`);
	ensureWithinOutDir(outDir, filePath);
	return filePath;
};
const parseResponseContent = async (response) => {
	const contentType = response.headers.get("Content-Type");
	try {
		if (contentType?.includes("text") || contentType?.includes("json")) return await response.text();
		else return await response.arrayBuffer();
	} catch (error) {
		throw new Error(`Error processing response: ${error instanceof Error ? error.message : "Unknown error"}`);
	}
};
const defaultExtensionMap = {
	"text/html": "html",
	"text/xml": "xml",
	"application/xml": "xml",
	"application/atom+xml": "xml",
	"application/rss+xml": "xml",
	"application/yaml": "yaml"
};
const determineExtension = (mimeType, userExtensionMap) => {
	const extensionMap = userExtensionMap || defaultExtensionMap;
	if (mimeType in extensionMap) return extensionMap[mimeType];
	return getExtension(mimeType) || "html";
};
const combineBeforeRequestHooks = (hooks) => {
	if (!Array.isArray(hooks)) return hooks;
	return async (req) => {
		let currentReq = req;
		for (const hook of hooks) {
			const result = await hook(currentReq);
			if (result === false) return false;
			if (result instanceof Request) currentReq = result;
		}
		return currentReq;
	};
};
const combineAfterResponseHooks = (hooks) => {
	if (!Array.isArray(hooks)) return hooks;
	return async (res) => {
		let currentRes = res;
		for (const hook of hooks) {
			const result = await hook(currentRes);
			if (result === false) return false;
			if (result instanceof Response) currentRes = result;
		}
		return currentRes;
	};
};
const combineAfterGenerateHooks = (hooks, fsModule, options) => {
	if (!Array.isArray(hooks)) return hooks;
	return async (result) => {
		for (const hook of hooks) await hook(result, fsModule, options);
	};
};
/**
* @experimental
* `fetchRoutesContent` is an experimental feature.
* The API might be changed.
*/
const fetchRoutesContent = function* (app, beforeRequestHook, afterResponseHook, concurrency) {
	const baseURL = "http://localhost";
	const pool = createPool({ concurrency });
	for (const route of filterStaticGenerateRoutes(app)) {
		const thisRouteBaseURL = new URL(route.path, baseURL).toString();
		let forGetInfoURLRequest = new Request(thisRouteBaseURL);
		yield new Promise(async (resolveGetInfo, rejectGetInfo) => {
			try {
				if (beforeRequestHook) {
					const maybeRequest = await beforeRequestHook(forGetInfoURLRequest);
					if (!maybeRequest) {
						resolveGetInfo(void 0);
						return;
					}
					forGetInfoURLRequest = maybeRequest;
				}
				await pool.run(() => app.fetch(forGetInfoURLRequest, { [SSG_CONTEXT]: true }));
				if (!forGetInfoURLRequest.ssgParams) {
					if (isDynamicRoute(route.path)) {
						resolveGetInfo(void 0);
						return;
					}
					forGetInfoURLRequest.ssgParams = [{}];
				}
				const requestInit = {
					method: forGetInfoURLRequest.method,
					headers: forGetInfoURLRequest.headers
				};
				resolveGetInfo((function* () {
					for (const param of forGetInfoURLRequest.ssgParams) yield new Promise(async (resolveReq, rejectReq) => {
						try {
							const replacedUrlParam = replaceUrlParam(route.path, param);
							let response = await pool.run(() => app.request(replacedUrlParam, requestInit, { [SSG_CONTEXT]: true }));
							if (response.headers.get("x-hono-disable-ssg")) {
								resolveReq(void 0);
								return;
							}
							if (afterResponseHook) {
								const maybeResponse = await afterResponseHook(response);
								if (!maybeResponse) {
									resolveReq(void 0);
									return;
								}
								response = maybeResponse;
							}
							resolveReq({
								routePath: replacedUrlParam,
								mimeType: response.headers.get("Content-Type")?.split(";")[0] || DEFAULT_CONTENT_TYPE,
								content: await parseResponseContent(response)
							});
						} catch (error) {
							rejectReq(error);
						}
					});
				})());
			} catch (error) {
				rejectGetInfo(error);
			}
		});
	}
};
/**
* @experimental
* `saveContentToFile` is an experimental feature.
* The API might be changed.
*/
const createdDirs = /* @__PURE__ */ new Set();
const saveContentToFile = async (data, fsModule, outDir, extensionMap) => {
	const awaitedData = await data;
	if (!awaitedData) return;
	const { routePath, content, mimeType } = awaitedData;
	const filePath = generateFilePath(routePath, outDir, mimeType, extensionMap);
	const dirPath = dirname(filePath);
	if (dirPath !== "" && !createdDirs.has(dirPath)) {
		await fsModule.mkdir(dirPath, { recursive: true });
		createdDirs.add(dirPath);
	}
	if (typeof content === "string") await fsModule.writeFile(filePath, content);
	else if (content instanceof ArrayBuffer) await fsModule.writeFile(filePath, new Uint8Array(content));
	return filePath;
};
/**
* @experimental
* `toSSG` is an experimental feature.
* The API might be changed.
*/
const toSSG = async (app, fs, options) => {
	let result;
	const getInfoPromises = [];
	const savePromises = [];
	const plugins = options?.plugins || [defaultPlugin()];
	const beforeRequestHooks = [];
	const afterResponseHooks = [];
	const afterGenerateHooks = [];
	if (options?.beforeRequestHook) beforeRequestHooks.push(...Array.isArray(options.beforeRequestHook) ? options.beforeRequestHook : [options.beforeRequestHook]);
	if (options?.afterResponseHook) afterResponseHooks.push(...Array.isArray(options.afterResponseHook) ? options.afterResponseHook : [options.afterResponseHook]);
	if (options?.afterGenerateHook) afterGenerateHooks.push(...Array.isArray(options.afterGenerateHook) ? options.afterGenerateHook : [options.afterGenerateHook]);
	for (const plugin of plugins) {
		if (plugin.beforeRequestHook) beforeRequestHooks.push(...Array.isArray(plugin.beforeRequestHook) ? plugin.beforeRequestHook : [plugin.beforeRequestHook]);
		if (plugin.afterResponseHook) afterResponseHooks.push(...Array.isArray(plugin.afterResponseHook) ? plugin.afterResponseHook : [plugin.afterResponseHook]);
		if (plugin.afterGenerateHook) afterGenerateHooks.push(...Array.isArray(plugin.afterGenerateHook) ? plugin.afterGenerateHook : [plugin.afterGenerateHook]);
	}
	try {
		const outputDir = options?.dir ?? "./static";
		const concurrency = options?.concurrency ?? DEFAULT_CONCURRENCY;
		const combinedBeforeRequestHook = combineBeforeRequestHooks(beforeRequestHooks.length > 0 ? beforeRequestHooks : [(req) => req]);
		const combinedAfterResponseHook = combineAfterResponseHooks(afterResponseHooks.length > 0 ? afterResponseHooks : [(req) => req]);
		const getInfoGen = fetchRoutesContent(app, combinedBeforeRequestHook, combinedAfterResponseHook, concurrency);
		for (const getInfo of getInfoGen) getInfoPromises.push(getInfo.then((getContentGen) => {
			if (!getContentGen) return;
			for (const content of getContentGen) savePromises.push(saveContentToFile(content, fs, outputDir, options?.extensionMap).catch((e) => e));
		}));
		await Promise.all(getInfoPromises);
		const files = [];
		for (const savePromise of savePromises) {
			const fileOrError = await savePromise;
			if (typeof fileOrError === "string") files.push(fileOrError);
			else if (fileOrError) throw fileOrError;
		}
		result = {
			success: true,
			files
		};
	} catch (error) {
		result = {
			success: false,
			files: [],
			error: error instanceof Error ? error : new Error(String(error))
		};
	}
	if (afterGenerateHooks.length > 0) await combineAfterGenerateHooks(afterGenerateHooks, fs, options)(result, fs, options);
	return result;
};
//#endregion
export { DEFAULT_OUTPUT_DIR, combineAfterGenerateHooks, combineAfterResponseHooks, combineBeforeRequestHooks, defaultExtensionMap, fetchRoutesContent, saveContentToFile, toSSG };
