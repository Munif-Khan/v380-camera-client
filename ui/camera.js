(() => {
  const cfg = JSON.parse(document.getElementById('cameraConfig').textContent);
  const $ = id => document.getElementById(id);

  document.title = cfg.name;
  $('camName').textContent = cfg.name;
  $('infoName').textContent = cfg.name;
  $('infoHost').textContent = cfg.host + ' · port ' + cfg.webPort;
  $('infoDevice').textContent = cfg.deviceId;
  $('infoQuality').textContent = cfg.quality === 'hd' ? 'Higher resolution' : 'Lower resolution';
  $('homeLink').href = cfg.homeUrl;
  if (!cfg.hasPtz) $('ptzBtn').disabled = true;

  /* ---------- capability list ---------- */
  const caps = cfg.capabilities || {};
  const capStates = {
    verified: ['Tested', 'ok'],
    decoded: ['Tested', 'ok'],
    supported: ['Available', 'ok'],
    unverified: ['Not confirmed', 'warn'],
    unknown: ['Not confirmed', 'warn'],
    notImplemented: ['Not available', 'bad'],
    unsupported: ['Not available', 'bad']
  };
  const capRows = [
    ['Live view', 'verified'],
    ['Sound (listen)', caps.audioListen],
    ['Light', caps.light],
    ['Pan and tilt', caps.ptz],
    ['SD-card recordings', caps.sdPlayback],
    ['Motion alarm settings', caps.alarmSettings],
    ['Alarm sensitivity levels', caps.sensitivity]
  ];
  for (const [label, value] of capRows) {
    const state = capStates[value] || ['Unknown', 'warn'];
    const row = document.createElement('div');
    row.className = 'cap';
    const name = document.createElement('span');
    name.textContent = label;
    const chip = document.createElement('span');
    chip.className = 'cap-state ' + state[1];
    chip.textContent = state[0];
    row.append(name, chip);
    $('capList').append(row);
  }
  $('capNote').textContent = 'Tested means the feature was confirmed on this camera. Not confirmed means it may still work, but it has not been checked on this model.';

  const toastEl = $('toast');
  let toastTimer = 0;
  function toast(message) {
    toastEl.textContent = message;
    toastEl.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove('show'), 2600);
  }

  async function post(path) {
    const res = await fetch(path, { method: 'POST', signal: AbortSignal.timeout(15000) });
    if (!res.ok) throw new Error('Camera did not accept the command (' + res.status + ')');
    return res;
  }

  /* ---------- tabs ---------- */
  const tabs = ['live', 'recordings', 'alarm', 'info'];
  function selectTab(name) {
    for (const t of tabs) {
      const button = $('tab-' + t);
      const panel = $('panel-' + t);
      const active = t === name;
      button.setAttribute('aria-selected', String(active));
      panel.classList.toggle('active', active);
      panel.hidden = !active;
    }
    if (name === 'recordings' && !clipList.dataset.loaded) loadClips();
    if (name === 'alarm' && !alarmBody.dataset.loaded) loadAlarm();
  }
  for (const t of tabs) $('tab-' + t).addEventListener('click', () => selectTab(t));

  /* ---------- live view ---------- */
  const frame = $('liveFrame');
  const overlay = $('stageOverlay');
  const clock = $('clock');
  const pill = $('statusPill');
  let liveTimer = 0;
  let liveFailures = 0;

  async function refreshFrame() {
    if (document.hidden) { liveTimer = setTimeout(refreshFrame, 800); return; }
    try {
      const res = await fetch('/snapshot?t=' + Date.now(), { cache: 'no-store', signal: AbortSignal.timeout(6000) });
      if (!res.ok) throw new Error('snapshot ' + res.status);
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const img = new Image();
      await new Promise((resolve, reject) => { img.onload = resolve; img.onerror = reject; img.src = url; });
      const previous = frame.src;
      frame.src = url;
      if (previous.startsWith('blob:')) URL.revokeObjectURL(previous);
      overlay.classList.add('hidden');
      clock.textContent = new Date().toLocaleTimeString();
      liveFailures = 0;
      setStatus('live', 'ok');
    } catch (error) {
      liveFailures++;
      overlay.classList.remove('hidden');
      overlay.textContent = liveFailures > 2 ? 'Camera reconnecting — retrying automatically' : 'Connecting to camera…';
      setStatus('reconnecting', 'warn');
    }
    liveTimer = setTimeout(refreshFrame, 260);
  }
  refreshFrame();

  function setStatus(state, level) {
    pill.textContent = state;
    pill.className = 'pill ' + level;
    $('infoStatus').textContent = state;
  }

  $('reloadBtn').addEventListener('click', () => { liveFailures = 0; location.reload(); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { clearTimeout(liveTimer); refreshFrame(); } });

  /* ---------- sound ---------- */
  const audio = $('audio');
  const soundButtons = [$('soundBtn'), $('soundBtn2')];
  function renderSound() {
    const playing = !audio.paused;
    $('soundBtn').setAttribute('aria-pressed', String(playing));
    $('soundBtn2').textContent = playing ? 'Stop' : 'Play';
  }
  async function toggleSound() {
    if (!audio.paused) {
      // Drop the source so the transcoder stops instead of buffering forever.
      audio.pause();
      audio.removeAttribute('src');
      audio.load();
      renderSound();
      return;
    }
    // WAV keeps the desktop and app builds on one code path; the app expands
    // the camera's A-law into PCM instead of running ffmpeg.
    audio.src = '/audio.wav?t=' + Date.now();
    try { await audio.play(); renderSound(); } catch (error) { toast('The browser blocked audio playback'); }
  }
  soundButtons.forEach(b => b.addEventListener('click', toggleSound));
  audio.addEventListener('play', renderSound);
  audio.addEventListener('pause', renderSound);
  audio.addEventListener('error', () => toast('Audio is not available right now'));

  /* ---------- light ---------- */
  const lightSheet = $('lightSheet');
  if (caps.light === 'unsupported') {
    $('lightBtn').disabled = true;
    $('lightBtn2').disabled = true;
    $('lightHint').textContent = 'Not available on this model';
  }
  function openLight() { lightSheet.showModal(); }
  $('lightBtn').addEventListener('click', openLight);
  $('lightBtn2').addEventListener('click', openLight);
  $('lightClose').addEventListener('click', () => lightSheet.close());
  lightSheet.addEventListener('click', event => { if (event.target === lightSheet) lightSheet.close(); });
  $('lightChoices').querySelectorAll('[data-light]').forEach(button => {
    button.addEventListener('click', async () => {
      const state = button.dataset.light;
      try {
        await post('/api/light/' + state);
        $('lightHint').textContent = 'Last command: ' + (state === 'auto' ? 'automatic' : state);
        lightSheet.close();
        toast('Light: ' + state);
      } catch (error) { toast(error.message); }
    });
  });

  /* ---------- PTZ ---------- */
  const ptzSheet = $('ptzSheet');
  $('ptzBtn').addEventListener('click', () => ptzSheet.showModal());
  $('ptzClose').addEventListener('click', () => ptzSheet.close());
  ptzSheet.addEventListener('click', event => { if (event.target === ptzSheet) ptzSheet.close(); });
  ptzSheet.querySelectorAll('[data-ptz]').forEach(button => {
    button.addEventListener('click', async () => {
      try { await post('/api/ptz/' + button.dataset.ptz); } catch (error) { toast(error.message); }
    });
  });

  /* ---------- snapshot ---------- */
  $('snapBtn').addEventListener('click', async () => {
    try {
      const res = await fetch('/snapshot?t=' + Date.now(), { cache: 'no-store', signal: AbortSignal.timeout(8000) });
      if (!res.ok) throw new Error('Snapshot unavailable');
      const blob = await res.blob();
      const name = cfg.id + '-' + new Date().toISOString().replace(/[:.]/g, '-') + '.jpg';
      if (window.AndroidCameras && window.AndroidCameras.savePhoto) {
        // A WebView cannot download a blob: URL, so the bytes go to the app.
        const dataUrl = await new Promise((resolve, reject) => {
          const reader = new FileReader();
          reader.onload = () => resolve(reader.result);
          reader.onerror = () => reject(new Error('Could not read the photo'));
          reader.readAsDataURL(blob);
        });
        window.AndroidCameras.savePhoto(dataUrl, name);
        toast('Photo saved');
        return;
      }
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = name;
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 4000);
      toast('Photo saved');
    } catch (error) { toast(error.message); }
  });

  /* ---------- fullscreen ---------- */
  $('fullBtn').addEventListener('click', async () => {
    const stage = $('stage');
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else {
        await stage.requestFullscreen({ navigationUI: 'hide' });
        // Phones can rotate the view; desktop browsers reject this quietly.
        if (screen.orientation && screen.orientation.lock) {
          screen.orientation.lock('landscape').catch(() => {});
        }
      }
    } catch (error) { toast('Full screen is not available'); }
  });

  document.addEventListener('fullscreenchange', () => {
    if (!document.fullscreenElement && screen.orientation && screen.orientation.unlock) {
      try { screen.orientation.unlock(); } catch (error) {}
    }
  });

  $('stageExit').addEventListener('click', () => {
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  });

  /* ---------- recordings ---------- */
  const clipDate = $('clipDate');
  const clipList = $('clipList');
  const clipStatus = $('clipStatus');
  const player = $('player');
  const video = $('clipVideo');
  const playerSave = $('playerSave');
  let playingTile = null;

  clipDate.value = new Date().toLocaleDateString('en-CA');

  function clipUrl(clip, download) {
    const query = new URLSearchParams({ id: clip.Id, start: clip.StartRaw, duration: clip.DurationSeconds });
    if (download) query.set('download', '1');
    return '/api/clip?' + query.toString();
  }

  function timeOfDay(iso, seconds) {
    return seconds ? iso.slice(11, 19) : iso.slice(11, 16);
  }

  function shortDuration(total) {
    const s = Math.max(0, Math.round(total));
    return String(Math.floor(s / 60)).padStart(2, '0') + "'" + String(s % 60).padStart(2, '0') + '"';
  }

  function hourLabel(hour) {
    const pad = n => String(n).padStart(2, '0');
    return pad(hour) + ':00–' + pad((hour + 1) % 24) + ':00';
  }

  function stopPlayback() {
    video.pause();
    video.removeAttribute('src');
    video.load();
    player.hidden = true;
    if (playingTile) playingTile.classList.remove('active');
    playingTile = null;
  }

  function playClip(clip, tile) {
    if (playingTile) playingTile.classList.remove('active');
    playingTile = tile || null;
    if (tile) tile.classList.add('active');
    player.hidden = false;
    $('playerTitle').textContent = timeOfDay(clip.StartLocal, true) + ' · ' + shortDuration(clip.DurationSeconds);
    playerSave.href = clipUrl(clip, true);
    playerSave.setAttribute('download', cfg.id + '-' + clip.StartLocal.replace(/[:.]/g, '-') + '.mp4');
    video.removeAttribute('src');
    video.load();
    video.src = clipUrl(clip, false);
    video.play().catch(() => {});
    player.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  $('closePlayer').addEventListener('click', stopPlayback);

  function tile(clip) {
    const cell = document.createElement('div');
    cell.className = 'clip-cell';
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'clip-tile';
    button.setAttribute('aria-label', 'Play clip from ' + timeOfDay(clip.StartLocal, true));
    const duration = document.createElement('span');
    duration.className = 'dur';
    duration.textContent = shortDuration(clip.DurationSeconds);
    const glyph = document.createElement('span');
    glyph.className = 'glyph';
    glyph.textContent = '\u25B6';
    button.append(glyph, duration);
    button.addEventListener('click', () => playClip(clip, button));
    const time = document.createElement('span');
    time.className = 'time';
    time.textContent = timeOfDay(clip.StartLocal, false);
    cell.append(button, time);
    return cell;
  }

  async function loadClips() {
    clipList.dataset.loaded = '1';
    stopPlayback();
    clipList.replaceChildren();
    clipStatus.textContent = 'Searching the camera card…';
    try {
      const res = await fetch('/api/recordings?date=' + encodeURIComponent(clipDate.value), { signal: AbortSignal.timeout(40000) });
      const data = await res.json();
      if (!res.ok) throw new Error(data.detail || 'Search failed');
      const clips = (data.Recordings || []).slice().sort((a, b) => b.StartRaw - a.StartRaw);
      clipStatus.textContent = clips.length
        ? clips.length + (clips.length === 1 ? ' clip' : ' clips') + '. Times use the camera clock.'
        : 'No event clips on that date.';

      const groups = new Map();
      for (const clip of clips) {
        const hour = Number(clip.StartLocal.slice(11, 13));
        if (!groups.has(hour)) groups.set(hour, []);
        groups.get(hour).push(clip);
      }
      for (const [hour, items] of groups) {
        const section = document.createElement('section');
        section.className = 'hour-group';
        const heading = document.createElement('h3');
        heading.textContent = hourLabel(hour);
        const tiles = document.createElement('div');
        tiles.className = 'clip-tiles';
        for (const clip of items) tiles.append(tile(clip));
        section.append(heading, tiles);
        clipList.append(section);
      }
    } catch (error) {
      clipList.dataset.loaded = '';
      clipStatus.textContent = 'Could not read the card: ' + error.message;
    }
  }

  function shiftDay(days) {
    const d = new Date(clipDate.value + 'T12:00:00');
    if (Number.isNaN(d.getTime())) return;
    d.setDate(d.getDate() + days);
    clipDate.value = d.toLocaleDateString('en-CA');
    loadClips();
  }

  $('prevDay').addEventListener('click', () => shiftDay(-1));
  $('nextDay').addEventListener('click', () => shiftDay(1));
  $('findClips').addEventListener('click', loadClips);
  clipDate.addEventListener('change', loadClips);

  /* ---------- alarm ---------- */
  const alarmStatus = $('alarmStatus');
  const alarmBody = $('alarmBody');
  const alarmMotion = $('alarmMotion');
  const alarmGrid = $('alarmGrid');
  let alarmData = null;
  let originalCells = [];

  function windowText(data) {
    const pad = n => String(n).padStart(2, '0');
    const windows = [data.Window1, data.Window2, data.Window3]
      .filter(w => w && w.Enabled)
      .map(w => pad(w.StartHour) + ':' + pad(w.StartMinute) + '–' + pad(w.EndHour) + ':' + pad(w.EndMinute));
    return windows.length ? 'Scheduled ' + windows.join(' · ') : 'Alarm schedule: off';
  }

  function renderAlarm(data) {
    alarmData = data;
    alarmMotion.checked = !!data.MotionAlarmSwitch;
    $('alarmWindow').textContent = windowText(data);
    alarmGrid.style.gridTemplateColumns = 'repeat(' + data.Columns + ', 1fr)';
    alarmGrid.replaceChildren();
    data.Cells.forEach((value, index) => {
      const cell = document.createElement('button');
      cell.type = 'button';
      cell.className = value === 1 ? 'on' : '';
      cell.setAttribute('aria-label', 'Row ' + (Math.floor(index / data.Columns) + 1) + ', column ' + (index % data.Columns + 1));
      cell.setAttribute('aria-pressed', String(value === 1));
      cell.addEventListener('click', () => {
        const on = !cell.classList.contains('on');
        cell.classList.toggle('on', on);
        cell.setAttribute('aria-pressed', String(on));
      });
      alarmGrid.append(cell);
    });
    originalCells = data.Cells.slice();
    alarmBody.hidden = false;
    alarmBody.dataset.loaded = '1';
    alarmStatus.textContent = 'Settings read from the camera.';
  }

  async function loadAlarm() {
    alarmStatus.textContent = 'Reading settings from the camera…';
    alarmBody.hidden = true;
    try {
      const res = await fetch('/api/alarm', { signal: AbortSignal.timeout(30000) });
      const data = await res.json();
      if (!res.ok) throw new Error(data.detail || 'Camera unavailable');
      renderAlarm(data);
    } catch (error) {
      alarmBody.dataset.loaded = '';
      alarmStatus.textContent = 'This camera did not report motion alarm settings: ' + error.message;
    }
  }

  $('alarmReset').addEventListener('click', () => { if (alarmData) renderAlarm(alarmData); });

  $('alarmSave').addEventListener('click', async () => {
    const button = $('alarmSave');
    button.disabled = true;
    $('alarmResult').textContent = 'Saving…';
    try {
      const cells = [...alarmGrid.children].map(cell => cell.classList.contains('on') ? 1 : 0);
      const res = await fetch('/api/alarm', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ MotionAlarmSwitch: alarmMotion.checked, Cells: cells }),
        signal: AbortSignal.timeout(40000)
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) throw new Error((data && data.detail) || ('Save failed (' + res.status + ')'));
      renderAlarm(data);
      $('alarmResult').textContent = 'Saved. Camera now reports motion alarm ' + (data.MotionAlarmSwitch ? 'on' : 'off') + '.';
    } catch (error) {
      $('alarmResult').textContent = 'Could not save: ' + error.message;
    } finally {
      button.disabled = false;
    }
  });

  /* ---------- health ---------- */
  setInterval(async () => {
    try {
      const res = await fetch('/health', { signal: AbortSignal.timeout(4000) });
      const data = await res.json();
      if (data.state === 'streaming' && data.videoFresh) setStatus('live', 'ok');
      else setStatus(data.state || 'starting', 'warn');
    } catch (error) {
      setStatus('reconnecting', 'warn');
    }
  }, 5000);
})();
