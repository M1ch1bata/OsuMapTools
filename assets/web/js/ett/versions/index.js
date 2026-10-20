import createMinaCalc723 from "./minaclac-72.3.js";
import createMinaCalc740 from "./minaclac-74.0.js";
import { ETTERNA_VERSION_KEYS, SUPPORTED_KEYS } from "../constants.js";

export const NON_4K_ETTERNA_FALLBACK_VERSION = "0.74.0";
export const DEFAULT_ETTERNA_VERSION = "0.72.3";

const LOADER_BY_VERSION = Object.freeze({
    "0.72.3": createMinaCalc723,
    "0.74.0": createMinaCalc740,
});

const REGISTRY = Object.freeze({
    "0.72.3": { loader: createMinaCalc723, supportedKeycounts: [...SUPPORTED_KEYS], reason: null },
    "0.74.0": { loader: createMinaCalc740, supportedKeycounts: [...SUPPORTED_KEYS], reason: null },
});

export function listEtternaVersions() {
    return Object.keys(REGISTRY);
}

export function supportsEtternaKeycount(version, keycount) {
    const entry = REGISTRY[version];
    const k = Number(keycount);
    return !!entry && entry.supportedKeycounts.includes(k);
}

export function normalizeEtternaVersion(value) {
    const v = typeof value === "string" ? value.trim() : "";
    const n = v === "0.68.0" ? "0.68.0-Unofficial" : v;
    return n && REGISTRY[n] ? n : DEFAULT_ETTERNA_VERSION;
}

function resolveAvailableFallbackVersion(preferred) {
    if (REGISTRY[preferred]?.loader) return preferred;
    const first = Object.keys(REGISTRY).find((k) => typeof REGISTRY[k].loader === "function");
    return first || null;
}

export function resolveEtternaVersionLoader(value) {
    const requestedVersion = normalizeEtternaVersion(value);
    const requestedEntry = REGISTRY[requestedVersion];
    if (requestedEntry && typeof requestedEntry.loader === "function") {
        return { requestedVersion, version: requestedVersion, loader: requestedEntry.loader, fallbackReason: null };
    }
    const fallbackVersion = resolveAvailableFallbackVersion(DEFAULT_ETTERNA_VERSION);
    const fallbackEntry = fallbackVersion ? REGISTRY[fallbackVersion] : null;
    if (!fallbackEntry || typeof fallbackEntry.loader !== "function") {
        throw new Error("No Etterna MinaCalc wasm loader is available");
    }
    return {
        requestedVersion,
        version: fallbackVersion,
        loader: fallbackEntry.loader,
        fallbackReason: requestedEntry?.reason || "Requested Etterna version is unavailable",
    };
}

export function resolveEtternaVersionLoaderForKeycount(value, keycount) {
    const resolved = resolveEtternaVersionLoader(value);
    const parsed = Number(keycount);
    const preferNon4k = parsed === 6 || parsed === 7;
    if (preferNon4k && resolved.version !== NON_4K_ETTERNA_FALLBACK_VERSION
        && supportsEtternaKeycount(NON_4K_ETTERNA_FALLBACK_VERSION, parsed)) {
        return {
            requestedVersion: resolved.requestedVersion,
            version: NON_4K_ETTERNA_FALLBACK_VERSION,
            loader: REGISTRY[NON_4K_ETTERNA_FALLBACK_VERSION].loader,
            fallbackReason: [resolved.fallbackReason, "Using 0.74.0 for non-4K stability"].filter(Boolean).join("; "),
        };
    }
    if (supportsEtternaKeycount(resolved.version, parsed)) return resolved;
    if (supportsEtternaKeycount(NON_4K_ETTERNA_FALLBACK_VERSION, parsed)) {
        return {
            requestedVersion: resolved.requestedVersion,
            version: NON_4K_ETTERNA_FALLBACK_VERSION,
            loader: REGISTRY[NON_4K_ETTERNA_FALLBACK_VERSION].loader,
            fallbackReason: [resolved.fallbackReason, "keycount fallback"].filter(Boolean).join("; "),
        };
    }
    return resolved;
}

export { ETTERNA_VERSION_KEYS, SUPPORTED_KEYS, DEFAULT_ETTERNA_VERSION as _DEFAULT };
