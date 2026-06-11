// AUTO-GENERATED from docs/openapi/openapi.v1.json by `npm run openapi:sync` — do not edit.
// `npm run openapi:check` (part of `npm run check` and CI) fails when this file is stale.

export interface paths {
    "/.well-known/api-catalog": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Too Many Requests */
                429: {
                    headers: {
                        /** @description Seconds to wait before retrying the request. */
                        "Retry-After": number;
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/feedback": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Submits feedback; retries carrying the same Idempotency-Key replay the stored response. */
        post: {
            parameters: {
                query?: never;
                header: {
                    "Idempotency-Key": string;
                };
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["FeedbackRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["FeedbackReceipt"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ValidationProblemDetails"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Too Many Requests */
                429: {
                    headers: {
                        /** @description Seconds to wait before retrying the request. */
                        "Retry-After": number;
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/weatherforecast": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["WeatherForecast"][];
                    };
                };
                /** @description Too Many Requests */
                429: {
                    headers: {
                        /** @description Seconds to wait before retrying the request. */
                        "Retry-After": number;
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        /**
         * @description Server acknowledgement. Guid FeedbackReceipt.Id is minted server-side, which
         *     makes idempotent replay observable: a retry that re-executed would mint a
         *     NEW id; a replayed response carries the SAME one.
         */
        FeedbackReceipt: {
            /** Format: uuid */
            id: string;
            /** Format: date-time */
            receivedAt: string;
            message: string;
        };
        /**
         * @description Sample create-style payload for the idempotent POST teaching endpoint
         *     (FeedbackController). DataAnnotations produce the automatic 400
         *     ValidationProblemDetails; domain rules live in FeedbackService (422).
         */
        FeedbackRequest: {
            message: string;
        };
        ProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: null | number;
            detail?: null | string;
            instance?: null | string;
        };
        ValidationProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: null | number;
            detail?: null | string;
            instance?: null | string;
            errors?: {
                [key: string]: string[];
            };
        };
        WeatherForecast: {
            /** Format: date */
            date: string;
            /** Format: int32 */
            temperatureC: number;
            summary: null | string;
            /** Format: int32 */
            readonly temperatureF: number;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export type operations = Record<string, never>;
