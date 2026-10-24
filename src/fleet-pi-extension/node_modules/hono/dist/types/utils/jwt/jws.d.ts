import { SignatureAlgorithm } from "./jwa.js";
//#region src/utils/jwt/jws.d.ts
export interface HonoJsonWebKey extends JsonWebKey {
  kid?: string;
}
export type SignatureKey = string | HonoJsonWebKey | CryptoKey;
export declare function signing(privateKey: SignatureKey, alg: SignatureAlgorithm, data: BufferSource): Promise<ArrayBuffer>;
export declare function verifying(publicKey: SignatureKey, alg: SignatureAlgorithm, signature: BufferSource, data: BufferSource): Promise<boolean>;
//#endregion