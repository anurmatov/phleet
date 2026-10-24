//#region src/middleware/timing/timing.ts
const getTime = () => {
	try {
		return performance.now();
	} catch {}
	return Date.now();
};
/**
* Server-Timing Middleware for Hono.
*
* @see {@link https://hono.dev/docs/middleware/builtin/timing}
*
* @param {TimingOptions} [config] - The options for the timing middleware.
* @param {boolean} [config.total=true] - Show the total response time.
* @param {boolean | ((c: Context) => boolean)} [config.enabled=true] - Whether timings should be added to the headers or not.
* @param {string} [config.totalDescription=Total Response Time] - Description for the total response time.
* @param {boolean} [config.autoEnd=true] - If `startTime()` should end automatically at the end of the request.
* @param {boolean | string | ((c: Context) => boolean | string)} [config.crossOrigin=false] - The origin this timings header should be readable.
* @returns {MiddlewareHandler} The middleware handler function.
*
* @example
* ```ts
* const app = new Hono()
*
* // add the middleware to your router
* app.use(timing());
*
* app.get('/', async (c) => {
*   // add custom metrics
*   setMetric(c, 'region', 'europe-west3')
*
*   // add custom metrics with timing, must be in milliseconds
*   setMetric(c, 'custom', 23.8, 'My custom Metric')
*
*   // start a new timer
*   startTime(c, 'db');
*
*   const data = await db.findMany(...);
*
*   // end the timer
*   endTime(c, 'db');
*
*   return c.json({ response: data });
* });
* ```
*/
const timing = (config) => {
	const options = {
		total: true,
		enabled: true,
		totalDescription: "Total Response Time",
		autoEnd: true,
		crossOrigin: false,
		...config
	};
	return async function timing(c, next) {
		const headers = [];
		const timers = /* @__PURE__ */ new Map();
		if (c.get("metric")) return await next();
		c.set("metric", {
			headers,
			timers
		});
		if (options.total) startTime(c, "total", options.totalDescription);
		await next();
		if (options.total) endTime(c, "total");
		if (options.autoEnd) timers.forEach((_, key) => {
			endTime(c, key);
		});
		if (typeof options.enabled === "function" ? options.enabled(c) : options.enabled) {
			c.res.headers.append("Server-Timing", headers.join(","));
			const crossOrigin = typeof options.crossOrigin === "function" ? options.crossOrigin(c) : options.crossOrigin;
			if (crossOrigin) c.res.headers.append("Timing-Allow-Origin", typeof crossOrigin === "string" ? crossOrigin : "*");
		}
	};
};
/**
* Set a metric for the timing middleware.
*
* @param {Context} c - The context of the request.
* @param {string} name - The name of the metric.
* @param {number | string} [valueDescription] - The value or description of the metric.
* @param {string} [description] - The description of the metric.
* @param {number} [precision] - The precision of the metric value.
*
* @example
* ```ts
* setMetric(c, 'region', 'europe-west3')
* setMetric(c, 'custom', 23.8, 'My custom Metric')
* ```
*/
const setMetric = (c, name, valueDescription, description, precision) => {
	const metrics = c.get("metric");
	if (!metrics) {
		console.warn("Metrics not initialized! Please add the `timing()` middleware to this route!");
		return;
	}
	if (typeof valueDescription === "number") {
		const dur = valueDescription.toFixed(precision || 1);
		const metric = description ? `${name};dur=${dur};desc="${description}"` : `${name};dur=${dur}`;
		metrics.headers.push(metric);
	} else {
		const metric = valueDescription ? `${name};desc="${valueDescription}"` : `${name}`;
		metrics.headers.push(metric);
	}
};
/**
* Start a timer for the timing middleware.
*
* @param {Context} c - The context of the request.
* @param {string} name - The name of the timer.
* @param {string} [description] - The description of the timer.
*
* @example
* ```ts
* startTime(c, 'db')
* ```
*/
const startTime = (c, name, description) => {
	const metrics = c.get("metric");
	if (!metrics) {
		console.warn("Metrics not initialized! Please add the `timing()` middleware to this route!");
		return;
	}
	metrics.timers.set(name, {
		description,
		start: getTime()
	});
};
/**
* End a timer for the timing middleware.
*
* @param {Context} c - The context of the request.
* @param {string} name - The name of the timer.
* @param {number} [precision] - The precision of the timer value.
*
* @example
* ```ts
* endTime(c, 'db')
* ```
*/
const endTime = (c, name, precision) => {
	const metrics = c.get("metric");
	if (!metrics) {
		console.warn("Metrics not initialized! Please add the `timing()` middleware to this route!");
		return;
	}
	const timer = metrics.timers.get(name);
	if (!timer) {
		console.warn(`Timer "${name}" does not exist!`);
		return;
	}
	const { description, start } = timer;
	const duration = getTime() - start;
	setMetric(c, name, duration, description, precision);
	metrics.timers.delete(name);
};
/**
* Wrap a Promise to capture its duration.
* @param {Context} c - The context of the request.
* @param {string} name - The name of the timer.
* @param {Promise<T>} callable - The Promise to time.
* @param {string} [description] - The description of the timer.
* @param {number} [precision] - The precision of the timer value.
*
* @example
* ```ts
*   // Instead of this:
*   const data = await db.findMany(...);
*
*   // do this:
*   const data = await wrapTime(c, 'query', db.findMany(...));
* ```
* */
async function wrapTime(c, name, callable, description, precision) {
	startTime(c, name, description);
	try {
		return await callable;
	} finally {
		endTime(c, name, precision);
	}
}
//#endregion
export { endTime, setMetric, startTime, timing, wrapTime };
