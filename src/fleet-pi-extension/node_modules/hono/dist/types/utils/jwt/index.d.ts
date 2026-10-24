import { AsymmetricAlgorithm, SignatureAlgorithm } from "./jwa.js";
import { HonoJsonWebKey, SignatureKey } from "./jws.js";
import { JWTPayload } from "./types.js";
import { TokenHeader, VerifyOptions, VerifyOptionsWithAlg } from "./jwt.js";
//#region src/utils/jwt/index.d.ts
/**
 * @module
 * JWT utility.
 */
export declare const Jwt: {
  sign: (payload: JWTPayload, privateKey: SignatureKey, alg?: SignatureAlgorithm) => Promise<string>;
  verify: (token: string, publicKey: SignatureKey, algOrOptions: SignatureAlgorithm | VerifyOptionsWithAlg) => Promise<JWTPayload>;
  decode: (token: string) => {
    header: TokenHeader;
    payload: JWTPayload;
  };
  verifyWithJwks: (token: string, options: {
    keys?: HonoJsonWebKey[];
    jwks_uri?: string;
    verification?: VerifyOptions;
    allowedAlgorithms: readonly AsymmetricAlgorithm[];
  }, init?: RequestInit) => Promise<JWTPayload>;
};
//#endregion