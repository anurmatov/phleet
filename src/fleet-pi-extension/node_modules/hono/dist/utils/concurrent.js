//#region src/utils/concurrent.ts
/**
* @module
* Concurrent utility.
*/
const DEFAULT_CONCURRENCY = 1024;
const createPool = ({ concurrency, interval } = {}) => {
	concurrency ||= DEFAULT_CONCURRENCY;
	if (concurrency === Infinity) return { run: async (fn) => fn() };
	const pool = /* @__PURE__ */ new Set();
	const run = async (fn, promise, resolve) => {
		if (pool.size >= concurrency) {
			promise ||= new Promise((r) => resolve = r);
			setTimeout(() => run(fn, promise, resolve));
			return promise;
		}
		const marker = {};
		pool.add(marker);
		const result = await fn();
		if (interval) setTimeout(() => pool.delete(marker), interval);
		else pool.delete(marker);
		if (resolve) {
			resolve(result);
			return promise;
		} else return result;
	};
	return { run };
};
//#endregion
export { createPool };
