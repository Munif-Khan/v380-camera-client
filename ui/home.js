(() => {
  const $ = id => document.getElementById(id);
  const list = $('cams');
  const empty = $('empty');
  const pill = $('summaryPill');
  const addSheet = $('addSheet');
  const manageSheet = $('manageSheet');
  const editSheet = $('editSheet');
  const toastEl = $('toast');

  let thumbTimers = [];
  let toastTimer = 0;
  let current = null;
  let signature = '';

  function toast(message) {
    toastEl.textContent = message;
    toastEl.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove('show'), 2600);
  }

  function anyDialogOpen() {
    return !!document.querySelector('dialog[open]');
  }

  function card(camera) {
    const wrap = document.createElement('div');
    wrap.className = 'cam-card';

    const link = document.createElement('a');
    link.className = 'open';
    link.href = camera.url;

    const thumb = document.createElement('div');
    thumb.className = 'thumb';
    const img = document.createElement('img');
    img.alt = 'Latest picture from ' + camera.name;
    img.loading = 'eager';
    const offline = document.createElement('div');
    offline.className = 'offline';
    offline.textContent = camera.online ? 'Camera offline' : 'Not responding';
    offline.hidden = camera.online;
    img.hidden = !camera.online;
    img.addEventListener('error', () => { img.hidden = true; offline.hidden = false; });
    img.addEventListener('load', () => { img.hidden = false; offline.hidden = true; });
    thumb.append(img, offline);

    const body = document.createElement('div');
    body.className = 'body';
    const grow = document.createElement('div');
    grow.className = 'grow';
    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = camera.name;
    const host = document.createElement('div');
    host.className = 'host';
    host.textContent = camera.host + (camera.online ? ' · live' : ' · offline');
    grow.append(name, host);
    const chevron = document.createElement('span');
    chevron.className = 'hint';
    chevron.textContent = '\u203A';
    chevron.style.fontSize = '22px';
    body.append(grow, chevron);
    link.append(thumb, body);

    const menu = document.createElement('button');
    menu.className = 'menu';
    menu.type = 'button';
    menu.textContent = '\u22EE';
    menu.setAttribute('aria-label', 'Manage ' + camera.name);
    menu.addEventListener('click', event => {
      event.preventDefault();
      openManage(camera);
    });

    wrap.append(link, menu);

    const refresh = () => {
      if (document.hidden || !camera.online) return;
      img.src = '/api/camera/' + encodeURIComponent(camera.id) + '/snapshot?t=' + Date.now();
    };
    refresh();
    thumbTimers.push(setInterval(refresh, 5000));

    return wrap;
  }

  function openManage(camera) {
    current = camera;
    $('manageTitle').textContent = camera.name;
    let status = 'live now';
    if (!camera.online) {
      const h = camera.health;
      status = h ? (h.state || 'not responding') : 'worker not running';
      if (h && h.lastError) status += ' · ' + h.lastError;
    }
    $('manageHint').textContent = camera.host + ' · device ' + camera.deviceId + ' · ' + status;
    manageSheet.showModal();
  }

  $('manageCancel').addEventListener('click', () => manageSheet.close());

  $('manageEdit').addEventListener('click', () => {
    if (!current) return;
    $('e-id').value = current.id;
    $('e-name').value = current.name;
    $('e-host').value = current.host;
    $('e-device').value = current.deviceId;
    $('e-user').value = current.username || '';
    $('e-pass').value = '';
    $('e-quality').value = current.quality || 'sd';
    manageSheet.close();
    editSheet.showModal();
  });

  $('manageRemove').addEventListener('click', async () => {
    if (!current) return;
    if (!confirm('Remove ' + current.name + ' from this computer? The camera itself is not changed.')) return;
    const id = current.id;
    manageSheet.close();
    try {
      const res = await fetch('/remove', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: 'id=' + encodeURIComponent(id),
        signal: AbortSignal.timeout(15000)
      });
      if (!res.ok) throw new Error('Remove failed (' + res.status + ')');
      toast('Camera removed');
      await load(true);
    } catch (error) {
      toast(error.message);
    }
  });

  async function load(force) {
    if (!force && anyDialogOpen()) return;
    try {
      const res = await fetch('/api/cameras', { cache: 'no-store', signal: AbortSignal.timeout(20000) });
      const data = await res.json();
      const next = JSON.stringify(data.map(c => [c.id, c.name, c.host, c.deviceId, c.quality, c.online]));
      // Rebuilding resets the thumbnails, so only redraw when something changed.
      if (next !== signature) {
        signature = next;
        thumbTimers.forEach(clearInterval);
        thumbTimers = [];
        list.replaceChildren(...data.map(card));
        empty.hidden = data.length > 0;
      }
      const online = data.filter(c => c.online).length;
      pill.textContent = data.length ? online + ' of ' + data.length + ' live' : 'no cameras';
      pill.className = 'pill ' + (online === data.length && data.length ? 'ok' : online ? 'warn' : 'bad');
    } catch (error) {
      pill.textContent = 'service unavailable';
      pill.className = 'pill bad';
    }
  }

  $('addBtn').addEventListener('click', () => addSheet.showModal());
  $('cancelAdd').addEventListener('click', () => addSheet.close());
  $('cancelEdit').addEventListener('click', () => editSheet.close());

  for (const sheet of [addSheet, manageSheet, editSheet]) {
    sheet.addEventListener('click', event => { if (event.target === sheet) sheet.close(); });
  }

  load(true);
  setInterval(load, 15000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) load(true); });
})();
