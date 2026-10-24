import { MiddlewareHandler } from "../../types.js";
import { CookiePrefixOptions } from "../../utils/cookie.js";
import { AsymmetricAlgorithm, SignatureAlgorithm } from "../../utils/jwt/jwa.js";
import { HonoJsonWebKey, SignatureKey } from "../../utils/jwt/jws.js";
import { JWTPayload } from "../../utils/jwt/types.js";
import { TokenHeader, VerifyOptions, VerifyOptionsWithAlg } from "../../utils/jwt/jwt.js";
//#region src/middleware/jwt/jwt.d.ts
export type JwtVariables<T = any> = {
  jwtPayload: T;
};
/**
 * JWT Auth Middleware for Hono.
 *
 * @see {@link https://hono.dev/docs/middleware/builtin/jwt}
 *
 * @param {object} options - The options for the JWT middleware.
 * @param {SignatureKey} options.secret - A value of your secret key.
 * @param {string} [options.cookie] - If this value is set, then the value is retrieved from the cookie header using that value as a key, which is then validated as a token.
 * @param {SignatureAlgorithm} options.alg - An algorithm type that is used for verifying (required). Available types are `HS256` | `HS384` | `HS512` | `RS256` | `RS384` | `RS512` | `PS256` | `PS384` | `PS512` | `ES256` | `ES384` | `ES512` | `EdDSA`.
 * @param {string} [options.headerName='Authorization'] - The name of the header to look for the JWT token. Default is 'Authorization'.
 * @param {string} [options.realm] - The protection space described by the `realm` parameter of the returned `WWW-Authenticate` challenge header. Defaults to the request URL.
 * @param {VerifyOptions} [options.verification] - Additional options for JWT payload verification.
 * @returns {MiddlewareHandler} The middleware handler function.
 *
 * @example
 * ```ts
 * const app = new Hono()
 *
 * app.use(
 *   '/auth/*',
 *   jwt({
 *     secret: 'it-is-very-secret',
 *     alg: 'HS256',
 *     headerName: 'x-custom-auth-header', // Optional, default is 'Authorization'
 *   })
 * )
 *
 * app.get('/auth/page', (c) => {
 *   return c.text('You are authorized')
 * })
 * ```
 */
export declare const jwt: (options: {
  secret: SignatureKey;
  cookie?: string | {
    key: string;
    secret?: string | BufferSource;
    prefixOptions?: CookiePrefixOptions;
  };
  alg: SignatureAlgorithm;
  headerName?: string;
  realm?: string;
  verification?: VerifyOptions;
}) => MiddlewareHandler;
export declare const verifyWithJwks: (token: string, options: {
  keys?: HonoJsonWebKey[];
  jwks_uri?: string;
  verification?: VerifyOptions;
  allowedAlgorithms: readonly AsymmetricAlgorithm[];
}, init?: RequestInit) => Promise<JWTPayload>;
export declare const verify: (token: string, publicKey: SignatureKey, algOrOptions: SignatureAlgorithm | VerifyOptionsWithAlg) => Promise<JWTPayload>;
export declare const decode: (token: string) => {
  header: TokenHeader;
  payload: JWTPayload;
};
export declare const sign: (payload: JWTPayload, privateKey: SignatureKey, alg?: SignatureAlgorithm) => Promise<string>;
//#endregion