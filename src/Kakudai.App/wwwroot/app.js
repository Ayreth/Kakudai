// Kakudai — logique de l'interface (fenêtre compacte façon osu trainer).
// Parle au C# par window.chrome.webview (WebView2). Protocole (camelCase) :
//   JS → C# : {type:"ready"}
//             {type:"settings", settings, mapId?}     à chaque changement ; C# répond "resolved" pour la map
//             {type:"profiles", profiles}             4 emplacements {name, settings|null}
//             {type:"generate", mapId, settings}
//             {type:"window", action, value?}         action ∈ minimize|close|drag|height (value en px CSS)
//             {type:"openUpdate"}                     ouvre la page de la release annoncée (le C# a l'adresse)
//   C# → JS : {type:"status", client, state}          state ∈ waiting|detected|notFound|unsupported|libraryError
//             {type:"appSettings", current, profiles} après "ready"
//             {type:"map", id, artist, title, difficultyName, mapper, isKakudai, background, base}   (isKakudai : ignoré)
//             {type:"noMap"}
//             {type:"resolved", mapId, settings, rate, bpm, hp, cs, ar, od, version, nothingToChange, maxArOd}
//                                                     settings = ceux pour lesquels la réponse a été calculée
//                                                     hp…od = {map, expected, defined, final}
//             {type:"stars", mapId, settings, value, map} star rating de la diff que ces réglages généreraient
//                                                     (null si inconnu) et de la map ; 300 ms après le dernier changement
//             {type:"update", version}                une release plus récente ("2.1.0") : bandeau « Download »
//             {type:"generated", title, version}
//             {type:"error", source, message}         source ∈ detect|settings|profiles|generate|update
// La page garde les réglages (TrainerSettings). Un "resolved" ou un "stars" d'une autre map est ignoré.
// Valeurs des stats : le curseur montre la valeur définie (manuelle, ou automatique sans HR) ; Emulate HR
// s'applique par-dessus, et la case de la valeur ne montre que la valeur écrite.
// Règles des cadenas : verrouiller garde la valeur définie affichée comme valeur manuelle ; verrouiller
// ou bouger AR/OD à la main coupe le Scale correspondant ; sur une nouvelle map, les valeurs manuelles non
// verrouillées repartent à null, un BPM verrouillé reste (lockedBpm).
// Sans hôte WebView2 (navigateur ordinaire), mock.js fournit un faux pont pour développer la page.
(() => {
  'use strict';

  const DEFAULTS = Object.freeze({
    factor: 1.5, crushStreams: false, accentAngles: false, kiaiOnly: false,
    rate: 1, lockedBpm: null,
    hp: null, cs: null, ar: null, od: null,
    hpLocked: false, csLocked: false, arLocked: false, odLocked: false,
    flipMap: false, flipMode: 'vertical', emulateHr: false, scaleAr: true, scaleOd: true, changePitch: false,
  });
  const FLIP_MODES = ['vertical', 'horizontal', 'both'];
  const STATS = ['hp', 'cs', 'ar', 'od'];
  const PROFILE_COUNT = 4;
  const FACTOR_MIN = 1, FACTOR_MAX = 3;
  const RATE_MIN = 0.5, RATE_MAX = 2;
  const STAT_MAX = 10;
  const EMPTY_TITLE = 'Pick a map in the game';
  const UPDATE_DISMISSED_KEY = 'kakudai.updateDismissed';   // version fermée, pour cette session seulement

  const STATUS_TEXT = {
    waiting: 'waiting for the game',
    detected: 'map detected',
    notFound: 'the game’s map was not found',
    unsupported: 'unsupported mode (osu!standard only)',
    libraryError: 'can’t read the game’s database, retrying shortly',
  };

  const $ = (id) => document.getElementById(id);
  const el = {
    app: $('app'),
    banner: $('banner'),
    art: $('art'),
    status: $('status'),
    statusText: $('status-text'),
    winMin: $('win-min'),
    winClose: $('win-close'),
    notice: $('notice'),
    artist: $('artist'),
    title: $('title'),
    diff: $('diff'),
    diffName: $('diff-name'),
    stars: $('stars'),
    starsValue: $('stars-value'),
    starsText: $('stars-text'),
    mapper: $('mapper'),
    factor: $('factor'),
    factorVal: $('factor-val'),
    rate: $('rate'),
    rateField: $('rate-field'),
    bpmWrap: $('bpm-wrap'),
    bpmField: $('bpm-field'),
    bpmVal: document.querySelector('.val--bpm'),
    locks: Array.from(document.querySelectorAll('.lock')),
    toggles: Array.from(document.querySelectorAll('[data-toggle]')),
    flipMode: $('flip-mode'),
    flipOptions: Array.from(document.querySelectorAll('[data-flip]')),
    more: $('more'),
    options: $('options'),
    profiles: $('profiles'),
    generate: $('generate'),
    generateText: $('generate-text'),
    message: $('message'),
    messageText: $('message-text'),
    update: $('update'),
    updateText: $('update-text'),
    updateDownload: $('update-download'),
    updateClose: $('update-close'),
  };
  const statRow = (k) => document.querySelector(`.row[data-stat="${k}"]`);
  const statEls = Object.fromEntries(STATS.map((k) => {
    const row = statRow(k);
    return [k, { row, range: row.querySelector('.range'), val: row.querySelector('.val') }];
  }));
  const lockOf = (k) => el.locks.find((b) => b.dataset.lock === k);

  const state = {
    settings: { ...DEFAULTS },
    profiles: emptyProfiles(),
    map: null,          // dernier message "map"
    resolved: null,     // dernier "resolved" de cette map
    stars: null,        // dernier "stars" de cette map
    update: null,       // version annoncée par "update", tant que le bandeau n'est pas fermé
    status: { client: null, state: 'waiting' },
    maxArOd: 11,
    generating: false,
    message: null,      // {kind: 'ok' | 'error', text}
    renaming: -1,       // emplacement de profil en cours de renommage
  };

  let bridge = null;

  // ------------------------------------------------------------------ nombres

  const isNum = (x) => typeof x === 'number' && Number.isFinite(x);
  const clamp = (x, lo, hi) => Math.min(hi, Math.max(lo, x));
  const round1 = (x) => Math.round(x * 10) / 10;
  const round2 = (x) => Math.round(x * 100) / 100;
  const fmt1 = (x) => (isNum(x) ? round1(x).toFixed(1) : '–');
  const fmtRate = (x) => (isNum(x) ? round2(x).toFixed(2) : '–');
  const fmtBpm = (x) => (isNum(x) ? String(Math.round(x)) : '–');
  const fmtFactor = (f) => '×' + f.toFixed(2);
  const fmtStars = (x) => (isNum(x) ? round2(x).toFixed(2) : '–');
  const shortNum = (x) => String(Number(x.toFixed(2)));   // 1.1, 2, 2.25
  const differs = (a, b) => isNum(a) && isNum(b) && Math.abs(round1(a) - round1(b)) >= 0.05;
  const clampFactor = (f) => clamp(Math.round(f * 20) / 20, FACTOR_MIN, FACTOR_MAX);
  const clampRate = (r) => clamp(round2(r), RATE_MIN, RATE_MAX);
  const statMax = (k) => (k === 'ar' || k === 'od' ? state.maxArOd : STAT_MAX);

  // "1,1" comme "1.1" ; rien d'autre qu'un nombre.
  function parseNumber(text) {
    const t = String(text).trim().replace(',', '.').replace(/[x×]$/i, '').trim();
    if (!/^\d+(\.\d*)?$|^\.\d+$/.test(t)) return NaN;
    return Number(t);
  }

  // ------------------------------------------------------------------ réglages

  function normalizeSettings(raw) {
    const s = { ...DEFAULTS };
    if (!raw || typeof raw !== 'object') return s;
    for (const key of Object.keys(DEFAULTS)) {
      const v = raw[key];
      if (typeof DEFAULTS[key] === 'boolean') { if (typeof v === 'boolean') s[key] = v; }
      else if (key === 'flipMode') { if (FLIP_MODES.includes(v)) s[key] = v; }
      else if (DEFAULTS[key] === null) { if (v === null || isNum(v)) s[key] = v; }
      else if (isNum(v)) s[key] = v;
    }
    s.factor = clampFactor(s.factor);
    s.rate = clampRate(s.rate);
    if (s.lockedBpm !== null && !(s.lockedBpm > 0)) s.lockedBpm = null;
    for (const k of STATS) {
      if (s[k] !== null) s[k] = clamp(round1(s[k]), 0, statMax(k));
      if (s[k] === null) s[k + 'Locked'] = false;   // un cadenas sans valeur ne garde rien
    }
    return s;
  }

  function emptyProfiles() {
    return Array.from({ length: PROFILE_COUNT }, () => ({ name: null, settings: null }));
  }

  function normalizeProfiles(raw) {
    const out = emptyProfiles();
    if (!Array.isArray(raw)) return out;
    raw.slice(0, PROFILE_COUNT).forEach((p, i) => {
      if (!p || typeof p !== 'object') return;
      out[i] = {
        name: typeof p.name === 'string' && p.name.trim() ? p.name.trim() : null,
        settings: p.settings && typeof p.settings === 'object' ? normalizeSettings(p.settings) : null,
      };
    });
    return out;
  }

  // Règle du changement de map : les valeurs manuelles non verrouillées repartent de la map.
  // Le rate est gardé tel quel ; un BPM verrouillé reste (le C# en déduit le rate).
  function dropUnlockedValues(s) {
    for (const k of STATS) if (!s[k + 'Locked']) s[k] = null;
  }

  const mainBpm = () => (state.map && state.map.base && state.map.base.mainBpm > 0 ? state.map.base.mainBpm : null);

  // Rate et BPM affichés (même calcul que le C#, pour que les champs suivent sans attendre la réponse).
  function shownRate() {
    const s = state.settings;
    if (s.lockedBpm === null) return s.rate;
    const bpm = mainBpm();
    return bpm ? clampRate(s.lockedBpm / bpm) : (state.map ? s.rate : null);
  }
  function shownBpm() {
    const bpm = mainBpm();
    if (bpm) return bpm * shownRate();
    return state.map ? null : state.settings.lockedBpm;
  }

  // Valeur définie d'une stat (celle du curseur) : la valeur manuelle tout de suite, sinon celle du C#. Sans map,
  // seule une valeur verrouillée s'affiche : une valeur manuelle non verrouillée ne vaut que pour sa map.
  const manualShown = (k) => state.settings[k] !== null && (!!state.map || state.settings[k + 'Locked']);
  function shownStat(k) {
    if (manualShown(k)) return state.settings[k];
    return state.map && state.resolved ? state.resolved[k].defined : null;
  }

  // La réponse du C# correspond-elle aux réglages affichés ? (elle renvoie les réglages pour lesquels elle a été
  // calculée ; une réponse plus ancienne peut arriver pendant qu'on bouge un curseur).
  function resolvedIsCurrent() { return isForCurrentSettings(state.resolved); }
  function isForCurrentSettings(r) {
    if (!r || !r.settings || typeof r.settings !== 'object') return false;
    return Object.keys(DEFAULTS).every((key) => r.settings[key] === state.settings[key]);
  }

  // Valeur écrite : toujours celle du C# (seul endroit où vivent les formules HR). En attendant la réponse aux
  // réglages affichés, une valeur manuelle s'affiche telle quelle, sans flèche.
  function writtenStat(k) {
    if (!state.map || !state.resolved) return manualShown(k) ? state.settings[k] : null;
    if (resolvedIsCurrent() || !manualShown(k)) return state.resolved[k].final;
    return state.settings[k];
  }
  const mapStat = (k) => (state.map && state.map.base && isNum(state.map.base[k]) ? state.map.base[k] : null);

  // ------------------------------------------------------------------ pont

  function send(msg) {
    if (!bridge) return;
    try { bridge.postMessage(msg); } catch (err) { console.error('Kakudai: envoi impossible', err); }
  }

  // Un seul envoi pour les changements faits ensemble (même tâche) ; le C# ne résout que la dernière demande.
  let settingsQueued = false;
  function sendSettings() {
    if (settingsQueued) return;
    settingsQueued = true;
    queueMicrotask(() => {
      settingsQueued = false;
      const msg = { type: 'settings', settings: { ...state.settings } };
      if (state.map) msg.mapId = state.map.id;
      send(msg);
    });
  }

  function sendProfiles() {
    send({ type: 'profiles', profiles: state.profiles.map((p) => ({ name: p.name, settings: p.settings && { ...p.settings } })) });
  }

  function handle(raw) {
    let msg = raw;
    if (typeof msg === 'string') {
      try { msg = JSON.parse(msg); } catch { return; }
    }
    if (!msg || typeof msg !== 'object') return;
    switch (msg.type) {
      case 'status': onStatus(msg); break;
      case 'appSettings': onAppSettings(msg); break;
      case 'map': onMap(msg); break;
      case 'noMap': onNoMap(); break;
      case 'resolved': onResolved(msg); break;
      case 'stars': onStars(msg); break;
      case 'update': onUpdate(msg); break;
      case 'generated': onGenerated(); break;
      case 'error': onError(msg.source, msg.message); break;
      default: break;
    }
  }

  function onStatus(msg) {
    state.status = { client: typeof msg.client === 'string' ? msg.client : null, state: STATUS_TEXT[msg.state] ? msg.state : 'waiting' };
    render();
  }

  function onAppSettings(msg) {
    state.settings = normalizeSettings(msg.current);
    state.profiles = normalizeProfiles(msg.profiles);
    state.renaming = -1;
    renderProfiles();
    render();
    if (state.map) sendSettings();
  }

  function onMap(m) {
    if (m.id == null) return;
    const sameMap = state.map && String(state.map.id) === String(m.id);
    state.map = {
      id: String(m.id),
      artist: m.artist || '',
      title: m.title || '',
      difficultyName: m.difficultyName || '',
      mapper: m.mapper || '',
      background: typeof m.background === 'string' ? m.background : null,
      base: m.base && typeof m.base === 'object' ? m.base : null,
    };
    if (!sameMap) {
      state.resolved = null;
      state.stars = null;
      dropUnlockedValues(state.settings);
      if (!state.generating) state.message = null;
    }
    setBackground(state.map.background);
    render();
    sendSettings();
  }

  function onNoMap() {
    state.map = null;
    state.resolved = null;
    state.stars = null;
    setBackground(null);
    render();
  }

  function onResolved(r) {
    if (!state.map || String(r.mapId) !== state.map.id) return;   // réponse pour une autre map : périmée
    if (!STATS.every((k) => r[k] && isNum(r[k].final) && isNum(r[k].defined))) return;
    state.resolved = r;
    if (isNum(r.maxArOd) && r.maxArOd > 0) state.maxArOd = r.maxArOd;
    render();
  }

  function onStars(msg) {
    if (!state.map || String(msg.mapId) !== state.map.id) return;   // pour une autre map : périmé
    state.stars = {
      settings: msg.settings,
      value: isNum(msg.value) ? msg.value : null,
      map: isNum(msg.map) ? msg.map : null,
    };
    render();
  }

  function onUpdate(msg) {
    if (typeof msg.version !== 'string' || !/^\d+\.\d+\.\d+$/.test(msg.version)) return;
    let dismissed = null;
    try { dismissed = sessionStorage.getItem(UPDATE_DISMISSED_KEY); } catch { /* stockage indisponible */ }
    state.update = dismissed === msg.version ? null : msg.version;
    render();
  }

  function onGenerated() {
    state.generating = false;
    state.message = { kind: 'ok', text: 'Opened in the game.' };
    render();
  }

  function onError(source, message) {
    if (source === 'generate') state.generating = false;
    state.message = { kind: 'error', text: message || 'Something went wrong.' };
    render();
  }

  // ------------------------------------------------------------------ rendu

  function render() {
    renderBanner();
    renderUpdate();
    renderFactor();
    renderRate();
    STATS.forEach(renderStat);
    renderToggles();
    renderProfileLabels();
    renderGenerate();
    renderMessage();
    reportHeight();
  }

  function renderBanner() {
    const m = state.map;
    const st = state.status.state;
    const statusText = st === 'detected' && state.status.client
      ? `${state.status.client} · ${STATUS_TEXT.detected}` : STATUS_TEXT[st];
    el.status.dataset.state = st;
    el.status.title = statusText;
    el.statusText.textContent = statusText;

    let notice = '';
    if (m && st !== 'detected' && st !== 'waiting') notice = STATUS_TEXT[st];
    el.notice.textContent = notice;
    el.notice.hidden = !notice;

    if (m) {
      el.artist.textContent = m.artist;
      el.artist.title = m.artist;
      setTitle(m.title || 'Untitled');
      el.diffName.textContent = m.difficultyName ? `[${m.difficultyName}]` : '';
      el.mapper.textContent = m.mapper || '–';
      el.diff.title = [m.difficultyName && `[${m.difficultyName}]`, m.mapper && `by ${m.mapper}`].filter(Boolean).join(' ');
      el.diff.hidden = false;
      renderStars();
      document.title = m.title ? `${m.title} · Kakudai` : 'Kakudai';
    } else {
      // Sans map, la ligne de l'artiste dit où en est la détection.
      el.artist.textContent = st === 'detected' ? '' : STATUS_TEXT[st];
      el.artist.title = '';
      setTitle(EMPTY_TITLE);
      el.diff.hidden = true;
      document.title = 'Kakudai';
    }
  }

  // Star rating de la diff qui serait générée : seulement la nouvelle valeur, en couleur d'accent si elle diffère
  // de la map (dont la valeur apparaît au survol). Pendant le calcul qui suit un changement, la dernière valeur de
  // cette map reste affichée, atténuée ; « – » tant qu'aucune n'est connue.
  function renderStars() {
    const st = state.stars;
    const value = st ? st.value : null;
    const known = isNum(value);
    const changed = known && isNum(st.map) && Math.abs(round2(value) - round2(st.map)) >= 0.005;
    el.starsValue.textContent = fmtStars(value);
    el.stars.classList.toggle('is-blank', !known);
    el.stars.classList.toggle('is-changed', changed);
    el.stars.classList.toggle('is-pending', known && !isForCurrentSettings(st));
    if (changed) el.stars.dataset.map = fmtStars(st.map);
    else delete el.stars.dataset.map;
    // Pour les lecteurs d'écran, le SR de la map est dit dans le texte (l'infobulle n'est qu'au survol).
    el.starsText.textContent = !known ? 'Star rating unknown'
      : changed ? `Star rating ${fmtStars(value)}, map ${fmtStars(st.map)}` : `Star rating ${fmtStars(value)}`;
  }

  function renderUpdate() {
    el.update.hidden = !state.update;
    if (state.update) el.updateText.textContent = `Kakudai v${state.update} is available`;
  }

  // Titre sur une ligne ; trop long, il passe plus petit sur deux lignes (texte entier en infobulle).
  function setTitle(text) {
    if (el.title.textContent !== text) el.title.textContent = text;
    el.title.title = text;
    fitTitle();
  }
  function fitTitle() {
    el.title.classList.remove('is-long');
    if (el.title.scrollWidth > el.title.clientWidth + 1) el.title.classList.add('is-long');
  }

  // Bulle « map X » : juste à gauche du bouton du curseur, ou à sa droite quand il est tout au début.
  function placeTip(row, input, fraction) {
    const x = input.offsetLeft + 7 + fraction * (input.offsetWidth - 14);   // centre du bouton (14 px)
    row.style.setProperty('--tip-x', `${x}px`);
    row.classList.toggle('tip-right', x - input.offsetLeft < 70);
  }

  function setRange(input, value, min, max) {
    input.classList.toggle('is-unknown', !isNum(value));
    if (isNum(value)) {
      if (Number(input.value) !== value) input.value = String(value);
      input.style.setProperty('--fill', `${((clamp(value, min, max) - min) / (max - min)) * 100}%`);
    } else {
      input.style.setProperty('--fill', '0%');
    }
  }

  // L'espacement reste gris : ce n'est pas une valeur de la map (choix du joueur, §11).
  function renderFactor() {
    const f = state.settings.factor;
    setRange(el.factor, f, FACTOR_MIN, FACTOR_MAX);
    el.factor.setAttribute('aria-valuetext', fmtFactor(f));
    el.factorVal.textContent = fmtFactor(f);
  }

  function renderRate() {
    const s = state.settings;
    const rate = shownRate();
    const bpm = shownBpm();
    const locked = s.lockedBpm !== null;
    const rateKnown = isNum(rate);

    el.rate.disabled = !rateKnown;
    setRange(el.rate, rateKnown ? rate : null, RATE_MIN, RATE_MAX);
    el.rate.classList.toggle('is-changed', rateKnown && Math.abs(rate - 1) > 1e-9);
    el.rate.setAttribute('aria-valuetext', rateKnown ? `${fmtRate(rate)}x` : 'unknown');
    setField(el.rateField, fmtRate(rate));
    el.rateField.disabled = !rateKnown;

    // Le BPM ne s'édite que si on peut en déduire un rate (map chargée) ou s'il est verrouillé.
    setField(el.bpmField, fmtBpm(bpm));
    el.bpmField.disabled = !(mainBpm() || (!state.map && locked));
    el.bpmWrap.classList.toggle('is-blank', !isNum(bpm));
    el.bpmWrap.classList.toggle('is-changed', !!mainBpm() && rateKnown && Math.abs(rate - 1) > 1e-9);
    if (mainBpm() && rateKnown && Math.abs(rate - 1) > 1e-9) {
      el.bpmVal.dataset.map = fmtBpm(mainBpm());
      placeTip(el.rate.closest('.row'), el.rate, (clamp(rate, RATE_MIN, RATE_MAX) - RATE_MIN) / (RATE_MAX - RATE_MIN));
    } else delete el.bpmVal.dataset.map;

    renderLock(lockOf('bpm'), locked, locked || !!mainBpm(), 'the BPM');
  }

  function setField(input, text) {
    if (document.activeElement !== input && input.value !== text) input.value = text;
  }

  function renderStat(k) {
    const { row, range, val } = statEls[k];
    const s = state.settings;
    const value = shownStat(k);
    const written = writtenStat(k);
    const known = isNum(value);
    const max = statMax(k);
    const mapValue = mapStat(k);
    const shown = known && isNum(written) ? written : value;   // la case montre la valeur écrite
    const changed = !!state.map && known && differs(shown, mapValue);

    range.max = String(max);
    range.disabled = !known;
    setRange(range, known ? round1(value) : null, 0, max);
    range.classList.toggle('is-changed', changed);
    range.setAttribute('aria-valuetext', !known ? 'unknown'
      : differs(shown, value) ? `${fmt1(value)}, written ${fmt1(shown)}` : fmt1(value));
    row.classList.toggle('is-off', !known);
    val.textContent = fmt1(known ? shown : null);
    val.classList.toggle('is-blank', !known);
    val.classList.toggle('is-changed', changed);
    if (changed) {
      val.dataset.map = fmt1(mapValue);
      placeTip(row, range, clamp(value, 0, max) / max);
    } else delete val.dataset.map;

    renderLock(lockOf(k), s[k + 'Locked'], s[k + 'Locked'] || known, k.toUpperCase());
  }

  function renderLock(button, locked, enabled, what) {
    button.setAttribute('aria-pressed', String(locked));
    button.disabled = !enabled;
    const label = locked ? `Unlock ${what}` : `Lock ${what}: keep this value from one map to the next`;
    button.setAttribute('aria-label', label);
    button.title = label;
  }

  function renderToggles() {
    const s = state.settings;
    el.toggles.forEach((input) => {
      const key = input.dataset.toggle;
      let disabled = false;
      let title = '';
      if (key === 'scaleAr' && s.arLocked) { disabled = true; title = 'AR is locked'; }
      if (key === 'scaleOd' && s.odLocked) { disabled = true; title = 'OD is locked'; }
      if (key === 'kiaiOnly' && state.map && state.map.base && state.map.base.hasKiai === false) title = 'This map has no kiai';
      input.checked = s[key];
      input.disabled = disabled;
      input.closest('label').title = title;
    });
    el.flipMode.classList.toggle('is-off', !s.flipMap);
    el.flipOptions.forEach((b) => b.setAttribute('aria-checked', String(b.dataset.flip === s.flipMode)));
  }

  function renderGenerate() {
    const nothing = !!(state.map && state.resolved && state.resolved.nothingToChange);
    el.generate.setAttribute('aria-busy', String(state.generating));
    el.generate.disabled = !state.map || state.generating || nothing;
    el.generateText.textContent = state.generating ? 'Generating…' : nothing ? 'Nothing to change' : 'Generate';
  }

  function renderMessage() {
    let kind = '';
    let text = '';
    if (state.message) ({ kind, text } = state.message);
    el.message.dataset.kind = kind;
    el.messageText.textContent = text;
    el.message.title = kind === 'error' ? '' : text;
  }

  // ------------------------------------------------------------------ fond de la map

  // Deux calques d'image pour un fondu enchaîné entre deux fonds.
  let bgToken = 0;
  function setBackground(url) {
    const imgs = el.art.querySelectorAll('.art__img');
    const current = Array.from(imgs).find((i) => i.classList.contains('is-shown'));
    if (url && current && current.dataset.src === url) return;
    const token = ++bgToken;

    if (!url) {
      imgs.forEach((i) => i.classList.remove('is-shown'));
      el.art.dataset.art = 'none';
      return;
    }
    const next = current === imgs[0] ? imgs[1] : imgs[0];
    next.onload = () => {
      if (token !== bgToken) return;
      imgs.forEach((i) => i.classList.toggle('is-shown', i === next));
      el.art.dataset.art = 'image';
    };
    next.onerror = () => {
      if (token !== bgToken) return;
      imgs.forEach((i) => i.classList.remove('is-shown'));
      el.art.dataset.art = 'none';
    };
    next.dataset.src = url;
    next.src = url;
  }

  // ------------------------------------------------------------------ actions

  // Après un changement de réglage, « Opened in the game » ne décrit plus ce qui est affiché.
  function changed() {
    if (state.message && state.message.kind === 'ok') state.message = null;
    render();
    sendSettings();
  }

  // Espacement
  el.factor.addEventListener('input', () => {
    state.settings.factor = clampFactor(Number(el.factor.value));
    changed();
  });

  // Rate : le curseur et le champ fixent le rate ; avec le BPM verrouillé, le BPM cible suit.
  function setRate(r) {
    const s = state.settings;
    s.rate = clampRate(r);
    const bpm = mainBpm();
    if (s.lockedBpm !== null && bpm) s.lockedBpm = bpm * s.rate;
  }
  el.rate.addEventListener('input', () => { setRate(Number(el.rate.value)); changed(); });

  function commitRateField() {
    const v = parseNumber(el.rateField.value);
    if (v > 0) setRate(v);
    el.rateField.value = fmtRate(shownRate());   // le rendu ne touche pas un champ focalisé
    changed();
  }

  // Taper un BPM fixe le rate (borné à 0.50–2.00) ; verrouillé, il devient le BPM cible.
  function commitBpmField() {
    const v = parseNumber(el.bpmField.value);
    const s = state.settings;
    const bpm = mainBpm();
    if (v > 0 && bpm) {
      s.rate = clampRate(v / bpm);
      if (s.lockedBpm !== null) s.lockedBpm = clamp(v, bpm * RATE_MIN, bpm * RATE_MAX);
    } else if (v > 0 && !state.map && s.lockedBpm !== null) {
      s.lockedBpm = v;
    }
    el.bpmField.value = fmtBpm(shownBpm());
    changed();
  }

  // Entrée valide, Échap annule : le blur déclenche "change", qui ne doit alors rien valider.
  function wireField(input, commit) {
    let cancelling = false;
    input.addEventListener('change', () => { if (!cancelling) commit(); });
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); commit(); input.select(); }
      else if (e.key === 'Escape') {
        e.preventDefault();
        cancelling = true;
        input.blur();
        cancelling = false;
      }
    });
    input.addEventListener('focus', () => input.select());
    input.addEventListener('blur', () => render());   // texte invalide abandonné : retour à la valeur
  }
  wireField(el.rateField, commitRateField);
  wireField(el.bpmField, commitBpmField);

  // Stats : bouger AR ou OD à la main coupe le Scale correspondant.
  STATS.forEach((k) => {
    const { range } = statEls[k];
    range.addEventListener('input', () => {
      const s = state.settings;
      s[k] = clamp(round1(Number(range.value)), 0, statMax(k));
      if (k === 'ar') s.scaleAr = false;
      if (k === 'od') s.scaleOd = false;
      changed();
    });
    // Double-clic : retour à la valeur automatique (celle de la map passée par les bascules). Le premier clic a
    // déjà bougé le curseur, donc coupé le Scale de AR/OD : on remet le Scale d'avant ce premier clic.
    const scaleKey = { ar: 'scaleAr', od: 'scaleOd' }[k];
    let scaleBefore = null;
    range.addEventListener('mousedown', (e) => {
      if (scaleKey && e.detail === 1) scaleBefore = state.settings[scaleKey];
    });
    range.addEventListener('dblclick', () => {
      state.settings[k] = null;
      state.settings[k + 'Locked'] = false;
      if (scaleKey && scaleBefore !== null) state.settings[scaleKey] = scaleBefore;
      changed();
    });
  });
  el.factor.addEventListener('dblclick', () => { state.settings.factor = FACTOR_MIN; changed(); });
  el.rate.addEventListener('dblclick', () => {
    state.settings.rate = 1;
    state.settings.lockedBpm = null;
    changed();
  });

  // Cadenas : verrouiller garde la valeur affichée ; déverrouiller la laisse en place pour cette map.
  el.locks.forEach((b) => b.addEventListener('click', () => {
    const k = b.dataset.lock;
    const s = state.settings;
    if (k === 'bpm') {
      if (s.lockedBpm !== null) {
        const rate = shownRate();
        if (isNum(rate)) s.rate = rate;
        s.lockedBpm = null;
      } else {
        const bpm = shownBpm();
        if (!isNum(bpm)) return;
        s.lockedBpm = Math.round(bpm);
      }
    } else if (s[k + 'Locked']) {
      s[k + 'Locked'] = false;
    } else {
      const value = shownStat(k);
      if (!isNum(value)) return;
      s[k] = clamp(round1(value), 0, statMax(k));
      s[k + 'Locked'] = true;
      if (k === 'ar') s.scaleAr = false;
      if (k === 'od') s.scaleOd = false;
    }
    changed();
  }));

  // Bascules. Allumer un Scale fait repartir de la map la valeur manuelle non verrouillée qu'il touche, sinon la
  // bascule n'aurait aucun effet visible. Emulate HR s'applique par-dessus les valeurs définies : il n'en efface aucune.
  const TOUCHES = { scaleAr: ['ar'], scaleOd: ['od'] };
  el.toggles.forEach((input) => input.addEventListener('change', () => {
    const key = input.dataset.toggle;
    const s = state.settings;
    s[key] = input.checked;
    for (const k of TOUCHES[key] || []) if (!s[k + 'Locked']) s[k] = null;
    changed();
  }));

  // « More options » : ouvert ou fermé d'un lancement à l'autre (stockage local de la page : le fichier
  // settings.json n'en sait rien). La fenêtre suit la hauteur (voir reportHeight).
  const MORE_KEY = 'kakudai.moreOptions';
  function setMoreOpen(open, remember) {
    el.options.hidden = !open;
    el.more.setAttribute('aria-expanded', String(open));
    if (remember) {
      try { localStorage.setItem(MORE_KEY, open ? 'open' : 'closed'); } catch { /* stockage indisponible : on oublie */ }
    }
    reportHeight();
  }
  el.more.addEventListener('click', () => setMoreOpen(el.options.hidden, true));

  // Sens du flip : choisir un sens allume aussi « Flip map ».
  el.flipOptions.forEach((b) => b.addEventListener('click', () => {
    state.settings.flipMode = b.dataset.flip;
    state.settings.flipMap = true;
    changed();
  }));

  // Mise à jour : le C# ouvre la page de la release ; fermer cache le bandeau pour cette version et cette session.
  el.updateDownload.addEventListener('click', () => send({ type: 'openUpdate' }));
  el.updateClose.addEventListener('click', () => {
    try { sessionStorage.setItem(UPDATE_DISMISSED_KEY, state.update); } catch { /* stockage indisponible */ }
    state.update = null;
    render();
  });

  // Générer
  el.generate.addEventListener('click', () => {
    if (!state.map || state.generating) return;
    if (state.resolved && state.resolved.nothingToChange) return;
    state.generating = true;
    state.message = null;
    render();
    send({ type: 'generate', mapId: state.map.id, settings: { ...state.settings } });
  });

  // ------------------------------------------------------------------ profils

  const lockIcon = $('lock-icon');
  el.locks.forEach((b) => b.appendChild(lockIcon.content.cloneNode(true)));

  const profileEls = [];
  function renderProfiles() {
    if (profileEls.length === 0) {
      const tpl = $('profile-template');
      for (let i = 0; i < PROFILE_COUNT; i++) {
        const node = tpl.content.firstElementChild.cloneNode(true);
        const parts = {
          root: node,
          load: node.querySelector('.profile__load'),
          rename: node.querySelector('.profile__rename'),
          save: node.querySelector('[data-act="save"]'),
          renameLink: node.querySelector('[data-act="rename"]'),
        };
        wireProfile(i, parts);
        profileEls.push(parts);
        el.profiles.appendChild(node);
      }
    }
    renderProfileLabels();
  }

  function profileLabel(p) {
    if (p.name) return p.name;
    if (!p.settings) return 'empty';
    if (p.settings.lockedBpm !== null) return `${Math.round(p.settings.lockedBpm)} bpm`;
    return `${shortNum(p.settings.rate)}x`;
  }

  function renderProfileLabels() {
    profileEls.forEach((parts, i) => {
      const p = state.profiles[i];
      const label = profileLabel(p);
      const renaming = state.renaming === i;
      parts.load.textContent = label;
      parts.load.title = p.settings ? `Load “${label}”` : 'Empty slot: “Save” stores the current settings here';
      parts.load.disabled = !p.settings;
      parts.load.hidden = renaming;
      parts.rename.hidden = !renaming;
      parts.renameLink.disabled = !p.settings && !renaming;
      parts.save.title = `Store the current settings in slot ${i + 1}`;
    });
  }

  function wireProfile(i, parts) {
    parts.load.addEventListener('click', () => {
      const p = state.profiles[i];
      if (!p.settings) return;
      state.settings = normalizeSettings(p.settings);
      dropUnlockedValues(state.settings);   // elles ne valaient que pour la map où elles ont été réglées
      changed();
    });

    let doneTimer = 0;
    parts.save.addEventListener('click', () => {
      state.profiles[i] = { name: state.profiles[i].name, settings: { ...state.settings } };
      sendProfiles();
      renderProfileLabels();
      parts.save.textContent = 'Saved';
      parts.save.classList.add('is-done');
      clearTimeout(doneTimer);
      doneTimer = setTimeout(() => {
        parts.save.textContent = 'Save';
        parts.save.classList.remove('is-done');
      }, 1200);
    });

    parts.renameLink.addEventListener('click', () => {
      if (state.renaming === i) { finishRename(i, true); return; }
      if (state.renaming >= 0) finishRename(state.renaming, true);
      state.renaming = i;
      parts.rename.value = state.profiles[i].name || '';
      parts.rename.placeholder = profileLabel({ ...state.profiles[i], name: null });
      renderProfileLabels();
      parts.rename.focus();
      parts.rename.select();
    });
    parts.renameLink.addEventListener('pointerdown', (e) => {
      if (state.renaming === i) e.preventDefault();   // garde le focus : le clic valide le nom
    });
    parts.rename.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); finishRename(i, true); }
      else if (e.key === 'Escape') { e.preventDefault(); finishRename(i, false); }
    });
    parts.rename.addEventListener('blur', () => { if (state.renaming === i) finishRename(i, true); });
  }

  // Nom vide : l'étiquette par défaut (BPM ou rate du profil) revient.
  function finishRename(i, keep) {
    if (state.renaming !== i) return;
    state.renaming = -1;
    if (keep) {
      const name = profileEls[i].rename.value.trim().slice(0, 24) || null;
      if (name !== state.profiles[i].name) {
        state.profiles[i] = { ...state.profiles[i], name };
        sendProfiles();
      }
    }
    renderProfileLabels();
  }

  // ------------------------------------------------------------------ fenêtre

  const windowAction = (action, value) => send(value === undefined ? { type: 'window', action } : { type: 'window', action, value });
  el.winMin.addEventListener('click', () => windowAction('minimize'));
  el.winClose.addEventListener('click', () => windowAction('close'));

  // La bannière déplace la fenêtre, sauf sur ses boutons.
  el.banner.addEventListener('mousedown', (e) => {
    if (e.button !== 0 || e.detail !== 1) return;
    if (e.target instanceof Element && e.target.closest('button')) return;
    windowAction('drag');
  });

  // La fenêtre prend la hauteur du contenu : au démarrage, et quand « Plus d'options » s'ouvre ou se ferme
  // (ou qu'un long message d'erreur passe sur deux lignes). Mesurée après chaque rendu, et par le ResizeObserver
  // pour ce qui change sans rendu (chargement des polices) ; le ResizeObserver seul ne suffit pas, il attend
  // une image et la page n'en dessine pas tant que la fenêtre est cachée.
  let sentHeight = 0;
  function reportHeight() {
    const h = Math.ceil(el.app.getBoundingClientRect().height);
    if (h > 0 && h !== sentHeight) {
      sentHeight = h;
      windowAction('height', h);
    }
  }
  new ResizeObserver(reportHeight).observe(el.app);

  // La largeur du titre dépend de la police : on la remesure quand Instrument Serif est chargée.
  if (document.fonts) {
    document.fonts.addEventListener('loadingdone', fitTitle);
    document.fonts.load('400 1em "Instrument Serif"').then(fitTitle, () => {});
  }

  // ------------------------------------------------------------------ démarrage

  function start(b) {
    let open = false;
    try { open = localStorage.getItem(MORE_KEY) === 'open'; } catch { /* stockage indisponible : fermé */ }
    setMoreOpen(open, false);
    bridge = b;
    bridge.addEventListener('message', (e) => handle(e.data));
    renderProfiles();
    render();
    send({ type: 'ready' });
  }

  const host = window.chrome && window.chrome.webview;
  if (host) {
    start(host);
  } else {
    // Hors de l'appli : faux pont local (aucune requête réseau), pour développer et tester la page.
    const s = document.createElement('script');
    s.src = 'mock.js';
    s.onload = () => start(window.KakudaiMock);
    s.onerror = () => console.error('Kakudai: hôte WebView2 absent et mock.js introuvable');
    document.head.appendChild(s);
  }
})();
