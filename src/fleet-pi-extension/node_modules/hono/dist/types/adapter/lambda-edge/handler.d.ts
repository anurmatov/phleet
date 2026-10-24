import { Hono } from "../../hono.js";
//#region src/adapter/lambda-edge/handler.d.ts
interface CloudFrontHeader {
  key: string;
  value: string;
}
interface CloudFrontHeaders {
  [name: string]: CloudFrontHeader[];
}
interface CloudFrontCustomOrigin {
  customHeaders: CloudFrontHeaders;
  domainName: string;
  keepaliveTimeout: number;
  path: string;
  port: number;
  protocol: string;
  readTimeout: number;
  sslProtocols: string[];
}
interface CloudFrontS3Origin {
  authMethod: 'origin-access-identity' | 'none';
  customHeaders: CloudFrontHeaders;
  domainName: string;
  path: string;
  region: string;
}
type CloudFrontOrigin = {
  s3: CloudFrontS3Origin;
  custom?: never;
} | {
  custom: CloudFrontCustomOrigin;
  s3?: never;
};
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export interface CloudFrontRequest {
  clientIp: string;
  headers: CloudFrontHeaders;
  method: string;
  querystring: string;
  uri: string;
  body?: {
    inputTruncated: boolean;
    action: string;
    encoding: string;
    data: string;
  };
  origin?: CloudFrontOrigin;
}
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export interface CloudFrontResponse {
  headers: CloudFrontHeaders;
  status: string;
  statusDescription?: string;
}
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export interface CloudFrontConfig {
  distributionDomainName: string;
  distributionId: string;
  eventType: string;
  requestId: string;
}
interface CloudFrontEvent {
  cf: {
    config: CloudFrontConfig;
    request: CloudFrontRequest;
    response?: CloudFrontResponse;
  };
}
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export interface CloudFrontEdgeEvent {
  Records: CloudFrontEvent[];
}
type CloudFrontContext = {};
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export interface Callback {
  (err: Error | null, result?: CloudFrontRequest | CloudFrontResult): void;
}
interface CloudFrontResult {
  status: string;
  statusDescription?: string;
  headers?: {
    [header: string]: {
      key: string;
      value: string;
    }[];
  };
  body?: string;
  bodyEncoding?: 'text' | 'base64';
}
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export declare const handle: (app: Hono<any>) => ((event: CloudFrontEdgeEvent, context?: CloudFrontContext, callback?: Callback) => Promise<CloudFrontResult | CloudFrontRequest>);
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export declare const createBody: (method: string, requestBody: CloudFrontRequest["body"]) => string | Uint8Array<ArrayBuffer> | undefined;
/**
 * @deprecated `hono/lambda-edge` will be removed in v5. Install `@hono/lambda-edge` and import from there instead.
 */
export declare const isContentTypeBinary: (contentType: string) => boolean;
//#endregion
export {};
