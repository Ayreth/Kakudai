// Kakudai — faux pont pour développer la page dans un navigateur ordinaire.
// Chargé par app.js UNIQUEMENT quand window.chrome.webview est absent. Aucune requête réseau : les fonds de map
// sont dessinés dans un <canvas> et passés en data: URL. Le calcul des stats est celui du C# (Trainer.Resolve),
// recopié ici, pour que la page réagisse comme dans l'appli. Le star rating ("stars") est une formule factice,
// envoyée comme le C# 300 ms après le dernier changement de réglage.
//
// État de départ choisi dans l'URL : index.html?state=<nom>[&update][&shot]
//   nomap        en attente du jeu, aucune map (Generate désactivé, valeurs « – »)
//   map          (défaut) map chargée, deux profils enregistrés
//   closed       comme map, avec « More options » fermé (tous les autres états l'ouvrent)
//   generating   clic sur Generate, la réponse n'arrive jamais
//   error        clic sur Generate, la génération échoue
//   done         clic sur Generate, la génération réussit
//   nothing      espacement ×1, rate 1, rien d'autre : « Nothing to change »
//   locked       Emulate HR, Flip map (Both), BPM verrouillé à 200, AR verrouillé à 9.5 (écrit 10), OD réglé à la
//                main ; focus sur AR (valeur de la map)
//   nomaplocked  aucune map, mais un BPM et un AR verrouillés (et un HP manuel non verrouillé, qui affiche « – »)
//   kakudai      map déjà générée par Kakudai (aucune mention : on peut l'amplifier encore), titre long, sans fond
//   unsupported  map affichée, puis le jeu passe sur une diff taiko
//   long         noms très longs, puis une longue erreur à la génération
//   rename       renommage du 2e profil en cours
//   nokiai       « Kiai only » sur une map sans kiai, puis Generate (star rating inconnu : « – »)
//   nostars      map chargée, le star rating n'arrive jamais (« – »)
// &update : une release plus récente (v2.1.0) est annoncée : bandeau « Download » (journalisé, rien ne s'ouvre).
// &shot : pour les captures en navigateur headless (temps virtuel) : sans transitions ni animations, et la page
// tenue à 430 px de large (Edge headless n'accepte pas de fenêtre plus étroite que ~490 px ; on recadre l'image).
// Les messages {type:"window"} sont journalisés dans la console et dans KakudaiMock.windowLog ; la dernière hauteur
// demandée est aussi posée sur <html data-requested-height> (lisible avec --dump-dom).
(() => {
  'use strict';

  const params = new URLSearchParams(location.search);
  const scenario = params.get('state') || 'map';

  if (params.has('shot')) {
    const sheet = new CSSStyleSheet();
    sheet.replaceSync('*, *::before, *::after { transition: none !important; animation: none !important; }'
      + ' html { width: 430px; } body::after { right: auto; width: 430px; }');
    document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
  }

  // « More options » : ouvert pour tous les états sauf « closed » (app.js lit cette clé au démarrage).
  try { localStorage.setItem('kakudai.moreOptions', scenario === 'closed' ? 'closed' : 'open'); } catch { /* rien */ }

  const listeners = new Set();
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  const emit = (data) => listeners.forEach((fn) => fn({ data }));
  const windowLog = [];

  // ---------------------------------------------------------------- fonds dessinés localement
  function paintArt(glyphs, { x = 900, y = 250, size = 380, tilt = -0.08, tone = 0 } = {}) {
    const c = document.createElement('canvas');
    c.width = 1200; c.height = 500;
    const g = c.getContext('2d');
    const base = g.createLinearGradient(0, 0, 1200, 500);
    base.addColorStop(0, `hsl(${tone} 8% 22%)`);
    base.addColorStop(1, `hsl(${tone} 6% 6%)`);
    g.fillStyle = base;
    g.fillRect(0, 0, 1200, 500);
    g.filter = 'blur(50px)';
    g.fillStyle = `hsl(${tone} 40% 60% / 0.22)`;
    g.beginPath(); g.ellipse(x - 60, y - 90, 360, 180, 0.35, 0, Math.PI * 2); g.fill();
    g.fillStyle = 'rgba(255,255,255,0.06)';
    g.beginPath(); g.ellipse(220, 380, 300, 140, -0.2, 0, Math.PI * 2); g.fill();
    g.filter = 'blur(1px)';
    g.fillStyle = 'rgba(232,232,232,0.85)';
    g.font = `900 ${size}px "Yu Mincho", "YuMincho", "MS Mincho", "Yu Gothic", serif`;
    g.textBaseline = 'middle';
    g.save();
    g.translate(x, y);
    g.rotate(tilt);
    g.fillText(glyphs, -g.measureText(glyphs).width / 2, 0);
    g.restore();
    return c.toDataURL('image/jpeg', 0.86);
  }

  // ---------------------------------------------------------------- bibliothèque factice
  const library = {
    satori: {
      id: '1b0f3c2a-0000-4000-8000-00000000sato', artist: 'goreshit', title: 'satori de pon!',
      difficultyName: 'Insane', mapper: 'Rita Summers', isKakudai: false, stars: 5.84,
      base: { hp: 5, cs: 4, ar: 9.3, od: 8.5, mainBpm: 160, hasKiai: true, difficultyName: 'Insane' },
      art: () => paintArt('拡大', { tone: 330 }),
    },
    kakudai: {
      id: '2c71d000-0000-4000-8000-0000000kakud', artist: 'Camellia',
      title: 'Exit This Earth\'s Atomosphere (Camellia\'s "PLANETARY//200STEP" Remix)',
      difficultyName: '2x spaced Evolution of Planetary Mechanics', mapper: 'Nao Tomori', isKakudai: true, stars: 7.93,
      base: { hp: 6, cs: 4.2, ar: 9.8, od: 9.2, mainBpm: 200, hasKiai: true, difficultyName: '2x spaced Evolution of Planetary Mechanics' },
      art: null,
    },
    long: {
      id: '3d2e9000-0000-4000-8000-000000000long',
      artist: 'Camellia feat. Nanahira, Hatsune Miku et toute la bande du Planetarium',
      title: 'Exit This Earth\'s Atomosphere (Camellia\'s "PLANETARY//200STEP" Remix) — Extended Mix for the Planetarium',
      difficultyName: 'Evolution of Planetary Mechanics and the Collapse of Everything We Knew',
      mapper: 'Nao Tomori, Sing, Kowari, Rizia et beaucoup d’autres', isKakudai: false, stars: 7.12,
      base: { hp: 6, cs: 4.2, ar: 9.8, od: 9.2, mainBpm: 199.98, hasKiai: true, difficultyName: 'Evolution of Planetary Mechanics and the Collapse of Everything We Knew' },
      art: () => paintArt('星', { x: 950, y: 230, size: 460, tilt: 0.12, tone: 220 }),
    },
    yoru: {
      id: '4e5a0000-0000-4000-8000-000000000yoru', artist: 'YOASOBI', title: '夜に駆ける',
      difficultyName: 'Hard', mapper: 'Akitoshi', isKakudai: false, stars: 4.36,
      base: { hp: 4, cs: 4, ar: 8, od: 7, mainBpm: 130, hasKiai: false, difficultyName: 'Hard' },
      art: () => paintArt('夜', { x: 920, y: 240, size: 480, tilt: -0.05, tone: 250 }),
    },
  };
  const startMap = { kakudai: library.kakudai, long: library.long, nokiai: library.yoru }[scenario] || library.satori;

  // ---------------------------------------------------------------- réglages et profils de départ
  const defaults = {
    factor: 1.5, crushStreams: false, accentAngles: false, kiaiOnly: false, rate: 1, lockedBpm: null,
    hp: null, cs: null, ar: null, od: null, hpLocked: false, csLocked: false, arLocked: false, odLocked: false,
    flipMap: false, flipMode: 'vertical', emulateHr: false, scaleAr: true, scaleOd: true, changePitch: false,
  };
  const startSettings = {
    nothing: { factor: 1 },
    locked: {
      factor: 2, emulateHr: true, flipMap: true, flipMode: 'both', lockedBpm: 200, ar: 9.5, arLocked: true, scaleAr: false,
      od: 9, scaleOd: false,
    },
    nomaplocked: { factor: 2, lockedBpm: 200, ar: 9.5, arLocked: true, scaleAr: false, hp: 7 },
    nokiai: { kiaiOnly: true, factor: 2 },
  }[scenario] || { rate: 1.1 };
  const current = { ...defaults, ...startSettings };
  const profiles = [
    { name: null, settings: { ...defaults, factor: 2, lockedBpm: 176 } },
    { name: 'Streams ×2', settings: { ...defaults, factor: 2, rate: 1.2 } },
    { name: null, settings: { ...defaults, rate: 1.3 } },
    { name: null, settings: null },
  ];

  // ---------------------------------------------------------------- Trainer.Resolve (copie du C#)
  const MAX_STAT = 10, MAX_AR_OD = 11;
  const clamp = (x, lo, hi) => Math.min(hi, Math.max(lo, x));
  const round1 = (x) => Math.sign(x) * Math.round(Math.abs(x) * 10) / 10;
  const round2 = (x) => Math.sign(x) * Math.round(Math.abs(x) * 100) / 100;
  const same = (a, b) => round1(a) === round1(b);
  const arToMs = (ar) => (ar <= 5 ? 1800 - 120 * ar : 1200 - 150 * (ar - 5));
  const scaleAr = (ar, rate) => {
    const ms = arToMs(ar) / rate;
    return clamp(round1(ms >= 1200 ? (1800 - ms) / 120 : 5 + (1200 - ms) / 150), 0, MAX_AR_OD);
  };
  const scaleOd = (od, rate) => clamp(round1((79.5 - (79.5 - 6 * od) / rate) / 6), 0, MAX_AR_OD);

  const hrOf = (v, f) => (f === 1 || v >= MAX_STAT ? v : round1(Math.min(v * f, MAX_STAT)));
  function automatic(map, f, max, scale) {
    let v = hrOf(clamp(map, 0, max), f);
    if (scale) {
      const scaled = scale(v);
      if (Math.abs(scaled - v) > 1e-9) v = scaled;
    }
    return clamp(v, 0, max);
  }
  function stat(map, hrFactor, max, manual, scale) {
    const auto = automatic(map, 1, max, scale);
    const expected = hrFactor === 1 ? auto : automatic(map, hrFactor, max, scale);
    if (manual === null) return { map, expected, defined: auto, final: expected };
    const defined = clamp(manual, 0, max);
    return { map, expected, defined, final: clamp(hrOf(defined, hrFactor), 0, max) };
  }

  const num = (x, d) => String(Number(round2(x).toFixed(d)));

  function resolve(base, s) {
    let rate = s.lockedBpm !== null && base.mainBpm > 0 ? clamp(s.lockedBpm / base.mainBpm, 0.5, 2) : clamp(s.rate, 0.5, 2);
    rate = round2(rate);
    const hr = s.emulateHr;
    const cs = stat(base.cs, hr ? 1.3 : 1, MAX_STAT, s.cs, null);
    const hp = stat(base.hp, hr ? 1.4 : 1, MAX_STAT, s.hp, null);
    const ar = stat(base.ar, hr ? 1.4 : 1, MAX_AR_OD, s.ar, s.scaleAr ? (v) => scaleAr(v, rate) : null);
    const od = stat(base.od, hr ? 1.4 : 1, MAX_AR_OD, s.od, s.scaleOd ? (v) => scaleOd(v, rate) : null);
    const bpm = base.mainBpm * rate;
    const nothingToChange = s.factor === 1 && rate === 1 && !s.flipMap && !s.accentAngles
      && same(hp.map, hp.final) && same(cs.map, cs.final) && same(ar.map, ar.final) && same(od.map, od.final);

    const parts = [];
    const spaced = s.factor > 1;
    if (spaced) parts.push(`${num(s.factor, 2)}x spaced`);
    if (spaced && s.crushStreams) parts.push('(no streams)');
    if (s.accentAngles) parts.push('(angles)');
    if ((spaced || s.accentAngles) && s.kiaiOnly) parts.push('(kiai)');
    if (rate !== 1) {
      parts.push(`${num(rate, 2)}x (${Math.round(bpm)}bpm)`);
      if (s.changePitch) parts.push('pitch');
    }
    if (hr) parts.push('HR');
    if (s.flipMap) parts.push({ horizontal: 'hflip', both: 'hvflip' }[s.flipMode] || 'vflip');
    for (const [label, st] of [['HP', hp], ['CS', cs], ['AR', ar], ['OD', od]]) {
      if (!same(st.final, st.expected)) parts.push(label + String(round1(st.final)));
    }
    if (base.difficultyName) parts.push(base.difficultyName);

    return { rate, bpm, hp, cs, ar, od, version: parts.join(' '), nothingToChange, maxArOd: MAX_AR_OD };
  }

  // ---------------------------------------------------------------- star rating (factice)
  // Ce qui change le star rating dans l'appli (espacement, rate, CS, AR, OD), avec des poids plausibles. Rien à
  // changer : celui de la map ; « Kiai only » sans kiai : inconnu (le C# refuserait de générer).
  function stars(m, s) {
    const r = resolve(m.base, s);
    if (r.nothingToChange) return m.stars;
    if (s.kiaiOnly && !m.base.hasKiai && (s.factor > 1 || s.accentAngles)) return null;
    const aim = Math.pow(s.factor, 0.42) * (1 + 0.05 * (r.cs.final - r.cs.map));
    const read = 1 + 0.03 * Math.max(0, r.ar.final - 10.3);
    return m.stars * Math.pow(r.rate, 1.15) * aim * read;
  }

  // ---------------------------------------------------------------- « C# »
  let shown = null;
  let settings = { ...current };
  let starsTimer = 0;

  function mapMessage(m) {
    return {
      type: 'map', id: m.id, artist: m.artist, title: m.title, difficultyName: m.difficultyName, mapper: m.mapper,
      isKakudai: m.isKakudai, background: m.art ? (m._bg ||= m.art()) : null, base: m.base,
    };
  }

  const LONG_ERROR = 'Could not write the .osz to "C:\\Users\\you\\Kakudai\\generated\\Camellia - Exit This Earth’s '
    + 'Atomosphere (Nao Tomori) [2x spaced Evolution of Planetary Mechanics] (kakudai).osz" (there is not enough space '
    + 'on the disk).';

  async function receive(msg) {
    switch (msg && msg.type) {
      case 'ready': {
        await wait(20);
        const waiting = scenario === 'nomap' || scenario === 'nomaplocked';
        emit({ type: 'status', client: waiting ? null : 'torii', state: waiting ? 'waiting' : 'detected' });
        emit({ type: 'appSettings', current, profiles });
        if (params.has('update')) setTimeout(() => emit({ type: 'update', version: '2.1.0' }), 60);
        if (waiting) { emit({ type: 'noMap' }); break; }
        shown = startMap;
        emit(mapMessage(shown));
        if (scenario === 'unsupported') {
          await wait(400);
          emit({ type: 'status', client: 'torii', state: 'unsupported' });
        }
        break;
      }
      case 'openUpdate':
        console.info('Kakudai (faux hôte) : ouverture de la page de la release demandée (rien ne s’ouvre ici)');
        break;
      case 'settings': {
        settings = msg.settings;
        if (!shown || (msg.mapId && msg.mapId !== shown.id)) break;   // pas de réponse, comme le C#
        const m = shown;
        // Comme le C# : 300 ms après le dernier changement, le plus récent seulement.
        clearTimeout(starsTimer);
        if (scenario !== 'nostars') {
          starsTimer = setTimeout(() => {
            if (shown !== m) return;
            emit({ type: 'stars', mapId: m.id, settings: msg.settings, value: stars(m, msg.settings), map: m.stars });
          }, 300 + 80);
        }
        await wait(30);
        emit({ type: 'resolved', mapId: m.id, settings: msg.settings, ...resolve(m.base, msg.settings) });
        break;
      }
      case 'profiles':
        console.info('Kakudai (faux hôte) : profils enregistrés', msg.profiles);
        break;
      case 'generate': {
        const m = shown;
        if (scenario === 'generating') break;   // la génération ne se termine jamais
        await wait(900);
        if (!m || msg.mapId !== m.id) { emit({ type: 'error', source: 'generate', message: 'The map changed, try again.' }); break; }
        const r = resolve(m.base, msg.settings);
        if (r.nothingToChange) {
          emit({ type: 'error', source: 'generate', message: 'Nothing to change: the difficulty would be the same as the map\'s.' });
        } else if (msg.settings.kiaiOnly && !m.base.hasKiai && (msg.settings.factor > 1 || msg.settings.accentAngles)) {
          emit({ type: 'error', source: 'generate', message: 'This map has no kiai: turn off "Kiai only".' });
        } else if (scenario === 'error') {
          emit({ type: 'error', source: 'generate', message: 'Could not convert the audio "audio.mp3": unknown format (BASS error FileFormat).' });
        } else if (scenario === 'long') {
          emit({ type: 'error', source: 'generate', message: LONG_ERROR });
        } else {
          emit({ type: 'generated', title: m.title, version: r.version });
        }
        break;
      }
      case 'window': {
        windowLog.push(msg);
        console.info('Kakudai (faux hôte) : fenêtre → %s %s', msg.action, msg.value ?? '');
        if (msg.action === 'height') document.documentElement.dataset.requestedHeight = String(msg.value);
        break;
      }
      default: break;
    }
  }

  // Pilote automatique des états qui demandent un geste, pour les captures sans interaction.
  async function autopilot() {
    await wait(300);
    const click = (selector) => document.querySelector(selector).click();
    switch (scenario) {
      case 'generating': case 'error': case 'done': click('#generate'); break;
      case 'long': case 'nokiai': click('#generate'); break;
      case 'locked': document.getElementById('ar').focus(); break;
      case 'rename': {
        click('.profile:nth-child(2) [data-act="rename"]');
        const input = document.querySelector('.profile:nth-child(2) .profile__rename');
        input.value = 'Streams ×2.5';
        break;
      }
      default: break;
    }
  }
  if (document.readyState === 'complete') autopilot();
  else window.addEventListener('load', autopilot);

  window.KakudaiMock = {
    postMessage(msg) { receive(JSON.parse(JSON.stringify(msg))); },
    addEventListener(type, fn) { if (type === 'message') listeners.add(fn); },
    removeEventListener(type, fn) { if (type === 'message') listeners.delete(fn); },
    emit,
    library,
    resolve,
    stars,
    windowLog,
    show(name) { shown = library[name]; emit(mapMessage(shown)); },
    get settings() { return settings; },
  };
  console.info('Kakudai: pont factice actif (état « %s »)', scenario);
})();
