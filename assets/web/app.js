import { analyzeEtternaFromText } from './js/ett/calc.js';
import { runDanielEstimatorFromText } from './js/estimator/danielEstimator.js';
import { runAzusaEstimatorFromText } from './js/estimator/azusaEstimator.js';
import { OsuFileParser } from './js/parser/osuFileParser.js';
import { estDiff } from './js/estimator/reworkEstimatorUtils.js';
import { initRosu } from './js/rosu/rosu.js';
import { ROSU_WASM_B64 } from './js/rosu/rosu_wasm.js';

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
    const dan = runAzusaEstimatorFromText(text, { speedRate: rate });
    if (!dan || !isFinite(dan.star) || /^Invalid/i.test(dan.estDiff || '')) {
        throw new Error(dan && dan.estDiff ? dan.estDiff : 'Azusa 无法计算该谱面（仅支持 4K）');
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
            return sep >= 0 ? dan.estDiff + combined.substring(sep) : dan.estDiff;
        })(),
        osuStar: osuStar,
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
    try {
        const result = await analyze(msg.text, typeof msg.rate === 'number' ? msg.rate : 1, msg.id);
        if (result === null || msg.id !== latestId || cancelled.has(msg.id)) return;
        chrome.webview.postMessage({ type: 'result', id: msg.id, ok: true, result });
    } catch (err) {
        if (msg.id !== latestId || cancelled.has(msg.id)) return;
        chrome.webview.postMessage({ type: 'result', id: msg.id, ok: false, error: String(err && err.message || err) });
    }
});

window.addEventListener('error', (e) => {
    try { chrome.webview.postMessage({ type: 'stage', stage: 'error:' + (e.message || '') }); } catch (x) { }
});

chrome.webview.postMessage({ type: 'ready' });
