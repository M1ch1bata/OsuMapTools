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

async function analyze(text, rate) {
    stage('parse');
    const parsed = new OsuFileParser(text);
    parsed.process();
    if (parsed.status !== 'OK') {
        throw new Error('beatmap parse status: ' + parsed.status);
    }

    stage('msd');
    const msd = await analyzeEtternaFromText(text, { musicRate: rate });

    stage('dan');
    const dan = runAzusaEstimatorFromText(text, { speedRate: rate });

    stage('osu');
    let osuStar = null;
    try {
        const rosu = await getRosu();
        const attrs = new rosu.Difficulty({ mods: 0 }).calculate(new rosu.Beatmap(text));
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
        difficulty: dan.estDiff,
        osuStar: osuStar,
    };
}

chrome.webview.addEventListener('message', async (ev) => {
    const msg = ev.data;
    if (!msg || msg.type !== 'analyze') return;
    try {
        const result = await analyze(msg.text, typeof msg.rate === 'number' ? msg.rate : 1);
        chrome.webview.postMessage({ type: 'result', id: msg.id, ok: true, result });
    } catch (err) {
        chrome.webview.postMessage({ type: 'result', id: msg.id, ok: false, error: String(err && err.message || err) });
    }
});

window.addEventListener('error', (e) => {
    try { chrome.webview.postMessage({ type: 'stage', stage: 'error:' + (e.message || '') }); } catch (x) { }
});

chrome.webview.postMessage({ type: 'ready' });
