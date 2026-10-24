import { analyzeEtternaFromText } from './js/ett/calc.js';
import { runDanielEstimatorFromText } from './js/estimator/danielEstimator.js';
import { runAzusaEstimatorFromText } from './js/estimator/azusaEstimator.js';
import { runSunnyEstimatorFromText } from './js/estimator/sunnyEstimator.js';
import { OsuFileParser } from './js/parser/osuFileParser.js';
import { estDiff } from './js/estimator/reworkEstimatorUtils.js';
import { initRosu } from './js/rosu/rosu.js';
import { ROSU_WASM_B64 } from './js/rosu/rosu_wasm.js';
import { analyzePatternFromText } from './js/patterns/service.js';

let rosuPromise = null;
function getRosu() {
    if (!rosuPromise) rosuPromise = initRosu(ROSU_WASM_B64);
    return rosuPromise;
}

function stage(name) {
    try { chrome.webview.postMessage({ type: 'stage', stage: name }); } catch (e) { }
}

let latestId = 0;
const cancelled = new Set();

async function analyze(text, rate, id) {
    stage('parse');
    const parsed = new OsuFileParser(text);
    parsed.process();
    if (parsed.status !== 'OK') {
        throw new Error('beatmap parse status: ' + parsed.status);
    }

    stage('msd');
    let msd;
    try {
        msd = await analyzeEtternaFromText(text, { musicRate: rate });
    } catch (e) {
        msd = {
            etternaVersion: 'unavailable',
            values: { Overall: 0, Stream: 0, Jumpstream: 0, Handstream: 0, Stamina: 0, JackSpeed: 0, Chordjack: 0, Technical: 0 },
        };
    }
    if (id !== latestId || cancelled.has(id)) return null;

    stage('dan');
    let dan = runAzusaEstimatorFromText(text, { speedRate: rate });
    let lv = null;
    if (!dan || !isFinite(dan.star) || /^Invalid/i.test(dan.estDiff || '')) {
        // 6K: the plugin (and the community "xxy" level) is derived from the Sunny star rating:
        //     LV = sunnyStar * 200 / 81 + 7 / 6
        if (parsed.columnCount !== 4) {
            // Non-4K maps use the Sunny star rating for the dan label; 6K additionally gets the
            // community "xxy" level (LV). Other key counts intentionally have no numeric metric.
            const sunny = runSunnyEstimatorFromText(text, { speedRate: rate });
            if (sunny && isFinite(sunny.star)) {
                dan = {
                    star: sunny.star,
                    estDiff: estDiff(sunny.star, parsed.lnRatio, parsed.columnCount),
                    numericDifficulty: null,
                };
                if (parsed.columnCount === 6) {
                    lv = Math.round((sunny.star * 200 / 81 + 7 / 6) * 100) / 100;
                }
            } else {
                throw new Error('Sunny 无法计算该谱面');
            }
        } else {
            throw new Error(dan && dan.estDiff ? dan.estDiff : 'Azusa 无法计算该谱面（仅支持 4K）');
        }
    }

    stage('osu');
    let osuStar = null;
    try {
        const rosu = await getRosu();
        const attrs = new rosu.Difficulty({ mods: 0, clockRate: rate }).calculate(new rosu.Beatmap(text));
        if (attrs && isFinite(attrs.stars)) osuStar = attrs.stars;
    } catch (e) { }

    stage('done');
    return {
        keys: parsed.columnCount,
        lnRatio: parsed.lnRatio,
        etternaVersion: msd.etternaVersion,
        msd: msd.values,
        dan: {
            label: dan.estDiff,
            numeric: dan.numericDifficulty,
            star: dan.star,
        },
        difficulty: (function () {
            const combined = estDiff(dan.star, parsed.lnRatio, parsed.columnCount);
            const sep = combined.indexOf(' || ');
            if (sep < 0) return dan.estDiff;
            const lnPart = combined.substring(sep);
            // Azusa returns an RC-only label; other estimators may already include the LN part.
            return dan.estDiff.includes(' || ') ? dan.estDiff : dan.estDiff + lnPart;
        })(),
        osuStar: osuStar,
        lv: lv,
    };
}

chrome.webview.addEventListener('message', async (ev) => {
    const msg = ev.data;
    if (!msg) return;

    if (msg.type === 'cancel') {
        cancelled.add(msg.id);
        return;
    }
    if (msg.type !== 'analyze') return;

    latestId = msg.id;

    // Pattern classification is independent from difficulty estimation, so it is computed first
    // and still reported when the star/dan pipeline fails (e.g. 6K maps).
    let pattern = null;
    let modeTag = null;
    try {
        const { report } = analyzePatternFromText(msg.text, 1.0);
        pattern = report.Category;
        modeTag = report.ModeTag;
    } catch (e) { }

    // Banner shows the pattern of the ORIGINAL map; the difficulty row shows the transformed map.
    let sourcePattern = null;
    let sourceModeTag = null;
    if (typeof msg.sourceText === 'string') {
        try {
            const { report } = analyzePatternFromText(msg.sourceText, 1.0);
            sourcePattern = report.Category;
            sourceModeTag = report.ModeTag;
        } catch (e) { }
    }

    try {
        const result = await analyze(msg.text, typeof msg.rate === 'number' ? msg.rate : 1, msg.id);
        if (result === null || msg.id !== latestId || cancelled.has(msg.id)) return;
        result.pattern = pattern;
        result.modeTag = modeTag;
        result.sourcePattern = sourcePattern;
        result.sourceModeTag = sourceModeTag;
        chrome.webview.postMessage({ type: 'result', id: msg.id, ok: true, result });
    } catch (err) {
        if (msg.id !== latestId || cancelled.has(msg.id)) return;
        chrome.webview.postMessage({ type: 'result', id: msg.id, ok: false, error: String(err && err.message || err), pattern, modeTag, sourcePattern, sourceModeTag });
    }
});

window.addEventListener('error', (e) => {
    try { chrome.webview.postMessage({ type: 'stage', stage: 'error:' + (e.message || '') }); } catch (x) { }
});

chrome.webview.postMessage({ type: 'ready' });
