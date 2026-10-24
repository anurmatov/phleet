Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
const require_utils_accept = require("../../utils/accept.js");
const require_helper_cookie_index = require("../../helper/cookie/index.js");
//#region src/middleware/language/language.ts
const DEFAULT_OPTIONS = {
	order: [
		"querystring",
		"cookie",
		"header"
	],
	lookupQueryString: "lang",
	lookupCookie: "language",
	lookupFromHeaderKey: "accept-language",
	lookupFromPathIndex: 0,
	caches: ["cookie"],
	ignoreCase: true,
	fallbackLanguage: "en",
	supportedLanguages: ["en"],
	cookieOptions: {
		sameSite: "Strict",
		secure: true,
		maxAge: 31536e3,
		httpOnly: true
	},
	debug: false
};
/**
* Parse Accept-Language header values with quality scores
* @param header Accept-Language header string
* @returns Array of parsed languages with quality scores
*/
function parseAcceptLanguage(header) {
	return require_utils_accept.parseAccept(header).map(({ type, q }) => ({
		lang: type,
		q
	}));
}
/**
* Validate and normalize language codes
* @param lang Language code to normalize
* @param options Detector options
* @returns Normalized language code or undefined
*/
const normalizeLanguage = (lang, options) => {
	if (!lang) return;
	try {
		let normalizedLang = lang.trim();
		if (options.convertDetectedLanguage) normalizedLang = options.convertDetectedLanguage(normalizedLang);
		const compLang = options.ignoreCase ? normalizedLang.toLowerCase() : normalizedLang;
		const compSupported = options.supportedLanguages.map((l) => options.ignoreCase ? l.toLowerCase() : l);
		const exactIndex = compSupported.indexOf(compLang);
		if (exactIndex !== -1) return options.supportedLanguages[exactIndex];
		let longestMatchIndex = -1;
		let longestMatchLength = -1;
		for (let i = 0; i < compSupported.length; i++) {
			const candidate = compSupported[i];
			if (candidate.length < compLang.length && candidate.length > longestMatchLength && compLang.startsWith(candidate) && compLang[candidate.length] === "-") {
				longestMatchIndex = i;
				longestMatchLength = candidate.length;
			}
		}
		if (longestMatchIndex !== -1) return options.supportedLanguages[longestMatchIndex];
		return;
	} catch {
		return;
	}
};
/**
* Detects language from query parameter
*/
const detectFromQuery = (c, options) => {
	const query = c.req.query(options.lookupQueryString);
	return normalizeLanguage(query, options);
};
/**
* Detects language from cookie
*/
const detectFromCookie = (c, options) => {
	const cookie = require_helper_cookie_index.getCookie(c, options.lookupCookie);
	return normalizeLanguage(cookie, options);
};
/**
* Detects language from Accept-Language header
*/
function detectFromHeader(c, options) {
	try {
		const acceptLanguage = c.req.header(options.lookupFromHeaderKey);
		if (!acceptLanguage) return;
		const languages = parseAcceptLanguage(acceptLanguage);
		for (const { lang, q } of languages) {
			if (q === 0) continue;
			const normalizedLang = normalizeLanguage(lang, options);
			if (normalizedLang) return normalizedLang;
		}
		return;
	} catch {
		return;
	}
}
/**
* Detects language from URL path
*/
function detectFromPath(c, options) {
	const langSegment = new URL(c.req.url).pathname.split("/").filter(Boolean)[options.lookupFromPathIndex];
	return normalizeLanguage(langSegment, options);
}
/**
* Collection of all language detection strategies
*/
const detectors = {
	querystring: detectFromQuery,
	cookie: detectFromCookie,
	header: detectFromHeader,
	path: detectFromPath
};
/**
* Validate detector options
* @param options Detector options to validate
* @throws Error if options are invalid
*/
function validateOptions(options) {
	if (!options.supportedLanguages.includes(options.fallbackLanguage)) throw new Error("Fallback language must be included in supported languages");
	if (options.lookupFromPathIndex < 0) throw new Error("Path index must be non-negative");
	if (!options.order.every((detector) => Object.keys(detectors).includes(detector))) throw new Error("Invalid detector type in order array");
}
/**
* Cache detected language
*/
function cacheLanguage(c, language, options) {
	if (!Array.isArray(options.caches) || !options.caches.includes("cookie")) return;
	try {
		require_helper_cookie_index.setCookie(c, options.lookupCookie, language, options.cookieOptions);
	} catch (error) {
		if (options.debug) console.error("Failed to cache language:", error);
	}
}
/**
* Detect language from request
*/
const detectLanguage = (c, options) => {
	let detectedLang;
	for (const detectorName of options.order) {
		const detector = detectors[detectorName];
		try {
			detectedLang = detector(c, options);
			if (detectedLang) {
				if (options.debug) console.log(`Language detected from ${detectorName}: ${detectedLang}`);
				break;
			}
		} catch (error) {
			if (options.debug) console.error(`Error in ${detectorName} detector:`, error);
			continue;
		}
	}
	const finalLang = detectedLang || options.fallbackLanguage;
	if (detectedLang && options.caches) cacheLanguage(c, finalLang, options);
	return finalLang;
};
/**
* Language detector middleware factory
* @param userOptions Configuration options for the language detector
* @returns Hono middleware function
*
* @example
* ```ts
* type Variables = LanguageVariables
* const app = new Hono<{ Variables: Variables }>()
*
* app.use(
*   languageDetector({
*     supportedLanguages: ['en', 'ja'], // Must include fallback
*     fallbackLanguage: 'en', // Required
*   })
* )
*
* app.get('/', (c) => {
*   const lang = c.get('language')
*   return c.text(`Current language: ${lang}`)
* })
* ```
*/
const languageDetector = (userOptions) => {
	const options = {
		...DEFAULT_OPTIONS,
		...userOptions,
		cookieOptions: {
			...DEFAULT_OPTIONS.cookieOptions,
			...userOptions.cookieOptions
		}
	};
	validateOptions(options);
	return async function languageDetector(ctx, next) {
		const lang = detectLanguage(ctx, options);
		ctx.set("language", lang);
		await next();
	};
};
//#endregion
exports.DEFAULT_OPTIONS = DEFAULT_OPTIONS;
exports.detectFromCookie = detectFromCookie;
exports.detectFromHeader = detectFromHeader;
exports.detectFromPath = detectFromPath;
exports.detectFromQuery = detectFromQuery;
exports.detectors = detectors;
exports.languageDetector = languageDetector;
exports.normalizeLanguage = normalizeLanguage;
exports.parseAcceptLanguage = parseAcceptLanguage;
exports.validateOptions = validateOptions;
